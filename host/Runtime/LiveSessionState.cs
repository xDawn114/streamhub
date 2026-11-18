namespace StreamHub.Host.Runtime;

/// <summary>Authenticated receiver evidence, independent of the engine's stale BUSY flag.</summary>
public sealed class LiveSessionState
{
    public const int BoundedPrivacyTimeoutMs = 6000;
    private string? _lease, _client;
    private bool _ready;
    private bool _backendReleasing;
    private DateTimeOffset _expires;
    private bool _fastPrivacyHeartbeat;
    private DateTimeOffset _privacyExpires;
    private bool _boundedPrivacyHeartbeat;
    private readonly object _gate = new();
    private long _generation;
    public long Generation { get { lock (_gate) return _generation; } }
    public (string Lease, string Client)? LeaseIdentity
    { get { lock (_gate) return _lease is null ? null : (_lease, _client!); } }
    private readonly Dictionary<(string Lease, string Client), DateTimeOffset> _ended = new();
    public void Prepare(string lease, string client, DateTimeOffset now)
    {
        Validate(lease, client);
        lock (_gate)
        {
            if (_backendReleasing) throw new InvalidOperationException("上次远控会话正在回收，请先确认结束。");
            foreach (var key in _ended.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _ended.Remove(key);
            if (_ended.ContainsKey((lease, client))) throw new InvalidOperationException("会话已取消，请重新连接。");
            if (_lease == lease && _client == client && now < _expires) return;
            if (_lease is not null && now < _expires) throw new InvalidOperationException("已有设备正在连接或串流，请先结束当前会话。");
            _lease = lease; _client = client; _ready = _backendReleasing = false; _expires = now.AddSeconds(180);
            _generation++;
            _fastPrivacyHeartbeat = _boundedPrivacyHeartbeat = false; _privacyExpires = default;
        }
    }
    public long Heartbeat(string lease, string client, bool ready, DateTimeOffset now, bool fastPrivacyHeartbeat = false,
        bool boundedPrivacyHeartbeat = false, bool privacyGuardActive = false)
    {
        lock (_gate)
        {
            if (_lease != lease || _client != client || now >= _expires || _backendReleasing)
                throw new InvalidOperationException("会话已结束或过期，请重新连接。");
            // An overdue heartbeat cannot revive a stream already being stopped
            // by the independent privacy guard. Legacy leases keep their budget.
            if (_boundedPrivacyHeartbeat && privacyGuardActive && now >= _privacyExpires)
                throw new InvalidOperationException("隐私连接心跳已超时，请重新连接。");
            if (ready)
            {
                _ready = true; _expires = now.AddSeconds(25);
                if (fastPrivacyHeartbeat) _fastPrivacyHeartbeat = true;
                if (boundedPrivacyHeartbeat) _boundedPrivacyHeartbeat = _fastPrivacyHeartbeat = true;
                if (_fastPrivacyHeartbeat) _privacyExpires = now.AddMilliseconds(PrivacyTimeoutMs);
            }
            return _generation;
        }
    }
    public void End(string lease, string client)
    {
        lock (_gate)
        {
            Validate(lease, client);
            _ended[(lease, client)] = DateTimeOffset.UtcNow.AddMinutes(5);
            while (_ended.Count > 256) _ended.Remove(_ended.MinBy(p => p.Value).Key);
            if (_lease != lease || _client != client) return;
            _lease = _client = null; _ready = _backendReleasing = false; _expires = default;
            _generation++;
            _fastPrivacyHeartbeat = _boundedPrivacyHeartbeat = false; _privacyExpires = default;
        }
    }
    public void Reset() { lock (_gate) { if (_lease is not null) End(_lease, _client!); } }
    public bool HasLease(DateTimeOffset now) { lock (_gate) return _lease is not null && now < _expires; }
    // A prepared receiver owns its lease before video readiness is reported.
    public bool OwnsLease(string lease, string client, DateTimeOffset now)
    { lock (_gate) return _lease == lease && _client == client && now < _expires; }
    // Cleanup may arrive after heartbeat expiry. Under the runtime gate, exact
    // retained ownership is sufficient; Prepare/Reset replace or clear it.
    public bool OwnsLeaseForCleanup(string lease, string client)
    { lock (_gate) return _lease == lease && _client == client; }
    public long BeginBackendRelease(string lease, string client, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!OwnsLeaseForCleanup(lease, client) || _backendReleasing)
                throw new InvalidOperationException("原远控会话已结束或正在回收。");
            _backendReleasing = true;
            _ready = false;
            return _generation;
        }
    }
    public bool IsStreaming(DateTimeOffset now) { lock (_gate) return _ready && now < _expires; }
    public bool PrivacyHeartbeatExpired(DateTimeOffset now)
    { lock (_gate) return _ready && (now >= _expires || _fastPrivacyHeartbeat && now >= _privacyExpires); }
    public int PrivacyTimeoutMs { get { lock (_gate) return _boundedPrivacyHeartbeat ? BoundedPrivacyTimeoutMs : _fastPrivacyHeartbeat ? 12000 : 25000; } }
    public long PrivacyDeadlineTick(DateTimeOffset now)
    {
        lock (_gate) return Environment.TickCount64 + Math.Max(0,
            (long)((_fastPrivacyHeartbeat ? _privacyExpires : _expires) - now).TotalMilliseconds);
    }
    public bool IsStreaming(string lease, string client, DateTimeOffset now)
    { lock (_gate) return _ready && now < _expires && _lease == lease && _client == client; }
    private static void Validate(string lease, string client)
    {
        if (string.IsNullOrWhiteSpace(lease) || lease.Length > 128 || lease.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(client) || client.Length > 128 || client.Any(char.IsControl))
            throw new InvalidOperationException("会话标识无效。");
    }
}
