namespace StreamHub.Host.Runtime;

internal sealed record CaptureReadinessResult(long ElapsedMs, long FirstServerInfo200Ms, int Polls);

/// <summary>Wait through unavailable status samples within one fixed capture startup budget.</summary>
internal sealed class CaptureReadinessPolicy(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<CaptureReadinessResult> WaitAsync(string profile, Func<CancellationToken, Task<RuntimeResponse>> readStatus,
        CancellationToken cancellationToken)
    {
        if (profile is not ("remote" or "tablet")) throw new ArgumentException("Unknown capture profile.", nameof(profile));
        // Bound the status request itself as well as polling. A relative deadline
        // checked only after each request can overrun or reset during warmup.
        using var deadline = new CancellationTokenSource(MaximumWait, _time);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = pending.Token;
        var started = _time.GetTimestamp();
        var polls = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // RuntimeClient reports its own five-second IPC timeout and
                // unavailable service as a failed response. During capture these
                // are missing samples, not proof that initialization has failed.
                polls++;
                var response = await readStatus(token).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (response.Success && response.Status is { } status &&
                    (profile == "tablet" ? status.Tablet : status.Remote).Healthy)
                {
                    var elapsed = Math.Max(0, (long)_time.GetElapsedTime(started).TotalMilliseconds);
                    return new CaptureReadinessResult(elapsed, elapsed, polls);
                }
                await Task.Delay(PollInterval, _time, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Capture did not become healthy within the startup budget.");
        }
    }
}
