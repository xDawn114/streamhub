namespace StreamHub.Updates;

// A shell elevation request cannot safely be cancelled once submitted. A wait
// timeout only detaches the caller; this same operation still owns the package
// lock and the eventual result, so the caller must not submit a second request.
public sealed class UpdateLaunchOperation<T> where T : class
{
    public Task<T> Started { get; }
    public UpdateLaunchOperation(Func<T> launch, IDisposable? packageLock, Action<Action> schedule,
        Action<string, Exception?>? observe = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Started = completion.Task;
        void Report(string phase, Exception? error = null)
        {
            // Diagnostic failures must never change the elevation request or its lock ownership.
            try { observe?.Invoke(phase, error); } catch { }
        }
        try
        {
            Report("queued");
            schedule(() =>
            {
                try
                {
                    Report("shell-entered");
                    T result;
                    using (packageLock) result = launch();
                    Report("shell-returned");
                    completion.TrySetResult(result);
                }
                catch (Exception error) { Report("shell-failed", error); completion.TrySetException(error); }
            });
        }
        catch (Exception error) { packageLock?.Dispose(); Report("schedule-failed", error); completion.TrySetException(error); }
    }
    public Task<T> WaitForStartAsync(TimeSpan timeout, CancellationToken token = default) => Started.WaitAsync(timeout, token);
}
