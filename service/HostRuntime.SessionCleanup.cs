using System.Net;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed partial class HostRuntime
{
    private readonly RemoteBackendRelease _remoteBackendRelease;

    private void ResetRemoteSession()
    {
        if (_remoteBackendRelease is { } release && _remote.Process is { } engine)
            release.TrackEngineExit(_remoteSession, () => engine.Running);
        _remoteSession.Reset();
    }

    // Caller holds _gate through close, FREE readback and lease release. Never
    // grant a client permission to invoke Sunshine's global cancel afterwards.
    private async Task<BackendReleaseResult> ReleaseRemoteBackendAsync(RuntimeRequest request, bool shouldLock, CancellationToken token)
    {
        if (_remoteBackendRelease.ResolveReceipt(_remoteSession, request.LeaseId!, request.ClientId!) is { } receipt)
            return receipt;
        var engine = _remote.Process;
        if (engine is null || !engine.Running)
            return _remoteBackendRelease.CompleteExitedEngine(_remoteSession, request.LeaseId!, request.ClientId!, shouldLock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            return await _remoteBackendRelease.EndAsync(_remoteSession, request.LeaseId!, request.ClientId!, engine,
                () => engine.Running,
                (sent, completed, operation) => _backend.CloseAppAsync("remote", sent, completed, operation),
                ReadRemoteBackendFreeAsync, deadline.Token, shouldLock);
        }
        finally
        {
            if (_remoteBackendRelease.Pending)
            {
                // BeginBackendRelease suppresses ready evidence even on failure.
                // Reconcile independently of a cancelled caller, within one second.
                using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await ReconcilePrivacyScreenAsync(reconcile.Token); }
                catch (Exception) { Log("远控结束尚未确认，隐私状态将由监督任务继续核对。"); }
            }
        }
    }

    private async Task<bool> ReadRemoteBackendFreeAsync(CancellationToken token)
    {
        using var response = await _probe.GetAsync($"http://127.0.0.1:{ProfileStore.Port("remote")}/serverinfo", token);
        if (response.StatusCode != HttpStatusCode.OK) return false;
        return RemoteBackendRelease.IsFree(await response.Content.ReadAsStringAsync(token));
    }
}
