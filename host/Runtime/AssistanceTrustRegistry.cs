using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

/// <summary>Local assistance grants only; never a control-device or cloud credential.</summary>
public sealed class AssistanceTrustRegistry
{
    private sealed record Grant(string Id, string Name, string CredentialHash, bool AllowView,
        bool AllowControl, DateTimeOffset CreatedAt);
    private sealed record Store(int Version, bool Enabled, Grant[] Devices);
    private sealed record Challenge(string ClientId, string Mode, DateTimeOffset ExpiresAt);
    private readonly Action<byte[]> _save;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);
    private Store _store = new(1, true, []);
    public bool PersistencePending { get; private set; }
    public string? PersistenceError { get; private set; }

    public AssistanceTrustRegistry(Action<byte[]> save, byte[]? recovered = null, TimeProvider? clock = null)
    {
        _save = save; _clock = clock ?? TimeProvider.System;
        if (recovered is null) return;
        if (recovered.Length > 262144) throw new InvalidDataException("Assistance trust store is too large.");
        _store = JsonSerializer.Deserialize<Store>(recovered, RuntimeProtocol.JsonOptions)
            ?? throw new InvalidDataException("Invalid assistance trust store.");
        if (_store.Version != 1 || _store.Devices is null || _store.Devices.Length > 128 ||
            _store.Devices.Any(g => g is null || !ValidId(g.Id) || !ValidName(g.Name) ||
                !ValidSecret(g.CredentialHash) || !g.AllowView && !g.AllowControl) ||
            _store.Devices.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != _store.Devices.Length)
            throw new InvalidDataException("Invalid assistance trust store.");
    }

    public bool Enabled => _store.Enabled;
    public AssistanceTrustSnapshot Snapshot() => new(Enabled, _store.Devices.Select(g => new AssistanceTrustedDevice(
        g.Id, g.Name, g.AllowView && g.AllowControl ? "view,control" : g.AllowControl ? "control" : "view",
        g.CreatedAt, g.AllowView, g.AllowControl)).ToArray(), PersistencePending, PersistenceError);

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            _challenges.Clear();
            if (_store.Enabled || PersistencePending) PersistRevocation(_store with { Enabled = false });
        }
        else
        {
            EnsurePersistenceComplete();
            if (!_store.Enabled) Persist(_store with { Enabled = true });
        }
    }

    public void GrantDevice(string clientId, string name, string credentialHash, bool allowView, bool allowControl)
    {
        EnsurePersistenceComplete();
        if (!ValidId(clientId) || !ValidName(name) || !ValidSecret(credentialHash) || !allowView && !allowControl)
            throw new ArgumentException("Invalid assistance grant.");
        var previous = _store.Devices.SingleOrDefault(g => g.Id == clientId);
        if (previous is null && _store.Devices.Length >= 128)
            throw new InvalidOperationException("Assistance trust list is full.");
        var grant = new Grant(clientId, name, credentialHash.ToUpperInvariant(), allowView, allowControl,
            previous?.CreatedAt ?? _clock.GetUtcNow());
        Persist(_store with { Devices = _store.Devices.Where(g => g.Id != clientId).Append(grant).ToArray() });
        RemoveChallenges(clientId);
    }

    public void Revoke(string clientId)
    {
        if (!ValidId(clientId)) throw new ArgumentException("Invalid assistance client identity.");
        RemoveChallenges(clientId);
        PersistRevocation(_store with { Devices = _store.Devices.Where(g => g.Id != clientId).ToArray() });
    }

    public void RetryPersistence()
    {
        if (PersistencePending) PersistRevocation(_store);
    }

    public AssistanceTrustChallenge IssueChallenge(string clientId, string mode)
    {
        _ = Resolve(clientId, mode);
        foreach (var id in _challenges.Where(p => p.Value.ExpiresAt <= _clock.GetUtcNow()).Select(p => p.Key).ToArray())
            _challenges.Remove(id);
        if (_challenges.Count >= 256) throw new InvalidOperationException("Too many pending assistance requests.");
        string idValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = _clock.GetUtcNow().AddSeconds(60);
        _challenges.Add(idValue, new(clientId, mode, expires));
        return new(idValue, expires);
    }

    public void Authenticate(string clientId, string credential, string challengeId, string authProof,
        string mode, string sessionProof, string certificate)
    {
        // Every attempt consumes its challenge, including bad signatures and expired grants.
        if (!ValidSecret(challengeId) || !_challenges.Remove(challengeId.ToUpperInvariant(), out var challenge) ||
            challenge.ClientId != clientId || challenge.Mode != mode || challenge.ExpiresAt <= _clock.GetUtcNow())
            throw new UnauthorizedAccessException("Assistance challenge expired or already used.");
        var grant = Resolve(clientId, mode);
        if (!ValidSecret(credential) || !ValidSecret(authProof) || !ValidSecret(sessionProof))
            throw new UnauthorizedAccessException("Invalid assistance authentication.");
        byte[] secret = Convert.FromHexString(credential);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), Convert.FromHexString(grant.CredentialHash)))
                throw new UnauthorizedAccessException("Invalid assistance authentication.");
            byte[] expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(
                AuthenticationMessage(clientId, challengeId, mode, sessionProof, certificate)));
            if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(authProof)))
                throw new UnauthorizedAccessException("Invalid assistance authentication.");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    // Wire contract: UTF-8, LF separators, no trailing LF; all hex fields uppercase.
    public static string AuthenticationMessage(string clientId, string challengeId, string mode,
        string sessionProof, string certificate) => string.Join("\n", "StreamHub.Assistance.Trust.v1", clientId,
            challengeId.ToUpperInvariant(), mode, sessionProof.ToUpperInvariant(),
            Convert.ToHexString(SHA256.HashData(MediaIdentityPolicy.ParseCertificate("remote", certificate))));

    public static string HashCredential(string credential)
    {
        if (!ValidSecret(credential)) throw new ArgumentException("Invalid assistance credential.");
        byte[] secret = Convert.FromHexString(credential);
        try { return Convert.ToHexString(SHA256.HashData(secret)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public bool Allows(string clientId, string mode)
    {
        try { _ = Resolve(clientId, mode); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private Grant Resolve(string clientId, string mode)
    {
        var grant = _store.Devices.SingleOrDefault(g => g.Id == clientId);
        if (!Enabled || grant is null || !(mode == "view" && grant.AllowView || mode == "control" && grant.AllowControl))
            throw new UnauthorizedAccessException("Assistance device is unknown, disabled, revoked or lacks permission.");
        return grant;
    }

    private void RemoveChallenges(string clientId)
    {
        foreach (var id in _challenges.Where(p => p.Value.ClientId == clientId).Select(p => p.Key).ToArray())
            _challenges.Remove(id);
    }

    private void PersistRevocation(Store store)
    {
        // Denials take effect before disk I/O. Retries write this state, never the stale durable grants.
        _store = store;
        PersistencePending = true;
        try { Persist(store); }
        catch
        {
            PersistenceError = "权限撤销已生效，但未能保存，正在重试；重启前请确认保存完成。";
            throw;
        }
    }

    private void EnsurePersistenceComplete()
    {
        if (PersistencePending) throw new InvalidOperationException("权限撤销尚未保存，请等待后台重试完成。");
    }

    private void Persist(Store store)
    {
        _save(JsonSerializer.SerializeToUtf8Bytes(store, RuntimeProtocol.JsonOptions));
        _store = store;
        PersistencePending = false;
        PersistenceError = null;
    }
    internal static bool ValidId(string? value) => value is { Length: >= 8 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    internal static bool ValidSecret(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool ValidName(string? value) => value is { Length: >= 1 and <= 64 } && !value.Any(char.IsControl);
}
