namespace StreamHub.Host.Runtime;

/// <summary>Serializes maintenance with active file requests; session checks run under the runtime gate.</summary>
public sealed class MaintenanceWindow(TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset _until;
    private string? _id;
    private int _operations;
    public bool Active { get { lock (_gate) return _clock.GetUtcNow() < _until; } }
    public void Begin(string id, bool sessionActive)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("维护请求无效。");
        lock (_gate)
        {
            if (sessionActive || _operations != 0) throw new InvalidOperationException("请先结束串流并等待当前文件操作完成，再安装更新。");
            if (Active && _id != id) throw new InvalidOperationException("另一项维护正在进行，请稍后重试。");
            _id = id; _until = _clock.GetUtcNow().AddMinutes(2);
        }
    }
    public void End(string id) { lock (_gate) if (_id == id) { _id = null; _until = default; } }
    public IDisposable Enter()
    {
        lock (_gate)
        {
            if (Active) throw new InvalidOperationException("电脑正在安装更新，请稍后重试。");
            _operations++; return new Operation(this);
        }
    }
    private sealed class Operation(MaintenanceWindow owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (owner._gate) owner._operations--; }
    }
}
