using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

[Flags] public enum ControlScope { None = 0, Remote = 1, Tablet = 2, Files = 4, Clipboard = 8, All = 15 }
public sealed record ControlDevice(string Id, string ClientId, string Name, string TokenHash,
    ControlScope Scope, bool Revoked, DateTimeOffset CreatedAt,
    string? RemoteCertificate = null, string? TabletCertificate = null,
    string? Alias = null, string DeviceType = "android", DateTimeOffset? LastSeen = null,
    bool HistoryDeleted = false);
public sealed record ControlDeviceList(bool LegacyAllowed, ControlDeviceSummary[] Devices);
public sealed record ControlDeviceSummary(string Id, string ClientId, string Name, ControlScope Scope,
    bool Revoked, DateTimeOffset CreatedAt, string? Alias = null, string DeviceType = "android", DateTimeOffset? LastSeen = null);
public sealed record ControlPrincipal(string Id, string? ClientId, string Name, ControlScope Scope, bool Legacy);
public sealed record ControlDeviceGrant(ControlPrincipal Principal, byte[] Token);
public sealed record ControlMediaBaseline(string Id, string CertificateHash, bool Enabled);
public sealed record ControlMediaPairing(string Profile, string CredentialId, string ExpectedCertificate, ControlMediaBaseline[] Before);

