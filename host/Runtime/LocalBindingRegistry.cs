using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StreamHub.Host.Runtime;

public sealed record LocalBindingRequest(string Id, string ClientId, string Name, string Comparison, string State, DateTimeOffset ExpiresAt);
public sealed record LocalBindingSnapshot(string? Code, DateTimeOffset? ExpiresAt, LocalBindingRequest? Request);
public sealed record LocalBindingResult(string RequestId, string State, string Comparison, string? Invitation = null);

/// <summary>A short, owner-opened enrollment window. Discovery never grants trust.</summary>
public sealed class LocalBindingRegistry(string certificatePin, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string? _code, _nonce;
    private DateTimeOffset _expires;
    private int _failures;
    private LocalBindingRequest? _request;

    public LocalBindingSnapshot Start()
    {
        lock (_gate)
        {
            _code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
            _expires = _clock.GetUtcNow().AddMinutes(3);
            _failures = 0; _request = null; _nonce = null;
            return SnapshotCore();
        }
    }

    public LocalBindingSnapshot Snapshot() { lock (_gate) return SnapshotCore(); }
    private LocalBindingSnapshot SnapshotCore()
    {
        Expire();
        return new(_code, _code is null ? null : _expires, _request);
    }
    private void Expire()
    {
        var now = _clock.GetUtcNow();
        if (now >= _expires) _code = null;
        if (_request is { State: "pending" or "approved" } request && now >= request.ExpiresAt)
            _request = request with { State = "expired" };
    }

    public LocalBindingResult Begin(string code, string clientId, string name, string nonce)
    {
        if (clientId is not { Length: >= 8 and <= 128 } || clientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            name is not { Length: >= 1 and <= 64 } || name.Any(char.IsControl) ||
            nonce is not { Length: 64 } || nonce.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("设备信息无效，请重新查找电脑。");
        lock (_gate)
        {
            Expire();
            if (_code is null) throw new InvalidOperationException("短码已失效，请在电脑端重新生成。");
            var valid = code is { Length: 6 } && code.All(char.IsAsciiDigit) &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(_code));
            if (!valid)
            {
                if (++_failures >= 5) _code = null;
                throw new InvalidOperationException("短码不正确或已失效，请核对电脑显示。");
            }
            if (_request is { State: "pending" or "approved" })
                throw new InvalidOperationException("电脑正在确认另一台设备，请先处理或重新生成短码。");
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var proof = SHA256.HashData(Encoding.UTF8.GetBytes(certificatePin + ":" + nonce + ":" + id));
            var comparison = (BinaryPrimitives.ReadUInt32BigEndian(proof) % 1_000_000).ToString("D6");
            _nonce = nonce;
            _request = new(id, clientId, name, comparison, "pending", _clock.GetUtcNow().AddSeconds(90));
            _code = null; // A correct redemption consumes the window before owner approval.
            return new(id, "pending", comparison);
        }
    }

    public LocalBindingSnapshot Decide(string id, bool allow)
    {
        lock (_gate)
        {
            Expire();
            if (_request is not { State: "pending" } request || request.Id != id)
                throw new InvalidOperationException("设备确认已结束或过期，请重新生成短码。");
            _request = request with { State = allow ? "approved" : "rejected" };
            return SnapshotCore();
        }
    }

    public LocalBindingResult Poll(string id, string nonce, Func<string> invitation)
    {
        lock (_gate)
        {
            Expire();
            if (_request is not { } request || request.Id != id || _nonce is null || nonce is not { Length: 64 } ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(nonce), Encoding.ASCII.GetBytes(_nonce)))
                throw new InvalidOperationException("设备确认无效，请重新绑定。");
            if (request.State != "approved") return new(request.Id, request.State, request.Comparison);
            var credential = invitation();
            _request = request with { State = "delivered" };
            _nonce = null;
            return new(request.Id, "approved", request.Comparison, credential);
        }
    }

    public LocalBindingSnapshot Cancel()
    {
        lock (_gate) { _code = null; _nonce = null; _request = null; return SnapshotCore(); }
    }
}
