using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed partial class HostRuntime
{
    private AssistanceRegistry? _assistance;
    private AssistanceMediaProfile? _assistanceMedia;
    private readonly Engine _assistanceEngine = new("assistance", false);
    private DateTimeOffset _assistanceCleanupRetry;
    private DateTimeOffset _assistancePersistenceRetry;
    private string? _assistanceFailure;
    private bool AssistanceReserved => _assistance?.Reserved == true;
    private string AssistanceDirectory => Path.Combine(_data, "assistance");
    private void InitializeAssistance()
    {
        try
        {
            Directory.CreateDirectory(AssistanceDirectory);
            string path = Path.Combine(AssistanceDirectory, "journal.json");
            byte[]? recovered = File.Exists(path) ? File.ReadAllBytes(path) : null;
            static void Save(string target, byte[] bytes)
            {
                string temporary = target + ".new";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
                File.Move(temporary, target, true);
            }
            string trustPath = Path.Combine(AssistanceDirectory, "trust.json");
            var trust = new AssistanceTrustRegistry(bytes => Save(trustPath, bytes),
                File.Exists(trustPath) ? File.ReadAllBytes(trustPath) : null);
            _assistance = new(bytes => Save(path, bytes), (action, source, id, mode) => {
                // No code, proof, certificate, private key or raw peer data in audit.
                File.AppendAllText(Path.Combine(AssistanceDirectory, "audit.jsonl"), JsonSerializer.Serialize(new {
                    checkedAt = DateTimeOffset.UtcNow, action, source, sessionId = id, mode
                }) + "\n", new UTF8Encoding(false));
            }, recovered, trust: trust);
            _assistanceMedia = new(_data);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException)
        {
            _assistance = null;
            _assistanceFailure = "临时协助恢复记录不可用，请修复后重试；已绑定设备仍可使用。";
            Log(_assistanceFailure);
        }
    }
    private AssistanceSnapshot AssistanceStatus()
    {
        var snapshot = _assistance?.Snapshot() ?? new("unavailable", Message: _assistanceFailure ?? "临时协助尚未初始化");
        return snapshot.State == "cleanup-pending" && _assistanceFailure is not null
            ? snapshot with { Message = snapshot.PersistencePending ? snapshot.Message + " " + _assistanceFailure : _assistanceFailure } : snapshot;
    }

    // Called only with the runtime gate held; owner commands never have HTTP routes.
    private async Task<RuntimeResponse> AssistanceCommandAsync(RuntimeRequest request, CancellationToken token)
    {
        var registry = _assistance ?? throw new InvalidOperationException(_assistanceFailure ?? "临时协助不可用。");
        var command = request.Command.ToLowerInvariant();
        bool peer = command is "assistance-join" or "assistance-trusted-challenge" or "assistance-trusted-join" or
            "assistance-poll" or "assistance-heartbeat" or "assistance-peer-end";
        if (!peer && (request.AssistanceSource is not null || request.ControllerCredentialId is not null))
            throw new UnauthorizedAccessException("此协助操作只能由本机所有者执行。");
        if (command is "assistance-disable" or "assistance-trust-revoke" ||
            command == "assistance-enable" && request.AssistanceEnabled == false)
        {
            try
            {
                if (command == "assistance-trust-revoke") registry.Revoke(request.ClientId ?? request.RequestId ?? "");
                else registry.SetEnabled(false);
            }
            catch (Exception error) when (registry.PersistencePending &&
                error is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
            {
                _assistancePersistenceRetry = DateTimeOffset.UtcNow.AddSeconds(3);
                LogAssistancePersistenceFailure(error);
            }
            // Disk failure must never prevent stopping already-revoked media.
            await ReconcileAssistanceAsync(token);
            var snapshot = AssistanceStatus();
            return new(!snapshot.PersistencePending, snapshot.Message,
                PayloadJson: JsonSerializer.Serialize(snapshot, RuntimeProtocol.JsonOptions),
                ErrorStatus: snapshot.PersistencePending ? 503 : null);
        }
        if (registry.PersistencePending && (command is "assistance-enable" or "assistance-start" ||
            request.TrustDevice && command is "assistance-approve-view" or "assistance-approve-control"))
            return new(false, "权限撤销尚未保存，请等待后台重试完成。",
                PayloadJson: JsonSerializer.Serialize(AssistanceStatus(), RuntimeProtocol.JsonOptions), ErrorStatus: 409);
        object? payload = null;
        if (command is "assistance-start" or "assistance-login" or "assistance-refresh" or "assistance-enable" or "assistance-session-refresh")
        {
            // Offers never stop or reserve ordinary remote, tablet or file resources.
            bool enabled = command == "assistance-enable" ? request.AssistanceEnabled ?? true :
                command == "assistance-start" || registry.Snapshot().Enabled;
            if (enabled)
            {
                await EnsureControlApiAsync(token);
                if (_controlApi?.IsRunning != true) throw new InvalidOperationException("连接接口尚未就绪。");
            }
            payload = command switch {
                "assistance-login" or "assistance-session-refresh" => registry.Login(request.RequestId ?? ""),
                "assistance-start" => registry.Start(),
                "assistance-enable" => registry.SetEnabled(request.AssistanceEnabled ?? true),
                _ => registry.RefreshOffer(force: true)
            };
        }
        else if (command == "assistance-join")
        {
            await EnsureAssistanceIdleAsync(token);
            payload = registry.Join(request.AssistanceCode ?? "", request.Name ?? "", request.AssistanceProof ?? "",
                request.ClientCertificate ?? "", request.AssistanceSource ?? "unknown", request.ClientId, request.AssistanceCredential);
        }
        else if (command == "assistance-trusted-challenge")
        {
            if (_connectionsPaused || _maintenance.Active) throw new InvalidOperationException("电脑暂不接受协助请求。");
            payload = registry.Challenge(request.ClientId ?? "", request.AssistanceMode ?? "view");
        }
        else if (command == "assistance-trusted-join")
        {
            await EnsureAssistanceIdleAsync(token);
            registry.JoinTrusted(request.ClientId ?? "", request.AssistanceCredential ?? "",
                request.AssistanceChallengeId ?? "", request.AssistanceAuthProof ?? "", request.AssistanceMode ?? "view",
                request.Name ?? "", request.AssistanceProof ?? "", request.ClientCertificate ?? "", request.AssistanceSource ?? "unknown");
            await StartAssistanceAsync(request with { RequestId = registry.Snapshot().SessionId }, registry, token);
            payload = AssistancePeerReply(registry.Poll(registry.Snapshot().SessionId!, request.AssistanceProof ?? ""));
        }
        else if (command is "assistance-approve-view" or "assistance-approve-control")
        {
            await StartAssistanceAsync(request with { AssistanceMode = command == "assistance-approve-control" ? "control" : "view" }, registry, token);
            payload = registry.Snapshot();
        }
        else if (command is "assistance-poll" or "assistance-heartbeat")
        {
            AssistanceReply reply = command == "assistance-poll"
                ? registry.Poll(request.RequestId ?? "", request.AssistanceProof ?? "")
                : registry.Heartbeat(request.RequestId ?? "", request.AssistanceProof ?? "");
            payload = AssistancePeerReply(reply);
        }
        else if (command is "assistance-end" or "assistance-peer-end")
        {
            if (command == "assistance-peer-end") registry.EndRemote(request.RequestId ?? "", request.AssistanceProof ?? "");
            else
            {
                if (request.RequestId is not null && request.RequestId != registry.Snapshot().SessionId)
                    throw new InvalidOperationException("该协助请求已经结束，未影响其他会话。");
                registry.End("owner-ended");
            }
            await ReconcileAssistanceAsync(token);
            payload = registry.Snapshot();
        }
        else if (command is "assistance-status" or "assistance-trust-list" or "assistance-trusted-list") payload = registry.Snapshot();
        else if (command == "assistance-connection-info")
        {
            await EnsureControlApiAsync(token);
            payload = new { connectionInfo = _controlApi?.GetAssistanceConnectionInfo() ?? throw new InvalidOperationException("连接接口未就绪。") };
        }
        else throw new InvalidOperationException("没有此协助操作。");
        return new(true, registry.Snapshot().Message, PayloadJson: JsonSerializer.Serialize(payload, RuntimeProtocol.JsonOptions));
    }
    private AssistanceReply AssistancePeerReply(AssistanceReply reply) => reply with {
        HostName = Environment.MachineName,
        ServerCertificate = reply.State == "active" ? _assistanceMedia!.ServerCertificate : null,
        MediaHostId = reply.State == "active" ? _assistance!.Journal.SessionId : null
    };

    private void LogAssistancePersistenceFailure(Exception error)
    {
        try { Log("协助权限撤销保存失败，等待重试：" + error.GetType().Name); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }

    private async Task EnsureAssistanceIdleAsync(CancellationToken token)
    {
        if (_connectionsPaused || _maintenance.Active) throw new InvalidOperationException("电脑暂不接受协助请求。");
        if (_remoteSession.HasLease(DateTimeOffset.UtcNow) || _tabletLeaseId is not null || _remoteBackendRelease.Pending)
            throw new InvalidOperationException("请先结束当前远控或副屏会话，再接受协助。");
        foreach (var engine in new[] { _remote, _tablet })
        {
            var status = await GetEngineStatusAsync(engine, token);
            if (status.BackendBusy || status.Running && !status.Healthy)
                throw new InvalidOperationException("串流引擎尚未确认空闲，请稍后再接受协助。");
        }
    }

    private async Task StartAssistanceAsync(RuntimeRequest request, AssistanceRegistry registry, CancellationToken token)
    {
        await EnsureAssistanceIdleAsync(token);
        registry.Approve(request.RequestId ?? "", request.AssistanceMode ?? "view", request.TrustDevice,
            request.TrustAllowView, request.TrustAllowControl);
        try
        {
            // Only an accepted session acquires exclusive media resources.
            await StopEngineAsync(_remote, token);
            await StopEngineAsync(_tablet, token);
            if (_remote.Process?.Running == true || _tablet.Process?.Running == true)
                throw new IOException("串流引擎仍在退出，请稍后重试。");
            await _assistanceMedia!.PrepareAsync(registry.Journal, token);
            string executable = _profiles.ExecutableFor("remote");
            _assistanceEngine.Process = SessionLauncher.Start(executable, [_assistanceMedia.ConfigPath],
                Path.GetDirectoryName(executable)!, _interactive);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            bool ready = false;
            while (_assistanceEngine.Process.Running && DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    string xml = await _probe.GetStringAsync("http://127.0.0.1:48989/serverinfo", token);
                    var root = XDocument.Parse(xml).Root;
                    ready = root?.Attribute("status_code")?.Value == "200" && root.Element("uniqueid")?.Value == registry.Journal.SessionId;
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
                { token.ThrowIfCancellationRequested(); }
                if (ready) break;
                await Task.Delay(250, token);
            }
            if (!ready) throw new IOException("临时协助引擎未就绪，已取消授权并开始清理。");
            registry.Activate(commitTrust: false);
            ReconcileSessionNotice();
            if (_sessionNotice?.Running != true) throw new IOException("被控提示无法显示，本次协助已取消。");
            registry.CommitTrust();
        }
        catch
        {
            registry.End("prepare-failed");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ReconcileAssistanceAsync(cleanup.Token);
            throw;
        }
    }
    private async Task ReconcileAssistanceAsync(CancellationToken token)
    {
        if (_assistance is null) return;
        if (_assistance.PersistencePending && DateTimeOffset.UtcNow >= _assistancePersistenceRetry)
        {
            try { _assistance.RetryPersistence(); _assistancePersistenceRetry = default; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
            {
                _assistancePersistenceRetry = DateTimeOffset.UtcNow.AddSeconds(3);
                LogAssistancePersistenceFailure(error);
            }
        }
        _assistance.RefreshOffer();
        var status = _assistance.Snapshot();
        if (status.State == "active" && (_assistanceEngine.Process?.Running != true ||
            _sessionNotice?.Running != true || !_interactive && _assistanceEngine.Process.SessionId != SessionLauncher.ActiveSession))
        { _assistance.End("host-session-lost"); status = _assistance.Snapshot(); }
        if (status.State != "cleanup-pending" || DateTimeOffset.UtcNow < _assistanceCleanupRetry) return;
        try
        {
            await StopEngineAsync(_assistanceEngine, token);
            if (_assistanceEngine.Process?.Running == true) throw new IOException("协助引擎尚未停止。");
            await _assistanceMedia!.CleanupAsync(_assistance.Journal, token);
            _assistance.Cleaned(); _assistanceFailure = null;
            _remote.RetryAt = DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidOperationException)
        {
            _assistanceFailure = "协助配对清理未完成，正在重试；暂不允许新协助。";
            _assistanceCleanupRetry = DateTimeOffset.UtcNow.AddSeconds(3);
            Log(_assistanceFailure);
        }
    }
}
