using StreamHub.Host.Runtime;

namespace StreamHub.Service;

/// <summary>Short-lived in-memory requests executed by the signed-in desktop process.</summary>
public sealed class ClipboardBroker
{
    private sealed class Pending(ClipboardJob job)
    {
        public ClipboardJob Job = job;
        public TaskCompletionSource<ClipboardPayload?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Delivered;
        public bool Claimed;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new();

    public async Task<ClipboardPayload?> RequestAsync(string mode, string leaseId, string clientId, string action, ClipboardPayload? payload, CancellationToken cancellationToken,
        Func<ClipboardJob, CancellationToken, Task>? dispatch = null)
    {
        if (action is not ("get" or "set") || (action == "set" && payload is null) || (action == "get" && payload is not null))
            throw new InvalidOperationException("剪贴板请求无效。");
        if (payload is not null) _ = ClipboardPolicy.Decode(payload);
        var job = new ClipboardJob(Guid.NewGuid().ToString("N"), mode, leaseId, clientId, action, payload, DateTimeOffset.UtcNow.AddSeconds(8));
        var pending = new Pending(job);
        lock (_gate)
        {
            if (_pending.Count >= 4) throw new InvalidOperationException("剪贴板操作繁忙，请稍后重试。");
            _pending.Add(job.Id, pending);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            if (dispatch is not null)
            {
                try { await dispatch(job, timeout.Token).ConfigureAwait(false); }
                // A helper may exit nonzero after reporting an empty/unsupported
                // clipboard. Preserve that specific result instead of hiding it
                // behind a generic process-launch failure.
                catch (InvalidOperationException) when (pending.Completion.Task.IsCompleted) { }
            }
            return await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("电脑剪贴板响应超时，请解锁电脑桌面后重试。"); }
        finally { lock (_gate) _pending.Remove(job.Id); }
    }

    public ClipboardJob? Take(string? id = null)
    {
        lock (_gate)
        {
            var pending = _pending.Values.FirstOrDefault(x => (id is null || x.Job.Id == id) && !x.Delivered && x.Job.ExpiresAt > DateTimeOffset.UtcNow);
            if (pending is null) return null;
            pending.Delivered = true; return pending.Job;
        }
    }

    public ClipboardJob? Claim(string? id)
    {
        lock (_gate)
        {
            if (id is null || !_pending.TryGetValue(id, out var pending) || !pending.Delivered || pending.Claimed ||
                pending.Job.ExpiresAt <= DateTimeOffset.UtcNow) return null;
            pending.Claimed = true;
            return pending.Job;
        }
    }

    public void Complete(string? id, bool success, ClipboardPayload? payload, string? error)
    {
        lock (_gate)
        {
            if (id is null || !_pending.TryGetValue(id, out var pending) || !pending.Claimed || pending.Job.ExpiresAt <= DateTimeOffset.UtcNow) return;
            if (!success) { pending.Completion.TrySetException(new InvalidOperationException(error is { Length: > 0 and <= 160 } ? error : "电脑剪贴板不可用。")); return; }
            if (pending.Job.Action == "get")
            {
                try { if (payload is null) throw new InvalidOperationException("电脑剪贴板没有可用内容。"); _ = ClipboardPolicy.Decode(payload); }
                catch (InvalidOperationException ex) { pending.Completion.TrySetException(ex); return; }
            }
            pending.Completion.TrySetResult(pending.Job.Action == "get" ? payload : null);
        }
    }
}
