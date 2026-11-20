using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

/// <summary>Assistance scope only; never issues or writes a ControlDevice grant.</summary>
public sealed class AssistanceRegistry
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private readonly TimeProvider _clock;
    private readonly Action<byte[]> _save;
    private readonly Action<string, string, string?, string?> _audit;
    private readonly AssistanceTrustRegistry _trust;
    private readonly HashSet<string> _logins = new(StringComparer.Ordinal);
    private AssistanceSnapshot _snapshot = new("idle");
    private AssistanceJournal _journal = new(1, null);
    private byte[]? _proofHash;
    private string? _pendingCredentialHash;
    private (bool View, bool Control)? _pendingGrant;
    private int _failures;
    private DateTimeOffset _hardDeadline;
    public AssistanceRegistry(Action<byte[]> save, Action<string, string, string?, string?> audit,
        byte[]? recovered = null, TimeProvider? clock = null, AssistanceTrustRegistry? trust = null)
    {
        _save = save; _audit = audit; _clock = clock ?? TimeProvider.System;
        _trust = trust ?? new(_ => { }, clock: _clock);
        if (recovered is null) return;
        if (recovered.Length > 16384) throw new InvalidDataException("协助清理记录过大。");
        _journal = JsonSerializer.Deserialize<AssistanceJournal>(recovered, RuntimeProtocol.JsonOptions)
            ?? throw new InvalidDataException("协助清理记录无效。");
        if (_journal.Version != 1 || _journal.SessionId is not null &&
            (!Guid.TryParseExact(_journal.SessionId, "D", out var id) || id == Guid.Empty ||
             _journal.Mode is not (null or "view" or "control") ||
             _journal.ClientId is not null && !AssistanceTrustRegistry.ValidId(_journal.ClientId)))
            throw new InvalidDataException("协助清理记录无效。");
        if (_journal.SessionId is not null)
        {
            _ = MediaIdentityPolicy.ParseCertificate("remote", _journal.Certificate);
            _snapshot = new("cleanup-pending", SessionId: _journal.SessionId, Name: _journal.Name,
                Mode: _journal.Mode, Message: "正在结束上次协助", ClientId: _journal.ClientId, Trusted: _journal.Trusted);
        }
    }
    public AssistanceJournal Journal => _journal;
    public bool PersistencePending => _trust.PersistencePending;
    public bool Reserved => Snapshot().State is "pending" or "starting" or "active" or "cleanup-pending";
    public AssistanceSnapshot Snapshot()
    {
        Expire();
        var trust = _trust.Snapshot();
        return _snapshot with { Enabled = trust.Enabled,
            CanTrust = _snapshot.State == "pending" && _pendingCredentialHash is not null,
            TrustedDevices = trust.TrustedDevices, PersistencePending = trust.PersistencePending,
            PersistenceError = trust.PersistenceError,
            Message = trust.PersistencePending ? trust.PersistenceError! : _snapshot.Message };
    }
    public AssistanceSnapshot Login(string loginId)
    {
        if (!Guid.TryParse(loginId, out var id) || id == Guid.Empty) throw new ArgumentException("Invalid owner login identity.");
        loginId = id.ToString("D");
        if (_logins.Contains(loginId)) return Snapshot();
        if (_logins.Count >= 4096) throw new InvalidOperationException("Too many owner login sessions.");
        var result = RefreshOffer(force: true);
        _logins.Add(loginId);
        _audit("owner-login", "local", null, null);
        return result;
    }
    public AssistanceSnapshot SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            try { End("owner-disabled"); }
            finally { _trust.SetEnabled(false); }
        }
        else _trust.SetEnabled(true);
        _audit(enabled ? "assistance-enabled" : "assistance-disabled", "local", null, null);
        return enabled ? RefreshOffer(force: true) : Snapshot();
    }
    public AssistanceSnapshot RefreshOffer(bool force = false)
    {
        Expire();
        if (_trust.Enabled && !Reserved && (force || _snapshot.State != "offered")) return Start();
        return Snapshot();
    }
    public AssistanceSnapshot Start()
    {
        if (Reserved) throw new InvalidOperationException("请先结束当前协助并完成清理。");
        if (!_trust.Enabled) _trust.SetEnabled(true);
        string code = new(Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        _failures = 0;
        _snapshot = new("offered", code.Insert(4, "-"), _clock.GetUtcNow().AddMinutes(10),
            Message: "协助码已生成，等待连接");
        _audit("code-created", "local", null, null);
        return Snapshot();
    }
    public AssistanceReply Join(string code, string name, string proof, string certificate, string source,
        string? clientId = null, string? credential = null)
    {
        Expire();
        if (!_trust.Enabled || _snapshot.State != "offered") throw new InvalidOperationException("协助码已失效或已有会话。");
        string normalized = (code ?? "").Trim().ToUpperInvariant().Replace("-", "");
        if (normalized.Length != 8 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized),
            Encoding.ASCII.GetBytes(_snapshot.Code!.Replace("-", ""))))
        {
            _audit("code-rejected", source, null, null);
            if (++_failures >= 5) _snapshot = new("expired", Message: "协助码输错五次，已作废");
            throw new InvalidOperationException("协助码不正确或已失效。");
        }
        if (name is not { Length: >= 1 and <= 64 } || name.Any(char.IsControl) || !ValidProof(proof) ||
            (clientId is not null || credential is not null) &&
            (!AssistanceTrustRegistry.ValidId(clientId) || !AssistanceTrustRegistry.ValidSecret(credential)))
            throw new InvalidOperationException("协助端信息无效。");
        string? credentialHash = credential is null ? null : AssistanceTrustRegistry.HashCredential(credential);
        return CreatePending(name, proof, certificate, source, clientId, credentialHash, trusted: false);
    }
    public AssistanceTrustChallenge Challenge(string clientId, string mode)
    {
        if (Reserved) throw new InvalidOperationException("请先结束当前协助并完成清理。");
        return _trust.IssueChallenge(clientId, mode);
    }
    public AssistanceReply JoinTrusted(string clientId, string credential, string challengeId, string authProof,
        string mode, string name, string proof, string certificate, string source)
    {
        if (Reserved) throw new InvalidOperationException("请先结束当前协助并完成清理。");
        _trust.Authenticate(clientId, credential, challengeId, authProof, mode, proof, certificate);
        if (name is not { Length: >= 1 and <= 64 } || name.Any(char.IsControl))
            throw new ArgumentException("协助端信息无效。");
        var reply = CreatePending(name, proof, certificate, source, clientId, null, trusted: true);
        _audit("trusted-authenticated", source, reply.SessionId, mode);
        return reply;
    }
    private AssistanceReply CreatePending(string name, string proof, string certificate, string source,
        string? clientId, string? credentialHash, bool trusted)
    {
        var der = MediaIdentityPolicy.ParseCertificate("remote", certificate);
        string id = Guid.NewGuid().ToString("D");
        byte[] hash = SHA256.HashData(Convert.FromHexString(proof));
        // Bind the comparison to this exact session, proof and media certificate.
        string comparison = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            id + ":" + proof.ToUpperInvariant() + ":" + Convert.ToHexString(SHA256.HashData(der)))))[..8].Insert(4, "-");
        var journal = new AssistanceJournal(1, id, certificate, name, ClientId: clientId, Trusted: trusted);
        Persist(journal); // The recovery fence precedes every media mutation.
        _proofHash = hash;
        _pendingCredentialHash = credentialHash;
        _snapshot = new("pending", ExpiresAt: _clock.GetUtcNow().AddSeconds(90), SessionId: id,
            Name: name, Comparison: comparison, Message: "等待选择本次权限", ClientId: clientId, Trusted: trusted);
        _audit("request-pending", source, id, null);
        return Reply();
    }
    public void Approve(string id, string mode, bool trustDevice = false, bool? allowView = null, bool? allowControl = null)
    {
        Expire();
        if (_snapshot.State != "pending" || _snapshot.SessionId != id || mode is not ("view" or "control"))
            throw new InvalidOperationException("请求已结束，或尚未选择有效授权模式。");
        if (trustDevice && _trust.PersistencePending)
            throw new InvalidOperationException("权限撤销尚未保存，请等待后台重试完成。");
        if (trustDevice && (_snapshot.Trusted || _pendingCredentialHash is null || _snapshot.ClientId is null))
            throw new InvalidOperationException("此请求未提供可保存的设备凭据。");
        if (_snapshot.Trusted && !_trust.Allows(_snapshot.ClientId!, mode))
            throw new UnauthorizedAccessException("该设备未获准使用此协助模式。");
        var grant = (View: allowView ?? mode == "view", Control: allowControl ?? mode == "control");
        if (trustDevice && !grant.View && !grant.Control) throw new ArgumentException("请选择受信任设备权限。");
        Persist(_journal with { Mode = mode });
        _pendingGrant = trustDevice ? grant : null;
        _snapshot = _snapshot with { State = "starting", Mode = mode,
            ExpiresAt = _clock.GetUtcNow().AddSeconds(60), Message = "正在准备临时协助" };
        _audit(_journal.Trusted ? "trusted-approved" : "owner-approved", "local", id, mode);
    }
    public void Activate(bool commitTrust = true)
    {
        Expire();
        if (_snapshot.State != "starting") throw new InvalidOperationException("协助准备已取消或超时。");
        _hardDeadline = _clock.GetUtcNow().AddHours(2);
        _snapshot = _snapshot with { State = "active", ExpiresAt = _clock.GetUtcNow().AddSeconds(25),
            Message = _snapshot.Mode == "view" ? "临时协助 · 仅观看" : "临时协助 · 允许控制" };
        if (commitTrust) CommitTrust();
        _audit("session-ready", "local", _snapshot.SessionId, _snapshot.Mode);
    }
    public void CommitTrust()
    {
        Expire();
        if (_snapshot.State != "active") throw new InvalidOperationException("协助尚未就绪。");
        if (_pendingGrant is not { } grant) return;
        _trust.GrantDevice(_snapshot.ClientId!, _snapshot.Name!, _pendingCredentialHash!, grant.View, grant.Control);
        _pendingGrant = null;
        _snapshot = _snapshot with { Trusted = true };
        _audit("trust-granted", "local", _snapshot.SessionId, _snapshot.Mode);
    }
    public AssistanceReply Poll(string id, string proof)
    { Authenticate(id, proof); return Reply(); }
    public AssistanceReply Heartbeat(string id, string proof)
    {
        Authenticate(id, proof);
        if (_snapshot.State != "active") throw new UnauthorizedAccessException("协助尚未获准或已结束。");
        _snapshot = _snapshot with { ExpiresAt = new[] { _clock.GetUtcNow().AddSeconds(25), _hardDeadline }.Min() };
        return Reply();
    }
    public void EndRemote(string id, string proof) { Authenticate(id, proof); End("peer-ended"); }
    public void Revoke(string clientId)
    {
        try { if (_snapshot.ClientId == clientId) End("trust-revoked"); }
        finally { _trust.Revoke(clientId); }
        _audit("trust-revoked", "local", _snapshot.ClientId == clientId ? _snapshot.SessionId : null, null);
    }
    public void RetryPersistence() => _trust.RetryPersistence();
    public void End(string reason)
    {
        if (_snapshot.State is "idle" or "ended" or "expired") return;
        string? id = _snapshot.SessionId;
        _snapshot = _snapshot with { State = id is null ? "ended" : "cleanup-pending", Code = null,
            ExpiresAt = null, Message = id is null ? "临时协助已关闭" : "正在结束协助" };
        if (_proofHash is not null) CryptographicOperations.ZeroMemory(_proofHash);
        _proofHash = null; // Revocation takes effect even if later disk I/O fails.
        _pendingCredentialHash = null;
        _pendingGrant = null;
        _audit(reason, "local", id, _snapshot.Mode);
    }
    public void Cleaned()
    {
        if (_snapshot.State != "cleanup-pending") throw new InvalidOperationException("没有待清理会话。");
        var id = _journal.SessionId; var mode = _journal.Mode;
        Persist(new(1, null));
        _snapshot = new("ended", Message: "协助已结束");
        _audit("cleanup-complete", "local", id, mode);
    }
    private void Authenticate(string id, string proof)
    {
        Expire();
        if (_proofHash is null || _snapshot.SessionId != id || !ValidProof(proof) ||
            !CryptographicOperations.FixedTimeEquals(_proofHash, SHA256.HashData(Convert.FromHexString(proof))))
            throw new UnauthorizedAccessException("协助凭据无效或已回收。");
    }
    private void Expire()
    {
        if (_journal.Trusted && _snapshot.Mode is { } mode && _snapshot.State is "pending" or "starting" or "active" &&
            !_trust.Allows(_snapshot.ClientId!, mode))
            End("trust-unavailable");
        if (_snapshot.ExpiresAt is { } expires && _clock.GetUtcNow() >= expires)
            End("expired");
    }
    private AssistanceReply Reply()
    {
        var grant = _snapshot.Trusted
            ? _trust.Snapshot().TrustedDevices.SingleOrDefault(g => g.Id == _snapshot.ClientId) : null;
        string? mode = grant is null ? null : _snapshot.Mode == "control" && grant.AllowControl ? "control" :
            grant.AllowView ? "view" : "control";
        return new(_snapshot.SessionId!, _snapshot.State, _snapshot.Comparison!, _snapshot.Mode,
            Trusted: grant is not null, TrustedMode: mode);
    }
    private void Persist(AssistanceJournal journal)
    { _save(JsonSerializer.SerializeToUtf8Bytes(journal, RuntimeProtocol.JsonOptions)); _journal = journal; }
    private static bool ValidProof(string? proof) => proof is { Length: 64 } && proof.All(char.IsAsciiHexDigit);
}
