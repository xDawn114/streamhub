using System.Xml;
using System.Xml.Linq;
using System.Text.Json;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed class BackendReleaseException(int status, string message) : InvalidOperationException(message)
{
    internal int Status { get; } = status;
}

internal sealed record BackendReleaseResult(bool ShouldLock, bool AlreadyCompleted = false, bool Obsolete = false);

// All access is serialized by HostRuntime._gate, including the injected HTTP callbacks.
// This fence belongs to the actual engine instance, independently of lease expiry/reset.
internal sealed class RemoteBackendRelease
{
    private sealed class Release(LiveSessionState session, object engine, Func<bool> alive, string lease, string client, long generation, bool lockOnExit)
    {
        internal readonly LiveSessionState Session = session;
        internal readonly object Engine = engine;
        internal readonly Func<bool> Alive = alive;
        internal readonly string Lease = lease, Client = client;
        internal readonly long Generation = generation;
        internal readonly bool LockOnExit = lockOnExit;
        internal bool MutationSent, HttpCompleted, Acknowledged;
    }

    private Release? _release;
    private sealed record Receipt(string LeaseId, string ClientId);
    private sealed record Exiting(string Lease, string Client, Func<bool> Alive);
    private readonly List<Exiting> _exiting = new();
    private readonly string? _receiptPath;
    // Bounded receipts outlive heartbeat/tombstone expiry. They describe only
    // this exact completed end, never the state of a subsequent backend session.
    private readonly HashSet<(string Lease, string Client)> _completed = new();
    private readonly Queue<(string Lease, string Client)> _completedOrder = new();
    internal bool Pending => _release is not null || _exiting.Count != 0;

