namespace StreamHub.Service;

/// <summary>A private stop event shared by the supervisor and its desktop helper.</summary>
internal sealed class PrivacyScreenLifetime : IDisposable
{
    private const string Prefix = @"Global\StreamHub.PrivacyStop.";
    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _ready;
    private readonly EventWaitHandle _heartbeat;
    public string Name { get; }
    public long Generation { get; private set; }
    public bool IsReady => _ready.WaitOne(0);

    private PrivacyScreenLifetime(string name, EventWaitHandle stop, EventWaitHandle ready, EventWaitHandle heartbeat) =>
        (Name, _stop, _ready, _heartbeat) = (name, stop, ready, heartbeat);

    public static bool IsValidName(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) &&
        name.Length == Prefix.Length + 32 && Guid.TryParseExact(name[Prefix.Length..], "N", out _);

    public static PrivacyScreenLifetime Create(long generation = 0)
    {
        var name = Prefix + Guid.NewGuid().ToString("N");
        // The service and SessionLauncher helper use the same SYSTEM token.
        // The creator's default DACL protects this random, per-helper event.
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, name, out var created);
        if (!created) { stop.Dispose(); throw new IOException("隐私黑屏退出事件已存在。"); }
        try
        {
            var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Ready", out created);
            if (!created) { ready.Dispose(); throw new IOException("隐私黑屏就绪事件已存在。"); }
            try
            {
                var heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Heartbeat", out created);
                if (created) return new(name, stop, ready, heartbeat) { Generation = generation };
                heartbeat.Dispose();
                throw new IOException("隐私黑屏心跳事件已存在。");
            }
            catch { ready.Dispose(); throw; }
        }
        catch { stop.Dispose(); throw; }
    }

    public static EventWaitHandle Open(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("隐私黑屏退出事件无效。", nameof(name));
        return EventWaitHandle.OpenExisting(name);
    }

    public static EventWaitHandle OpenReady(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("隐私黑屏就绪事件无效。", nameof(name));
        return EventWaitHandle.OpenExisting(name + ".Ready");
    }

    public void RequestStop() => _stop.Set();
    public static EventWaitHandle OpenHeartbeat(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("隐私黑屏心跳事件无效。", nameof(name));
        return EventWaitHandle.OpenExisting(name + ".Heartbeat");
    }
    public void Heartbeat()
    {
        try { _heartbeat.Set(); }
        catch (ObjectDisposedException) { /* The retiring helper must not break a valid receiver heartbeat. */ }
    }
    public void Dispose() { _heartbeat.Dispose(); _ready.Dispose(); _stop.Dispose(); }
}
