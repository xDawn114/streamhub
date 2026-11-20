using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

public sealed record AssistancePeer(string Id, string Name, string ConnectionInfo, string Mode);

/// <summary>Per-user assistance identities; the UI sees only confirmed, secret-free peers.</summary>
public sealed class AssistancePeerStore
{
    private sealed record Entry(string Id, string Name, string ConnectionInfo, string Mode, string Credential, bool Trusted);
    private sealed record Data(int Version, string ClientId, Entry[] Peers);
    private sealed record Envelope(int Version, string ProtectedData);
    internal sealed class Binding(string clientId, AssistancePeer peer, string credential)
    {
        internal string ClientId { get; } = clientId;
        internal AssistancePeer Peer { get; } = peer;
        internal string Credential { get; } = credential;
    }

    private const int MaximumBytes = 524288;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StreamHub.Assistance.Peers.v1");
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _gate;

    public AssistancePeerStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "StreamHub", "assistance-peers.json"));
        _gate = Gates.GetOrAdd(_path, _ => new object());
    }

    public IReadOnlyList<AssistancePeer> List()
    {
        lock (_gate)
            return Read()?.Peers.Where(p => p.Trusted).Select(PublicPeer).ToArray() ?? [];
    }

    public void Forget(AssistancePeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        Change(data => data with { Peers = data.Peers.Where(p => p.Id != peer.Id).ToArray() }, create: false);
    }

    /// <summary>Refresh addresses for an existing peer without changing its pinned identity.</summary>
    public AssistancePeer Bind(string peerId, string connectionInfo)
    {
        var connection = AssistanceClient.ParseConnection(connectionInfo.Trim());
        string id = connection.Pin.ToUpperInvariant();
        if (id != peerId) throw new ArgumentException("The connection belongs to a different pinned computer.");
        AssistancePeer? result = null;
        Change(data => {
            var entry = data.Peers.SingleOrDefault(p => p.Id == peerId && p.Trusted)
                ?? throw new InvalidOperationException("The saved assistance computer was not found.");
            entry = entry with { ConnectionInfo = Encode(connection) };
            result = PublicPeer(entry);
            return Replace(data, entry);
        }, create: false);
        return result ?? throw new InvalidOperationException("The saved assistance computer was not found.");
    }

    public AssistancePeer Bind(AssistancePeer peer, string connectionInfo) => Bind(peer.Id, connectionInfo);

    internal Binding Prepare(AssistanceConnection connection)
    {
        Binding? binding = null;
        Change(data => {
            string id = connection.Pin.ToUpperInvariant();
            var entry = data.Peers.SingleOrDefault(p => p.Id == id);
            if (entry is null)
            {
                if (data.Peers.Length >= 128) throw new InvalidOperationException("The local assistance identity list is full.");
                entry = new(id, connection.Addresses[0], Encode(connection), "view",
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), false);
            }
            else entry = entry with { ConnectionInfo = Encode(connection) };
            binding = new(data.ClientId, PublicPeer(entry), entry.Credential);
            return Replace(data, entry);
        });
        return binding!;
    }

    internal Binding Resolve(string peerId)
    {
        lock (_gate)
        {
            var data = Read();
            var entry = data?.Peers.SingleOrDefault(p => p.Id == peerId && p.Trusted)
                ?? throw new InvalidOperationException("The saved assistance computer was not found.");
            return new(data!.ClientId, PublicPeer(entry), entry.Credential);
        }
    }

    internal void Confirm(Binding binding, string mode, string? name)
    {
        if (!ValidMode(mode)) throw new InvalidDataException("Invalid assistance trust permissions.");
        if (name is not null && (name.Length is < 1 or > 128 || name.Any(char.IsControl)))
            throw new InvalidDataException("Invalid assistance computer name.");
        Change(data => {
            var entry = data.Peers.SingleOrDefault(p => p.Id == binding.Peer.Id);
            // A delayed reply cannot recreate a forgotten or replaced credential.
            if (data.ClientId != binding.ClientId || entry?.Credential != binding.Credential) return data;
            if (entry.Trusted && entry.Mode == mode && (name is null || entry.Name == name)) return data;
            return Replace(data, entry with { Trusted = true, Mode = mode, Name = name ?? entry.Name });
        }, create: false);
    }

    internal void DiscardPending(Binding binding) => Change(data => {
        var peers = data.Peers.Where(p => p.Id != binding.Peer.Id || p.Trusted || p.Credential != binding.Credential).ToArray();
        return peers.Length == data.Peers.Length ? data : data with { Peers = peers };
    }, create: false);

    internal static bool ValidMode(string? mode) => mode is "view" or "control" or "view,control";
    private static bool ValidSecret(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static AssistancePeer PublicPeer(Entry entry) => new(entry.Id, entry.Name, entry.ConnectionInfo, entry.Mode);
    private static Data Replace(Data data, Entry entry) => data with {
        Peers = data.Peers.Where(p => p.Id != entry.Id).Append(entry).ToArray()
    };
    private static string Encode(AssistanceConnection connection) => "icontrol-help:" + Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(connection with { Pin = connection.Pin.ToUpperInvariant(), Code = null }, RuntimeProtocol.JsonOptions));

    private void Change(Func<Data, Data> change, bool create = true)
    {
        lock (_gate)
        {
            if (!create && !File.Exists(_path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var data = Read() ?? new(1, Guid.NewGuid().ToString("N"), []);
            var updated = change(data);
            if (!ReferenceEquals(data, updated)) Write(updated);
        }
    }

    private Data? Read()
    {
        if (!File.Exists(_path)) return null;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is 0 or > MaximumBytes) throw new InvalidDataException("Invalid assistance peer store size.");
        var envelope = JsonSerializer.Deserialize<Envelope>(stream, RuntimeProtocol.JsonOptions)
            ?? throw new InvalidDataException("Invalid assistance peer store.");
        if (envelope.Version != 1 || envelope.ProtectedData is not { Length: > 0 and <= MaximumBytes })
            throw new InvalidDataException("Invalid assistance peer store.");
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(envelope.ProtectedData); }
        catch (FormatException) { throw new InvalidDataException("Invalid assistance peer store."); }
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var data = JsonSerializer.Deserialize<Data>(plain, RuntimeProtocol.JsonOptions)
                ?? throw new InvalidDataException("Invalid assistance peer store.");
            if (data.Version != 1 || !Guid.TryParseExact(data.ClientId, "N", out var clientId) || clientId == Guid.Empty ||
                data.Peers is null || data.Peers.Length > 128 ||
                data.Peers.Any(p => p is null || !ValidSecret(p.Id) || !ValidSecret(p.Credential) ||
                    !ValidMode(p.Mode) || p.Name is not { Length: > 0 and <= 128 } || p.Name.Any(char.IsControl)) ||
                data.Peers.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Peers.Length)
                throw new InvalidDataException("Invalid assistance peer store.");
            foreach (var peer in data.Peers)
            {
                var connection = AssistanceClient.ParseConnection(peer.ConnectionInfo);
                if (peer.Id != connection.Pin.ToUpperInvariant() || connection.Code is not null)
                    throw new InvalidDataException("Assistance peer does not match its pinned connection.");
            }
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void Write(Data data)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data, RuntimeProtocol.JsonOptions);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            byte[] file = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToBase64String(encrypted)), RuntimeProtocol.JsonOptions);
            if (file.Length > MaximumBytes) throw new InvalidDataException("The assistance peer store is full.");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough)) { stream.Write(file); stream.Flush(true); }
            File.Move(temporary, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
