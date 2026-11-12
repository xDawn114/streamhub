using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace StreamHub.Host.Runtime;

public static class LanDiscoveryPacket
{
    public const string Service = "_streamhub._tcp.local";
    public static bool IsLocalAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 ||
            b[0] == 172 && b[1] is >= 16 and <= 31);
    }
    public static bool IsQuery(ReadOnlySpan<byte> packet, string? deviceId = null) =>
        TryReadQuery(packet, deviceId, out _, out _, out _);

    public static string Classify(ReadOnlySpan<byte> packet, IPAddress source, string? deviceId = null)
    {
        if (!IsLocalAddress(source)) return "outside-lan";
        if (packet.Length is < 12 or > 4096) return "malformed";
        if ((packet[2] & 0x80) != 0) return "response";
        return TryReadQuery(packet, deviceId, out _, out _, out var complete) ? "query" : complete ? "other-service" : "malformed";
    }

    public static bool WantsUnicast(ReadOnlySpan<byte> packet, string? deviceId = null) =>
        TryReadQuery(packet, deviceId, out var unicast, out _, out _) && unicast;

    private static bool TryReadQuery(ReadOnlySpan<byte> packet, string? deviceId, out bool unicast, out int questionsEnd, out bool complete)
    {
        unicast = false; questionsEnd = 12; complete = false;
        if (packet.Length is < 12 or > 4096 || (packet[2] & 0xf8) != 0) return false;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        if (questions is 0 or > 32) return false;
        var offset = 12; var matched = false;
        for (var i = 0; i < questions; i++)
        {
            var labels = new List<string>(); var cursor = offset; var hops = 0; var jumped = false;
            while (true)
            {
                if (++hops > 64 || cursor >= packet.Length) return false;
                var length = packet[cursor++];
                if (length == 0) { if (!jumped) offset = cursor; break; }
                if ((length & 0xc0) == 0xc0)
                {
                    if (cursor >= packet.Length) return false;
                    var target = ((length & 0x3f) << 8) | packet[cursor++];
                    if (target >= cursor - 2) return false;
                    if (!jumped) offset = cursor;
                    jumped = true; cursor = target; continue;
                }
                if (length > 63 || cursor + length > packet.Length) return false;
                labels.Add(Encoding.UTF8.GetString(packet.Slice(cursor, length))); cursor += length;
            }
            if (offset + 4 > packet.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var questionClass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]); offset += 4;
            if ((questionClass & 0x7fff) != 1) continue;
            var name = string.Join('.', labels);
            var relevant = string.Equals(name, Service, StringComparison.OrdinalIgnoreCase) && type is 12 or 255;
            if (Guid.TryParse(deviceId, out var guid))
            {
                var key = guid.ToString("N")[..16];
                if (string.Equals(name, "StreamHub-" + key + "." + Service, StringComparison.OrdinalIgnoreCase) && type is 33 or 16 or 255 ||
                    string.Equals(name, "streamhub-" + key + ".local", StringComparison.OrdinalIgnoreCase) && type is 1 or 255) relevant = true;
            }
            if (relevant) { matched = true; unicast |= (questionClass & 0x8000) != 0; }
        }
        questionsEnd = offset;
        complete = true;
        return matched;
    }
    public static byte[] Response(string id, string name, string pin, IPAddress address, byte[]? legacyQuery = null)
    {
        if (!IsLocalAddress(address) || !Guid.TryParse(id, out var guid) || pin.Length != 64 || !pin.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid discovery identity");
        var questionsEnd = 12;
        if (legacyQuery is not null && !TryReadQuery(legacyQuery, id, out _, out questionsEnd, out _)) throw new ArgumentException("Invalid legacy query");
        var key = guid.ToString("N")[..16]; var instance = "StreamHub-" + key + "." + Service; var host = "streamhub-" + key + ".local";
        using var stream = new MemoryStream();
        void U16(int n) { stream.WriteByte((byte)(n >> 8)); stream.WriteByte((byte)n); }
        void U32(uint n) { U16((int)(n >> 16)); U16((int)n); }
        static byte[] DnsName(string value)
        {
            using var s = new MemoryStream();
            foreach (var label in value.Split('.')) { var bytes = Encoding.UTF8.GetBytes(label); if (bytes.Length is < 1 or > 63) throw new ArgumentException(); s.WriteByte((byte)bytes.Length); s.Write(bytes); }
            s.WriteByte(0); return s.ToArray();
        }
        void Record(string owner, int type, byte[] data, bool flush = true)
        { stream.Write(DnsName(owner)); U16(type); U16(flush && legacyQuery is null ? 0x8001 : 1); U32(legacyQuery is null ? 120u : 10u); U16(data.Length); stream.Write(data); }
        U16(legacyQuery is null ? 0 : BinaryPrimitives.ReadUInt16BigEndian(legacyQuery)); U16(0x8400);
        U16(legacyQuery is null ? 0 : BinaryPrimitives.ReadUInt16BigEndian(legacyQuery.AsSpan(4))); U16(4); U16(0); U16(0);
        if (legacyQuery is not null) stream.Write(legacyQuery.AsSpan(12, questionsEnd - 12));
        Record(Service, 12, DnsName(instance), false);
        Record(instance, 33, new byte[] { 0, 0, 0, 0, (byte)(ControlApiServer.Port >> 8), (byte)(ControlApiServer.Port & 255) }.Concat(DnsName(host)).ToArray());
        using var txt = new MemoryStream();
        foreach (var item in new[] { "v=1", "id=" + id, "name=" + name, "pin=" + pin })
        { var bytes = Encoding.UTF8.GetBytes(item); if (bytes.Length > 255) throw new ArgumentException(); txt.WriteByte((byte)bytes.Length); txt.Write(bytes); }
        Record(instance, 16, txt.ToArray()); Record(host, 1, address.GetAddressBytes());
        return stream.ToArray();
    }
}