    internal RemoteBackendRelease(string? receiptPath = null)
    {
        _receiptPath = receiptPath;
        if (receiptPath is null || !File.Exists(receiptPath)) return;
        try
        {
            if (new FileInfo(receiptPath).Length > 128 * 1024) return;
            var receipts = JsonSerializer.Deserialize<Receipt[]>(File.ReadAllText(receiptPath), RuntimeProtocol.JsonOptions);
            if (receipts is null || receipts.Length > 256 || receipts.Any(x => x is null ||
                string.IsNullOrWhiteSpace(x.LeaseId) || x.LeaseId.Length > 128 || x.LeaseId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(x.ClientId) || x.ClientId.Length > 128 || x.ClientId.Any(char.IsControl))) return;
            foreach (var receipt in receipts)
                if (_completed.Add((receipt.LeaseId, receipt.ClientId))) _completedOrder.Enqueue((receipt.LeaseId, receipt.ClientId));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    // Capture before an administrative reset loses the lease, including an
    // expired one. A receipt is written only after that same engine really exits.
    internal void TrackEngineExit(LiveSessionState session, Func<bool> alive)
    {
        if (session.LeaseIdentity is not { } identity || Completed(identity.Lease, identity.Client)) return;
        if (_exiting.Any(x => x.Lease == identity.Lease && x.Client == identity.Client)) return;
        _exiting.Add(new(identity.Lease, identity.Client, alive));
    }

    internal void ObserveEngineExit()
    {
        foreach (var exiting in _exiting.Where(x => !x.Alive()).ToArray())
        {
            RememberCompleted(exiting.Lease, exiting.Client);
            _exiting.Remove(exiting);
        }
        // Replacing a reference or resetting the control lease is not proof of exit.
        if (_release is { } release && !release.Alive())
        {
            release.Session.End(release.Lease, release.Client);
            RememberCompleted(release.Lease, release.Client);
            _release = null;
        }
    }

    internal bool Completed(string lease, string client) => _completed.Contains((lease, client));

    // Pure receipt resolution: obsolete says only that this request has no
    // current ownership. It does not observe engine liveness or claim FREE.
    internal BackendReleaseResult? ResolveReceipt(LiveSessionState session, string lease, string client)
    {
        if (Completed(lease, client)) return new(false, true);
        if (Pending)
        {
            if (_release is not { } release || release.Lease != lease || release.Client != client)
                throw new BackendReleaseException(409, "上次远控结束尚未确认，未决回收不能被旧请求绕过。");
            return null;
        }
        return session.OwnsLeaseForCleanup(lease, client) ? null : new(false, Obsolete: true);
    }

    private void RememberCompleted(string lease, string client)
    {
        if (!_completed.Add((lease, client))) return;
        _completedOrder.Enqueue((lease, client));
        while (_completedOrder.Count > 256) _completed.Remove(_completedOrder.Dequeue());
        if (_receiptPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_receiptPath)!);
            var temporary = _receiptPath + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_completedOrder.Select(x => new Receipt(x.Lease, x.Client)), RuntimeProtocol.JsonOptions));
            File.Move(temporary, _receiptPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Keep the proven in-memory receipt. Failed persistence grants nothing on restart. */ }
    }

    internal BackendReleaseResult CompleteExitedEngine(LiveSessionState session, string lease, string client, bool shouldLock)
    {
        if (ResolveReceipt(session, lease, client) is { } receipt) return receipt;
        EnsureCanPrepareOrRelease();
        if (Completed(lease, client)) return new(false, true);
        if (!session.OwnsLeaseForCleanup(lease, client))
            throw new BackendReleaseException(409, "原远控会话已结束或不属于此设备。");
        session.End(lease, client);
        RememberCompleted(lease, client);
        return new(shouldLock);
    }

    internal void EnsureCanPrepareOrRelease()
    {
        ObserveEngineExit();
        if (Pending) throw new BackendReleaseException(409, "正在确认上次远控会话已结束，请重试结束请求后再连接。");
    }

    internal void EnsureCanPrepare(string lease, string client)
    {
        EnsureCanPrepareOrRelease();
        if (Completed(lease, client)) throw new BackendReleaseException(409, "此会话已经结束，请使用新的会话标识连接。");
    }

    internal async Task<BackendReleaseResult> EndAsync(LiveSessionState session, string lease, string client,
        object engine, Func<bool> alive,
        Func<Action, Action, CancellationToken, Task> close,
        Func<CancellationToken, Task<bool>> isFree, CancellationToken token, bool lockOnExit = false,
        Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        token.ThrowIfCancellationRequested();
        // A lost client response may retry a completed end after a new device
        // connected. Acknowledge that old end without another close or lock.
        if (ResolveReceipt(session, lease, client) is { } receipt) return receipt;
        ObserveEngineExit();
        if (Completed(lease, client)) return new(false, true);
        var release = _release;
        if (release is null)
        {
            if (!session.OwnsLeaseForCleanup(lease, client) || !alive())
                throw new BackendReleaseException(409, "原远控会话已结束或不属于此设备，未关闭串流应用。");
            // Set the fence before any backend mutation can be submitted.
            var generation = session.BeginBackendRelease(lease, client, DateTimeOffset.UtcNow);
            _release = release = new(session, engine, alive, lease, client, generation, lockOnExit);
        }
        else if (release.Lease != lease || release.Client != client || !ReferenceEquals(release.Engine, engine))
            throw new BackendReleaseException(409, "上次远控结束尚未确认，此请求不属于待回收的会话。");

        // A second close cannot establish that an earlier, timed-out close will
        // not execute later. Only an actual engine exit resolves that ambiguity.
        if (release.MutationSent && !release.HttpCompleted)
            throw new BackendReleaseException(503, "串流应用关闭结果未知，当前引擎仍禁止新连接；请在电脑端处理。");
        if (!release.Acknowledged && session.Generation != release.Generation)
            throw new BackendReleaseException(409, "会话状态已改变，未再次关闭串流应用。");

        try
        {
            if (!release.Acknowledged)
            {
                await close(() => { release.MutationSent = true; release.HttpCompleted = false; },
                    () => release.HttpCompleted = true, token).ConfigureAwait(false);
                release.Acknowledged = true;
            }

            // closeApp terminates the launched app, not necessarily RTSP sessions.
            // An ACK is necessary but cannot replace a positively observed FREE.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!release.Alive())
                    throw new BackendReleaseException(409, "原串流引擎已退出，请重新连接。");
                if (await isFree(token).ConfigureAwait(false))
                {
                    session.End(lease, client);
                    _release = null;
                    RememberCompleted(lease, client);
                    return new(release.LockOnExit);
                }
                if (attempt < 7)
                    await (pause ?? Task.Delay)(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
            throw new BackendReleaseException(409, "串流应用关闭已确认，但电脑仍未确认空闲，请重试结束请求。");
        }
        catch (BackendReleaseException) { throw; }
        catch (OperationCanceledException)
        { throw new BackendReleaseException(504, "远控结束确认超时或已取消，当前引擎仍禁止新连接。"); }
        catch (Exception)
        { throw new BackendReleaseException(503, "未能确认远控已结束，当前引擎仍禁止新连接，请重试结束请求。"); }
    }

    internal static bool IsFree(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            var states = root?.Elements("state").ToArray();
            var games = root?.Elements("currentgame").ToArray();
            return root?.Name == "root" && root.Attribute("status_code")?.Value == "200" &&
                states is { Length: 1 } && !states[0].HasElements && states[0].Value == "SUNSHINE_SERVER_FREE" &&
                games is { Length: 1 } && !games[0].HasElements && games[0].Value == "0";
        }
        catch (XmlException) { return false; }
    }
}
