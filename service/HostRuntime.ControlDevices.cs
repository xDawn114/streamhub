using System.Text.Json;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed partial class HostRuntime
{
    private ControlDeviceRegistry? _controlDevices;
    private ControllerSession? _remoteController, _tabletController;
    private OwnedProcess? _sessionNotice;
    private DateTimeOffset _noticeRetryAt;
    private long _reconciledDeviceRevision = -1;

    private ControlPrincipal? ValidateController(RuntimeRequest request)
    {
        if (request.ControllerCredentialId is null) return null; // Owner-authenticated local IPC / internal cleanup.
        var principal = (_controlDevices ?? throw new UnauthorizedAccessException("设备授权尚未就绪。"))
            .Resolve(request.ControllerCredentialId);
        ControlDeviceRegistry.EnsureClient(principal, request.ClientId);
        ControlScope scope = request.Profile == "tablet" ? ControlScope.Tablet : ControlScope.Remote;
        if (request.Command == "clipboard-authorize") scope |= ControlScope.Clipboard;
        ControlDeviceRegistry.EnsureScope(principal, scope);
        return principal;
    }

    private static ControllerSession? Controller(RuntimeRequest request, ControlPrincipal? principal) =>
        principal is null ? null : new(principal.Id, principal.Name,
            request.ControllerChannel is "异地安全隧道" ? "异地安全隧道" : "局域网 / USB", !principal.Legacy);

    private async Task<string> DeviceCommandAsync(RuntimeRequest request, CancellationToken token)
    {
        await EnsureControlApiAsync(token);
        if (_controlApi?.IsRunning != true) throw new InvalidOperationException("设备授权接口尚未就绪。");
        string snapshot = _controlApi.DeviceCommand(request);
        await ReconcileDeviceGrantsAsync(token);
        return snapshot;
    }

    private async Task ReconcileDeviceGrantsAsync(CancellationToken token)
    {
        if (_controlDevices is null || _reconciledDeviceRevision == _controlDevices.Revision) return;
        long revision = _controlDevices.Revision;
        // Commit first. Even if engine cleanup fails, subsequent HTTP/IPC operations
        // cannot reuse a credential or a scope that has just been removed.
        bool Disallowed(ControllerSession? session, ControlScope scope)
        {
            if (session is null) return false;
            try { ControlDeviceRegistry.EnsureScope(_controlDevices!.Resolve(session.CredentialId), scope); return false; }
            catch (UnauthorizedAccessException) { return true; }
        }
        if (Disallowed(_remoteController, ControlScope.Remote)) await DisconnectRemoteAsync(token);
        if (Disallowed(_tabletController, ControlScope.Tablet))
            await EndTabletSessionAsync("设备接口授权已撤销，副屏已断开。", token, allowWarm: false);
        foreach (var engine in new[] { _remote, _tablet })
        {
            if (_controlDevices.PendingMediaPairing(engine.Profile) is not null)
            {
                if (engine == _remote) await DisconnectRemoteAsync(token);
                else await EndTabletSessionAsync("媒体配对正在安全回收，副屏已断开。", token, allowWarm: false);
                if (engine.Process?.Running == true) throw new IOException("串流引擎尚未停止，媒体配对回收将重试。");
                await new DeviceMediaPairing(_controlDevices, _profiles).RecoverAsync(engine.Profile, token);
            }
            string[] blocked = _controlDevices.BlockedMedia(engine.Profile);
            if (blocked.Length == 0) continue;
            // Check exact persisted identities before interrupting an unrelated stream.
            var trust = await _profiles.ReadMediaTrustAsync(engine.Profile, token);
            if (!trust.Any(c => blocked.Any(b => c.Certificate.AsSpan().SequenceEqual(Convert.FromBase64String(b))))) continue;
            if (engine == _remote) await DisconnectRemoteAsync(token);
            else await EndTabletSessionAsync("媒体授权正在撤销，副屏已断开。", token, allowWarm: false);
            if (engine.Process?.Running == true) throw new IOException("串流引擎尚未停止，媒体授权撤销将重试。");
            await _profiles.RemoveMediaTrustAsync(engine.Profile, blocked, token);
        }
        _reconciledDeviceRevision = revision;
    }

    private async Task DisconnectRemoteAsync(CancellationToken token)
    {
        ResetRemoteSession(); _remoteController = null;
        await StopPrivacyScreenAsync(token);
        await StopEngineAsync(_remote, token);
        _remote.RetryAt = DateTimeOffset.UtcNow;
    }

    private void ReconcileSessionNotice()
    {
        var now = DateTimeOffset.UtcNow;
        bool active = _remote.Process?.Running == true && _remoteSession.IsStreaming(now) ||
            _tablet.Process?.Running == true && _tabletStreamingSeen && _tabletLeaseId is not null && !TabletLeaseExpired(now);
        if (_sessionNotice is not null && (!active || !_sessionNotice.Running ||
            !_interactive && _sessionNotice.SessionId != SessionLauncher.ActiveSession))
        { _sessionNotice.Dispose(); _sessionNotice = null; }
        if (!active || _sessionNotice is not null || now < _noticeRetryAt) return;
        try
        {
            _sessionNotice = SessionLauncher.Start(Path.Combine(_root, "我控制.exe"), ["--session-notice"],
                _root, _interactive, _ownerSid?.Value ?? throw new InvalidOperationException("授权用户不可用。"), showWithoutActivation: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { _noticeRetryAt = now.AddSeconds(10); Log("持续被控提示暂时无法显示，将自动重试。"); }
    }
}