public sealed record LanDiscoveryStatus(bool Running, int Interfaces, int Joined, long Queries, long UnicastReplies,
    long MulticastReplies, long Errors, string? LastError, long Received = 0, long OutsideLan = 0,
    long Responses = 0, long OtherService = 0, long Malformed = 0, long Throttled = 0, long NoRoute = 0);

/// <summary>Counts the actual receive path before filtering, without logging addresses or packet bodies.</summary>
public sealed class LanDiscoveryTraffic
{
    private long _received, _outsideLan, _responses, _otherService, _malformed;
    public long Received => Interlocked.Read(ref _received);
    public long OutsideLan => Interlocked.Read(ref _outsideLan);
    public long Responses => Interlocked.Read(ref _responses);
    public long OtherService => Interlocked.Read(ref _otherService);
    public long Malformed => Interlocked.Read(ref _malformed);
    public bool Accept(ReadOnlySpan<byte> packet, IPAddress source, string id)
    {
        Interlocked.Increment(ref _received);
        switch (LanDiscoveryPacket.Classify(packet, source, id))
        {
            case "query": return true;
            case "outside-lan": Interlocked.Increment(ref _outsideLan); break;
            case "response": Interlocked.Increment(ref _responses); break;
            case "other-service": Interlocked.Increment(ref _otherService); break;
            default: Interlocked.Increment(ref _malformed); break;
        }
        return false;
    }
}

