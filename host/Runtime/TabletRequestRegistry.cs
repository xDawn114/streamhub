namespace StreamHub.Host.Runtime;

// In-memory rendezvous only. Publishing/accepting a request never attaches a display.
public sealed class TabletRequestRegistry(Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TabletReceiver> _receivers = new();
    private readonly Dictionary<string, TabletConnectionRequest> _requests = new();
    private readonly Dictionary<string, string> _leases = new();
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public static bool Active(string state) => state is "waiting" or "accepted" or "connecting" or "connected";

    public ReceiverSnapshot Snapshot(string? requestId = null)
    {
        lock (_gate)
        {
            Prune();
            var request = requestId is not null ? _requests.GetValueOrDefault(requestId) : _requests.Values.LastOrDefault();
            return new(_receivers.Values.OrderBy(x => x.Name).ToArray(), request);
        }
    }

    public TabletConnectionRequest? Poll(TabletReceiver receiver)
    {
        lock (_gate)
        {
            Prune();
            if (!_receivers.ContainsKey(receiver.ClientId) && _receivers.Count >= 64)
                throw new InvalidOperationException("在线接收设备过多，请稍后重试。");
            _receivers[receiver.ClientId] = receiver with { LastSeen = Now };
            return _requests.Values.LastOrDefault(x => x.ClientId == receiver.ClientId);
        }
    }

    /// <summary>
    /// Returns the most recent display capabilities advertised by a receiver.
    /// The values are intentionally read from the in-memory rendezvous record so
    /// a legacy prepare request can still be upgraded to the receiver's native
    /// mode without a second network round trip.
    /// </summary>
    public TabletReceiver? Find(string clientId)
    {
        lock (_gate)
        {
            Prune();
            return _receivers.GetValueOrDefault(clientId);
        }
    }

    public TabletConnectionRequest Create(string clientId, int orientation, string link)
    {
        lock (_gate)
        {
            Prune();
            if (!_receivers.TryGetValue(clientId, out var receiver))
                throw new InvalidOperationException("设备尚未开启接收，或已经离线。请在平板开启“接收电脑副屏请求”。");
            if (!receiver.Orientations.Contains(orientation)) throw new InvalidOperationException("设备不支持所选方向。");
            if (_requests.Values.Any(x => Active(x.State))) throw new InvalidOperationException("已有副屏请求或连接，请先取消或断开。");
            var request = new TabletConnectionRequest(Guid.NewGuid().ToString("N"), clientId, "waiting", orientation,
                link, Now.AddSeconds(45), "等待平板接受");
            _requests.Add(request.Id, request);
            return request;
        }
    }

    public TabletConnectionRequest Respond(string id, string clientId, string action, string? message = null)
    {
        lock (_gate)
        {
            Prune();
            if (!_requests.TryGetValue(id, out var request) || request.ClientId != clientId)
                throw new InvalidOperationException("副屏请求不存在或不属于此设备。");
            if (!Active(request.State)) return request;
            var next = action switch
            {
                "accept" when request.State == "waiting" => "accepted",
                "reject" when request.State is "waiting" or "accepted" => "rejected",
                "connecting" when request.State is "accepted" or "connecting" => "connecting",
                "connected" when request.State is "connecting" or "connected" => "connected",
                "failed" => "failed",
                _ => throw new InvalidOperationException("请求状态已变化，请刷新后重试。")
            };
            var text = next switch { "accepted" => "平板已接受", "rejected" => "平板已拒绝", "connecting" => "正在连接副屏",
                "connected" => "副屏已连接", _ => string.IsNullOrWhiteSpace(message) ? "副屏连接失败" : message[..Math.Min(160, message.Length)] };
            return _requests[id] = request with { State = next, Message = text,
                ExpiresAt = Now.AddSeconds(next == "connected" ? 40 : 180) };
        }
    }

    public void BindLease(string? id, string? clientId, string lease)
    {
        if (id is null) return;
        lock (_gate)
        {
            Prune();
            if (!_requests.TryGetValue(id, out var request) || request.ClientId != clientId || request.State is not ("accepted" or "connecting"))
                throw new InvalidOperationException("电脑的副屏请求已取消或过期。");
            if (_leases.TryGetValue(id, out var current) && current != lease)
                throw new InvalidOperationException("此副屏请求已绑定另一会话，请取消后重新发起。");
            _leases[id] = lease;
        }
    }

    public TabletConnectionRequest Cancel(string id)
    {
        lock (_gate)
        {
            Prune();
            if (!_requests.TryGetValue(id, out var request)) throw new InvalidOperationException("找不到此副屏请求。");
            return Active(request.State) ? _requests[id] = request with { State = "cancelled", Message = "电脑已取消", ExpiresAt = Now } : request;
        }
    }

    public (string ClientId, string LeaseId)? LeaseFor(string id)
    {
        lock (_gate)
            return _requests.TryGetValue(id, out var request) && _leases.TryGetValue(id, out var lease)
                ? (request.ClientId, lease) : null;
    }

    public void Session(string clientId, string lease, bool ended)
    {
        lock (_gate)
        {
            Prune();
            foreach (var request in _requests.Values.Where(x => x.ClientId == clientId && Active(x.State)).ToArray())
            {
                if (_leases.GetValueOrDefault(request.Id) != lease) continue;
                _requests[request.Id] = ended
                    ? request with { State = "cancelled", Message = "会话已断开", ExpiresAt = Now }
                    : request.State == "connected" ? request with { ExpiresAt = Now.AddSeconds(40) } : request;
            }
        }
    }

    private void Prune()
    {
        var now = Now;
        foreach (var id in _receivers.Where(x => now - x.Value.LastSeen > TimeSpan.FromSeconds(30)).Select(x => x.Key).ToArray()) _receivers.Remove(id);
        foreach (var request in _requests.Values.ToArray())
        {
            if (Active(request.State) && request.ExpiresAt <= now)
                _requests[request.Id] = request with { State = "expired", Message = "请求或连接已超时" };
            else if (!Active(request.State) && now - request.ExpiresAt > TimeSpan.FromMinutes(10))
            { _requests.Remove(request.Id); _leases.Remove(request.Id); }
        }
        // Keep a short bounded history, including rejected requests.
        while (_requests.Count > 64)
        {
            var oldest = _requests.Values.FirstOrDefault(x => !Active(x.State));
            if (oldest is null) break;
            _requests.Remove(oldest.Id); _leases.Remove(oldest.Id);
        }
    }
}