/// <summary>Only hashes are retained. Mutations are exposed through the owner-authenticated local pipe.</summary>
public sealed class ControlDeviceRegistry
{
    private sealed record State(int Version, bool LegacyAllowed, ControlDevice[] Devices, ControlMediaPairing[]? Pairings = null);
    private readonly object _gate = new();
    private readonly Action<byte[]>? _save;
    private State _state;
    private readonly Dictionary<string, DateTimeOffset> _lastPersistedSeen = new();
    public long Revision { get; private set; }
    public ControlDeviceRegistry(byte[]? saved = null, Action<byte[]>? save = null)
    {
        _save = save;
        _state = saved is null ? new(1, true, []) : Decode(saved);
    }
    private static State Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > 1048576) throw new InvalidDataException("设备授权记录大小无效。");
        var state = JsonSerializer.Deserialize<State>(bytes, RuntimeProtocol.JsonOptions)
            ?? throw new InvalidDataException("设备授权记录无效。");
        if (state.Version != 1 || state.Devices is null || state.Devices.Length > 64 || state.Devices.Any(x => x is null) ||
            state.Devices.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != state.Devices.Length)
            throw new InvalidDataException("设备授权记录格式无效。");
        foreach (var d in state.Devices)
        {
            ValidateClient(d.ClientId, d.Name); ValidateScope(d.Scope);
            if (d.Alias is { } alias && (alias.Length is < 1 or > 40 || alias.Any(char.IsControl)) ||
                d.DeviceType is not ("android" or "phone" or "tablet") || d.HistoryDeleted && !d.Revoked)
                throw new InvalidDataException("设备备注或类型无效。");
            if (!ValidId(d.Id) || d.TokenHash is not { Length: 64 } || !d.TokenHash.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("设备授权记录格式无效。");
            if (d.RemoteCertificate is not null) _ = MediaIdentityPolicy.ParseCertificate("remote", d.RemoteCertificate);
            if (d.TabletCertificate is not null) _ = MediaIdentityPolicy.ParseCertificate("tablet", d.TabletCertificate);
        }
        if (state.Pairings is { } pairings)
        {
            if (pairings.Length > 2 || pairings.Any(p => p is null) ||
                pairings.Select(p => p.Profile).Distinct().Count() != pairings.Length)
                throw new InvalidDataException("媒体配对恢复记录无效。");
            foreach (var pairing in pairings)
            {
                _ = MediaIdentityPolicy.ParseCertificate(pairing.Profile, pairing.ExpectedCertificate);
                if (!state.Devices.Any(d => d.Id == pairing.CredentialId)) throw new InvalidDataException("媒体配对恢复记录无效。");
                ValidateBaseline(pairing.Before);
            }
        }
        return state;
    }
    public ControlDeviceList Snapshot()
    {
        lock (_gate) return new(_state.LegacyAllowed, _state.Devices.Where(d => !d.HistoryDeleted).Select(d =>
            new ControlDeviceSummary(d.Id, d.ClientId, d.Name, d.Scope, d.Revoked, d.CreatedAt, d.Alias, d.DeviceType, d.LastSeen)).ToArray());
    }
    public bool? FilePermission(string clientId)
    {
        lock (_gate)
        {
            var devices = _state.Devices.Where(d => d.ClientId == clientId).ToArray();
            return devices.Length == 0 ? null : devices.Any(d => !d.Revoked && (d.Scope & ControlScope.Files) != 0);
        }
    }
    public void Rename(string id, string alias)
    {
        alias = alias.Trim();
        if (alias.Length > 40 || alias.Any(char.IsControl)) throw new ArgumentException("设备备注最多40个字符，不能包含控制字符。");
        Change(id, d => d with { Alias = alias.Length == 0 ? null : alias });
    }
    // Metadata comes only from successful authentication. It does not change grants
    // or their revision, and heartbeat writes are bounded to once per five minutes.
    public void Seen(ControlPrincipal principal, string? deviceType = null)
    {
        if (principal.Legacy) return;
        if (deviceType is not (null or "android" or "phone" or "tablet")) throw new ArgumentException("设备类型无效。");
        lock (_gate)
        {
            var device = _state.Devices.FirstOrDefault(d => d.Id == principal.Id && !d.Revoked);
            if (device is null) return;
            var now = DateTimeOffset.UtcNow;
            var next = _state with { Devices = _state.Devices.Select(d => d.Id == device.Id
                ? d with { LastSeen = now, DeviceType = deviceType ?? d.DeviceType } : d).ToArray() };
            if (!_lastPersistedSeen.TryGetValue(device.Id, out var previous) || now - previous >= TimeSpan.FromMinutes(5) ||
                deviceType is not null && deviceType != device.DeviceType)
            {
                try { _save?.Invoke(JsonSerializer.SerializeToUtf8Bytes(next, RuntimeProtocol.JsonOptions)); _lastPersistedSeen[device.Id] = now; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { /* Telemetry cannot interrupt a valid session. */ }
            }
            _state = next;
        }
    }
    public ControlDeviceGrant Issue(string clientId, string name, ControlScope scope = ControlScope.All)
    {
        ValidateClient(clientId, name); ValidateScope(scope);
        lock (_gate)
        {
            if (_state.Devices.Count(d => !d.Revoked && d.ClientId != clientId) >= 64)
                throw new InvalidOperationException("授权设备已达上限，请先撤销旧设备。");
            byte[] token = RandomNumberGenerator.GetBytes(32);
            var device = new ControlDevice(Guid.NewGuid().ToString("N"), clientId, name,
                Convert.ToHexString(SHA256.HashData(token)), scope, false, DateTimeOffset.UtcNow);
            // Re-enrolling the same verified ClientId rotates its old credential. Revoked
            // records remain visible until capacity needs reclaiming; secrets never do.
            var devices = _state.Devices.Select(d => d.ClientId == clientId ? d with { Revoked = true } : d)
                .Append(device).ToList();
            while (devices.Count > 64)
            {
                var disposable = devices.FirstOrDefault(d => d.Revoked && d.RemoteCertificate is null && d.TabletCertificate is null);
                if (disposable is null)
                { CryptographicOperations.ZeroMemory(token); throw new InvalidOperationException("设备授权历史已达上限；已撤销的媒体身份仍需保留，未新增授权。"); }
                devices.Remove(disposable);
            }
            try { Commit(_state with { Devices = devices.ToArray() }); }
            catch { CryptographicOperations.ZeroMemory(token); throw; }
            return new(new(device.Id, clientId, name, scope, false), token);
        }
    }
    public void Revoke(string id) => Change(id, d => d with { Revoked = true });
    public void DeleteHistory(string id)
    {
        lock (_gate)
        {
            var device = _state.Devices.FirstOrDefault(d => d.Id == id)
                ?? throw new InvalidOperationException("历史设备不存在，请刷新。");
            if (!device.Revoked) throw new InvalidOperationException("请先撤销设备，再删除历史记录。");
            if (device.HistoryDeleted) return;
            // Retain the revoked credential, client ID, and media fence. Removing
            // them would make a legacy file/media identity appear unknown again.
            // Presentation-only deletion must not interrupt unrelated sessions.
            Commit(_state with { Devices = _state.Devices.Select(d => d.Id == id
                ? d with { HistoryDeleted = true, Name = "已删除设备", Alias = null, LastSeen = null, DeviceType = "android" }
                : d).ToArray() }, permissionsChanged: false);
        }
    }
    public void SetScope(string id, ControlScope scope)
    { ValidateScope(scope); Change(id, d => d with { Scope = scope }); }
    public void SetLegacyAllowed(bool allowed) { lock (_gate) Commit(_state with { LegacyAllowed = allowed }); }
    public ControlPrincipal Resolve(string id)
    {
        lock (_gate)
        {
            if (id == "legacy" && _state.LegacyAllowed)
                return new("legacy", null, "旧版共享绑定", ControlScope.All, true);
            var d = _state.Devices.FirstOrDefault(d => d.Id == id && !d.Revoked);
            return d is null ? throw new UnauthorizedAccessException("设备授权已撤销，请重新绑定。")
                : new(d.Id, d.ClientId, d.Alias ?? d.Name, d.Scope, false);
        }
    }
    public void EnsureCurrent(ControlPrincipal principal, string? clientId, ControlScope scope)
    {
        var current = Resolve(principal.Id);
        EnsureClient(current, clientId); EnsureScope(current, scope);
    }
    public void BindMedia(string credentialId, string profile, string certificate)
    {
        _ = MediaIdentityPolicy.ParseCertificate(profile, certificate);
        lock (_gate)
        {
            var principal = Resolve(credentialId);
            EnsureScope(principal, profile == "remote" ? ControlScope.Remote : ControlScope.Tablet);
            if (principal.Legacy) return;
            string? Get(ControlDevice d) => profile == "remote" ? d.RemoteCertificate : d.TabletCertificate;
            if (_state.Devices.Any(d => d.ClientId != principal.ClientId && Get(d) == certificate))
                throw new UnauthorizedAccessException("媒体身份已属于另一台设备，请在电脑端重新核对绑定。");
            var existing = _state.Devices.Single(d => d.Id == credentialId);
            if (Get(existing) == certificate) return;
            if (Get(existing) is not null) throw new UnauthorizedAccessException("媒体身份已变化，请重新进行本机确认绑定。");
            Change(credentialId, d => profile == "remote" ? d with { RemoteCertificate = certificate }
                : d with { TabletCertificate = certificate });
        }
    }
    public void EnsureMediaBound(string credentialId, string profile)
    {
        lock (_gate)
        {
            var principal = Resolve(credentialId);
            if (principal.Legacy) return;
            var device = _state.Devices.Single(d => d.Id == credentialId);
            if ((profile == "remote" ? device.RemoteCertificate : device.TabletCertificate) is null)
                throw new UnauthorizedAccessException("请先核验此设备的媒体身份再配对。");
        }
    }
    // A durable fence precedes /api/pin. A crash or lost response leaves recovery
    // work that must finish with the selected engine stopped before it can restart.
    public void BeginMediaPairing(string credentialId, string profile, ControlMediaBaseline[] before)
    {
        ValidateBaseline(before);
        lock (_gate)
        {
            var principal = Resolve(credentialId);
            EnsureScope(principal, profile == "remote" ? ControlScope.Remote : ControlScope.Tablet);
            EnsureMediaBound(credentialId, profile);
            if (principal.Legacy) throw new InvalidOperationException("旧版共享绑定不使用独立媒体配对。");
            var device = _state.Devices.Single(d => d.Id == credentialId);
            string expected = (profile == "remote" ? device.RemoteCertificate : device.TabletCertificate)!;
            _ = MediaIdentityPolicy.ParseCertificate(profile, expected);
            var pending = _state.Pairings ?? [];
            if (pending.Any(p => p.Profile == profile)) throw new InvalidOperationException("上次媒体配对尚待安全回收，请稍后重试。");
            Commit(_state with { Pairings = pending.Append(new(profile, credentialId, expected, before.ToArray())).ToArray() });
        }
    }
    public ControlMediaPairing? PendingMediaPairing(string profile)
    {
        lock (_gate)
        {
            var pending = _state.Pairings?.SingleOrDefault(p => p.Profile == profile);
            return pending is null ? null : pending with { Before = pending.Before.ToArray() };
        }
    }
    public void CompleteMediaPairing(string profile, ControlMediaBaseline[] after)
    {
        ValidateBaseline(after);
        lock (_gate)
        {
            var pending = PendingMediaPairing(profile) ?? throw new InvalidOperationException("媒体配对恢复记录缺失。");
            EnsureScope(Resolve(pending.CredentialId), profile == "remote" ? ControlScope.Remote : ControlScope.Tablet);
            string expectedHash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(pending.ExpectedCertificate)));
            if (!after.Any(c => c.Enabled && c.CertificateHash == expectedHash) ||
                pending.Before.Any(c => !after.Contains(c)) ||
                after.Any(c => !pending.Before.Contains(c) && (!c.Enabled || c.CertificateHash != expectedHash)))
                throw new UnauthorizedAccessException("实际媒体配对与此设备核验的证书不一致，已停止连接并回收本次新增授权。");
            ClearMediaPairing(profile);
        }
    }
    public void ClearMediaPairing(string profile)
    { lock (_gate) Commit(_state with { Pairings = (_state.Pairings ?? []).Where(p => p.Profile != profile).ToArray() }); }
    private static void ValidateBaseline(ControlMediaBaseline[] clients)
    {
        if (clients is null || clients.Length > 1024 || clients.Any(c => c is null ||
            !Guid.TryParseExact(c.Id, "D", out var id) || id == Guid.Empty ||
            c.CertificateHash is not { Length: 64 } || !c.CertificateHash.All(char.IsAsciiHexDigit)) ||
            clients.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != clients.Length)
            throw new InvalidDataException("媒体配对恢复记录无效。");
    }
    public string[] BlockedMedia(string profile)
    {
        var scope = profile == "remote" ? ControlScope.Remote : ControlScope.Tablet;
        lock (_gate)
        {
            string? Get(ControlDevice d) => profile == "remote" ? d.RemoteCertificate : d.TabletCertificate;
            var allowed = _state.Devices.Where(d => !d.Revoked && (d.Scope & scope) != 0).Select(Get).ToHashSet();
            return _state.Devices.Where(d => d.Revoked || (d.Scope & scope) == 0).Select(Get)
                .Where(c => c is not null && !allowed.Contains(c)).Cast<string>().Distinct().ToArray();
        }
    }
    private void Change(string id, Func<ControlDevice, ControlDevice> change)
    {
        lock (_gate)
        {
            if (!_state.Devices.Any(d => d.Id == id)) throw new InvalidOperationException("设备授权不存在，请刷新。");
            Commit(_state with { Devices = _state.Devices.Select(d => d.Id == id ? change(d) : d).ToArray() });
        }
    }
    private void Commit(State next, bool permissionsChanged = true)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, RuntimeProtocol.JsonOptions);
        if (bytes.Length > 1048576) throw new InvalidDataException("设备授权记录已达大小上限，未更改授权。");
        _save?.Invoke(bytes); // Failure preserves the prior in-memory permissions.
        _state = next;
        if (permissionsChanged) Revision++;
    }
    public ControlPrincipal? Authenticate(string authorization, byte[] legacyToken)
    {
        if (authorization.Length != 71 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        byte[] token;
        try { token = Convert.FromHexString(authorization[7..]); } catch (FormatException) { return null; }
        try
        {
            byte[] hash = SHA256.HashData(token);
            lock (_gate)
            {
                ControlPrincipal? found = null;
                foreach (var device in _state.Devices)
                    if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(device.TokenHash)) && !device.Revoked)
                        found = new(device.Id, device.ClientId, device.Alias ?? device.Name, device.Scope, false);
                if (found is not null) return found;
                return _state.LegacyAllowed && CryptographicOperations.FixedTimeEquals(token, legacyToken)
                    ? new("legacy", null, "旧版共享绑定", ControlScope.All, true) : null;
            }
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }
    public static void EnsureClient(ControlPrincipal principal, string? clientId)
    {
        if (!principal.Legacy && !string.Equals(principal.ClientId, clientId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("设备身份与授权不一致。");
    }
    public static void EnsureScope(ControlPrincipal principal, ControlScope scope)
    { if ((principal.Scope & scope) != scope) throw new UnauthorizedAccessException("电脑未向此设备开放该功能。"); }
    private static bool ValidId(string value) => value is { Length: >= 8 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static void ValidateClient(string id, string name)
    { if (!ValidId(id) || name is not { Length: >= 1 and <= 64 } || name.Any(char.IsControl)) throw new ArgumentException("授权设备信息无效。"); }
    private static void ValidateScope(ControlScope scope)
    { if ((scope & ~ControlScope.All) != 0) throw new ArgumentException("设备权限范围无效。"); }
}