public sealed class LanDiscovery(string id, string name, string pin) : IAsyncDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _socket;
    private Task? _work;
    private int _disposed;
    private int _interfaces, _joined;
    private long _queries, _unicastReplies, _multicastReplies, _errors;
    private long _throttled, _noRoute;
    private readonly LanDiscoveryTraffic _traffic = new();
    private string? _lastError;
    public bool Running => _socket is not null && _work is { IsCompleted: false };
    public LanDiscoveryStatus Status => new(Running, Volatile.Read(ref _interfaces), Volatile.Read(ref _joined),
        Interlocked.Read(ref _queries), Interlocked.Read(ref _unicastReplies), Interlocked.Read(ref _multicastReplies),
        Interlocked.Read(ref _errors), Volatile.Read(ref _lastError), _traffic.Received, _traffic.OutsideLan,
        _traffic.Responses, _traffic.OtherService, _traffic.Malformed, Interlocked.Read(ref _throttled), Interlocked.Read(ref _noRoute));
    private void RecordError(string operation, SocketException error)
    { Interlocked.Increment(ref _errors); Volatile.Write(ref _lastError, operation + ": " + error.SocketErrorCode); }
    public void Start()
    {
        if (_work is not null || Volatile.Read(ref _disposed)!=0) return;
        try
        {
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.ExclusiveAddressUse = false; _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            _work = RunAsync(_stop.Token);
        }
        catch (SocketException error) { RecordError("bind", error); _socket?.Dispose(); _socket = null; }
    }
    private static IPAddress[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            !new[] { "tailscale", "wintun", "vpn", "hyper-v" }.Any(x => (n.Name + " " + n.Description).Contains(x, StringComparison.OrdinalIgnoreCase)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(LanDiscoveryPacket.IsLocalAddress).Distinct().Take(8).ToArray();
    private async Task RunAsync(CancellationToken token)
    {
        var joined = new HashSet<IPAddress>(); var nextAdvert = DateTimeOffset.MinValue;
        var replies = new Dictionary<IPEndPoint, DateTimeOffset>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                IPAddress[] addresses;
                try { addresses = Addresses(); }
                catch (NetworkInformationException) { await Task.Delay(2000, token); continue; }
                Volatile.Write(ref _interfaces, addresses.Length);
                foreach (var address in joined.Where(a => !addresses.Contains(a)).ToArray())
                {
                    try { _socket!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(Group, address)); }
                    catch (SocketException error) { RecordError("leave", error); }
                    joined.Remove(address);
                }
                foreach (var address in addresses.Where(a => !joined.Contains(a)))
                    try { _socket!.JoinMulticastGroup(Group, address); joined.Add(address); } catch (SocketException error) { RecordError("join", error); }
                Volatile.Write(ref _joined, joined.Count);
                async Task Advertise()
                {
                    foreach (var address in addresses)
                        try
                        {
                            _socket!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                            await _socket.SendAsync(LanDiscoveryPacket.Response(id, name, pin, address), new IPEndPoint(Group, 5353), token);
                            Interlocked.Increment(ref _multicastReplies);
                        }
                        catch (SocketException error) { RecordError("multicast-send", error); }
                }
                if (DateTimeOffset.UtcNow >= nextAdvert) { await Advertise(); nextAdvert = DateTimeOffset.UtcNow.AddSeconds(15); }
                using var receive = CancellationTokenSource.CreateLinkedTokenSource(token); receive.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var packet = await _socket!.ReceiveAsync(receive.Token);
                    if (!_traffic.Accept(packet.Buffer, packet.RemoteEndPoint.Address, id)) continue;
                    Interlocked.Increment(ref _queries);
                    var now = DateTimeOffset.UtcNow;
                    foreach (var key in replies.Where(x => now - x.Value > TimeSpan.FromSeconds(2)).Select(x => x.Key).ToArray()) replies.Remove(key);
                    if (replies.TryGetValue(packet.RemoteEndPoint, out var last) && now - last < TimeSpan.FromMilliseconds(750) || replies.Count >= 64)
                    { Interlocked.Increment(ref _throttled); continue; }
                    replies[packet.RemoteEndPoint] = now;
                    if (packet.RemoteEndPoint.Port != 5353 || LanDiscoveryPacket.WantsUnicast(packet.Buffer, id))
                    {
                        try
                        {
                            // Ask routing for the source of this unicast reply. Advertising
                            // another adapter's A record would fail the receiver's source check.
                            using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                            route.Connect(packet.RemoteEndPoint);
                            var source = ((IPEndPoint)route.LocalEndPoint!).Address;
                            if (!addresses.Contains(source)) { Interlocked.Increment(ref _noRoute); continue; }
                            var response = LanDiscoveryPacket.Response(id, name, pin, source,
                                packet.RemoteEndPoint.Port == 5353 ? null : packet.Buffer);
                            await _socket.SendAsync(response, packet.RemoteEndPoint, token);
                            Interlocked.Increment(ref _unicastReplies);
                        }
                        catch (SocketException error) { RecordError("unicast-send", error); }
                    }
                    else await Advertise();
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (SocketException error) { RecordError("receive", error); await Task.Delay(250, token); }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or NetworkInformationException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _stop.Cancel(); _socket?.Dispose(); if (_work is not null) await _work; _socket = null; _stop.Dispose();
    }
}
