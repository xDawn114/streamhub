using StreamHub.Host.Runtime;

namespace StreamHub.Installer;

// Observe the normal service startup only. Never starts a session, enables a
// paused host, changes an active stream or creates a virtual display.
internal static class EncoderPrewarm
{
    internal static string? Outcome(RuntimeStatus? status)
    {
        if (status is null) return null;
        if (status.ConnectionsPaused || !status.Remote.Desired) return "skipped-paused";
        if (status.Remote.Streaming || status.Remote.BackendBusy || status.Controller is not null ||
            status.Remote.BackendReleasePending) return "deferred-active-session";
        if (!status.Remote.Healthy) return null;
        return status.Remote.EncoderCacheState switch
        {
            "ready" => "ready",
            "disabled" or "not-applicable" => "skipped-configuration",
            "unavailable" => "deferred-cache-unavailable",
            _ => "deferred-unsupported-engine"
        };
    }

    internal static async Task<string> WaitAsync(
        Func<CancellationToken, Task<RuntimeResponse>> query, TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        try
        {
            while (true)
            {
                var response = await query(timeout.Token).WaitAsync(timeout.Token);
                if (response.Success && Outcome(response.Status) is { } outcome) return outcome;
                await Task.Delay(TimeSpan.FromMilliseconds(400), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return "deferred-timeout"; }
    }
}
