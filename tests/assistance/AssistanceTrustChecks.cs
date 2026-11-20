using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;
using StreamHub.Service;

static class AssistanceTrustChecks
{
    public static async Task RunAsync(Action<bool, string> check, Action<Action, string> refuses, string certificate, string proof, string fixture)
    {
        const string clientId = "fixture-trusted-client";
        string credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[]? saved = null;
        var clock = new Clock();
        var trust = new AssistanceTrustRegistry(bytes => saved = bytes, clock: clock);
        var registry = new AssistanceRegistry(_ => { }, (_, _, _, _) => { }, clock: clock, trust: trust);
        check(trust.Enabled, "assistance defaults enabled without cloud or settings access");
        string login = Guid.NewGuid().ToString("D");
        var offer = registry.Login(login);
        check(!registry.Reserved && offer.State == "offered", "idle assistance offer reserves no ordinary stream or update resources");
        check(registry.Login(login).Code == offer.Code, "owner login token refreshes code only once");
        var refreshed = registry.Login(Guid.NewGuid().ToString("D"));
        check(refreshed.Code != offer.Code, "a new owner login refreshes the assistance code");
        refuses(() => registry.Join(offer.Code!, "peer", proof, certificate, "fixture"), "prior login code is invalid");
        clock.Advance(TimeSpan.FromMinutes(10));
        offer = registry.RefreshOffer();
        check(offer.Code != refreshed.Code && offer.State == "offered" && !registry.Reserved,
            "periodic refresh rotates expired offers without resource reservation");

        var pending = registry.Join(offer.Code!, "peer", proof, certificate, "fixture", clientId, credential);
        check(registry.Snapshot().CanTrust && registry.Reserved && !pending.Trusted,
            "persistent identity only offers owner opt-in and does not grant itself trust");
        check(registry.RefreshOffer(true).SessionId == pending.SessionId, "auto refresh preserves pending approval");
        registry.Approve(pending.SessionId, "control"); registry.Activate();
        check(trust.Snapshot().TrustedDevices.Length == 0 && !registry.Poll(pending.SessionId, proof).Trusted,
            "ordinary approval defaults to no persistent trust");
        registry.End("test"); registry.Cleaned(); offer = registry.RefreshOffer();
        pending = registry.Join(offer.Code!, "peer", proof, certificate, "fixture", clientId, credential);
        registry.Approve(pending.SessionId, "view", trustDevice: true);
        registry.Activate(commitTrust: false);
        check(trust.Snapshot().TrustedDevices.Length == 0 && !registry.Poll(pending.SessionId, proof).Trusted,
            "media-ready but unconfirmed host notice does not persist or advertise trust");
        registry.End("simulated-notice-failure"); registry.Cleaned(); offer = registry.RefreshOffer();
        check(trust.Snapshot().TrustedDevices.Length == 0, "failed preparation leaves no remembered device");
        pending = registry.Join(offer.Code!, "peer", proof, certificate, "fixture", clientId, credential);
        registry.Approve(pending.SessionId, "view", trustDevice: true); registry.Activate();
        var ready = registry.Poll(pending.SessionId, proof);
        check(ready.Trusted && ready.TrustedMode == "view", "successful explicit grant confirms exact remembered mode");
        check(trust.Allows(clientId, "view") && !trust.Allows(clientId, "control"), "view trust grants no control permission");
        check(saved is not null && !Encoding.UTF8.GetString(saved).Contains(credential) &&
            Encoding.UTF8.GetString(saved).Contains(AssistanceTrustRegistry.HashCredential(credential)),
            "local trust file persists only a hash of the client credential");
        check(!JsonSerializer.Serialize(registry.Snapshot(), RuntimeProtocol.JsonOptions).Contains("credential", StringComparison.OrdinalIgnoreCase),
            "owner snapshots expose no credential or hash");
        var recovered = new AssistanceTrustRegistry(_ => { }, saved, clock);
        check(recovered.Allows(clientId, "view") && !recovered.Allows(clientId, "control"), "local grants survive registry restart with exact permissions");
        registry.End("test"); registry.Cleaned(); registry.RefreshOffer();
        refuses(() => registry.Challenge("unknown-device", "view"), "unknown device cannot request code-free access");
        refuses(() => registry.Challenge(clientId, "control"), "view-only device cannot request a control challenge");

        string Sign(AssistanceTrustChallenge challenge, string mode = "view", string? signedCertificate = null,
            string? signedProof = null, string? secret = null)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(signedCertificate ?? certificate)));
            string message = $"StreamHub.Assistance.Trust.v1\n{clientId}\n{challenge.ChallengeId}\n{mode}\n{signedProof ?? proof}\n{hash}";
            return Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(secret ?? credential), Encoding.UTF8.GetBytes(message)));
        }
        void Authenticate(AssistanceTrustChallenge challenge, string? signature = null, string? secret = null,
            string mode = "view", string? receivedProof = null, string? receivedCertificate = null) =>
            trust.Authenticate(clientId, secret ?? credential, challenge.ChallengeId, signature ?? Sign(challenge),
                mode, receivedProof ?? proof, receivedCertificate ?? certificate);

        var challenge = registry.Challenge(clientId, "view");
        var trusted = registry.JoinTrusted(clientId, credential, challenge.ChallengeId, Sign(challenge),
            "view", "peer", proof, certificate, "fixture");
        registry.Approve(trusted.SessionId, "view"); registry.Activate();
        check(registry.Poll(trusted.SessionId, proof).Trusted, "trusted authenticated device opens a new session without an assistance code");
        refuses(() => registry.Approve(trusted.SessionId, "control"), "trusted active view session cannot escalate");
        registry.End("test"); registry.Cleaned();
        refuses(() => Authenticate(challenge), "successful authentication consumes challenge against replay");
        challenge = trust.IssueChallenge(clientId, "view");
        clock.Advance(TimeSpan.FromSeconds(61));
        refuses(() => Authenticate(challenge), "expired challenge denies access");
        challenge = trust.IssueChallenge(clientId, "view");
        refuses(() => Authenticate(challenge, signature: new string('0', 64)), "forged authentication proof denies access");
        refuses(() => Authenticate(challenge), "failed authentication also consumes challenge");
        challenge = trust.IssueChallenge(clientId, "view");
        string wrongCredential = new('E', 64);
        refuses(() => Authenticate(challenge, signature: Sign(challenge, secret: wrongCredential), secret: wrongCredential),
            "self-signed proof with unknown credential cannot impersonate trusted id");
        challenge = trust.IssueChallenge(clientId, "view");
        refuses(() => Authenticate(challenge, mode: "control"), "signed view request cannot change mode");
        challenge = trust.IssueChallenge(clientId, "view");
        refuses(() => Authenticate(challenge, receivedProof: new string('B', 64)), "authentication binds the ephemeral session proof");
        using var otherKey = RSA.Create(2048);
        using var otherCert = new CertificateRequest("CN=other-fixture", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        challenge = trust.IssueChallenge(clientId, "view");
        refuses(() => Authenticate(challenge, receivedCertificate: Convert.ToBase64String(otherCert.RawData)),
            "authentication binds the exact media certificate");

        challenge = trust.IssueChallenge(clientId, "view");
        registry.SetEnabled(false);
        check(!registry.Snapshot().Enabled && registry.Snapshot().Code is null, "disable immediately removes offers");
        var disabledTrust = new AssistanceTrustRegistry(_ => { }, saved, clock);
        var disabled = new AssistanceRegistry(_ => { }, (_, _, _, _) => { }, trust: disabledTrust, clock: clock);
        check(!disabled.Login(Guid.NewGuid().ToString("D")).Enabled && disabled.RefreshOffer().Code is null,
            "persisted disable survives restart, login and periodic refresh");
        refuses(() => Authenticate(challenge), "disable invalidates outstanding challenge");
        registry.SetEnabled(true);
        challenge = trust.IssueChallenge(clientId, "view");
        trusted = registry.JoinTrusted(clientId, credential, challenge.ChallengeId, Sign(challenge),
            "view", "peer", proof, certificate, "fixture");
        registry.Approve(trusted.SessionId, "view"); registry.Activate();
        var beforeRevoke = trust.IssueChallenge(clientId, "view");
        registry.Revoke(clientId);
        refuses(() => registry.Poll(trusted.SessionId, proof), "revocation immediately invalidates active session proof");
        check(registry.Snapshot().State == "cleanup-pending", "active trust revocation requires media cleanup before another session");
        refuses(() => Authenticate(beforeRevoke), "revocation invalidates outstanding challenge");
        refuses(() => trust.IssueChallenge(clientId, "view"), "revoked device cannot obtain a new challenge");
        check(!new AssistanceTrustRegistry(_ => { }, saved, clock).Allows(clientId, "view"), "revocation remains effective after restart");
        registry.Cleaned();

        trust.GrantDevice(clientId, "control peer", AssistanceTrustRegistry.HashCredential(credential), false, true);
        check(!trust.Allows(clientId, "view") && trust.Allows(clientId, "control"), "control and view permissions are independent");
        challenge = trust.IssueChallenge(clientId, "control");
        trusted = registry.JoinTrusted(clientId, credential, challenge.ChallengeId, Sign(challenge, "control"),
            "control", "peer", proof, certificate, "fixture");
        registry.Approve(trusted.SessionId, "control"); registry.Activate();
        check(registry.Poll(trusted.SessionId, proof).Mode == "control", "control-only grant survives pending-to-active transition");
        registry.End("test"); registry.Cleaned();
        trust.GrantDevice(clientId, "both modes", AssistanceTrustRegistry.HashCredential(credential), true, true);
        challenge = trust.IssueChallenge(clientId, "view");
        trusted = registry.JoinTrusted(clientId, credential, challenge.ChallengeId, Sign(challenge),
            "view", "peer", proof, certificate, "fixture");
        registry.Approve(trusted.SessionId, "view"); registry.Activate();
        check(registry.Poll(trusted.SessionId, proof).TrustedMode == "view", "combined grants return a client-compatible accepted mode");
        registry.End("test"); registry.Cleaned();
        bool fail = true;
        var fragile = new AssistanceTrustRegistry(_ => { if (fail) throw new IOException("fixture"); });
        refuses(() => fragile.SetEnabled(false), "persist failure is reported to owner");
        check(!fragile.Enabled && fragile.PersistencePending && fragile.PersistenceError is not null,
            "failed disable is effective in memory and explicitly pending persistence");
        fail = false; fragile.RetryPersistence();
        check(!fragile.Enabled && !fragile.PersistencePending && fragile.PersistenceError is null,
            "retry durably disables assistance and clears pending status");
        fail = true;
        refuses(() => fragile.SetEnabled(true), "positive enable still requires a successful commit");
        check(!fragile.Enabled && !fragile.PersistencePending, "failed positive enable neither grants access nor queues an enable");
        refuses(() => fragile.GrantDevice(clientId, "peer", AssistanceTrustRegistry.HashCredential(credential), true, false),
            "positive grant still requires a successful commit");
        check(fragile.Snapshot().TrustedDevices.Length == 0, "failed positive grant creates no effective permission");

        await CheckRoutesAsync(check, credential, certificate, proof);
        await CheckRuntimePolicyAsync(check, certificate, proof, fixture);
        await CheckPersistenceFailuresAsync(check, refuses, credential, certificate, proof, fixture);
    }

    private static async Task CheckPersistenceFailuresAsync(Action<bool, string> check, Action<Action, string> refuses,
        string credential, string certificate, string proof, string fixture)
    {
        const string clientId = "fixture-revocation-client";
        foreach (bool disable in new[] { false, true })
        {
            string operation = disable ? "disable" : "revoke";
            bool fail = false;
            byte[]? durable = null;
            int attempts = 0;
            Action? observeSave = null;
            var trust = new AssistanceTrustRegistry(bytes => {
                attempts++;
                observeSave?.Invoke();
                if (fail) throw new IOException("fixture persistence failure");
                durable = bytes;
            });
            trust.GrantDevice(clientId, "peer", AssistanceTrustRegistry.HashCredential(credential), true, false);
            var registry = new AssistanceRegistry(_ => { }, (_, _, _, _) => { }, trust: trust);
            var challenge = registry.Challenge(clientId, "view");
            var outstanding = trust.IssueChallenge(clientId, "view");
            string outstandingProof = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(credential), Encoding.UTF8.GetBytes(
                AssistanceTrustRegistry.AuthenticationMessage(clientId, outstanding.ChallengeId, "view", proof, certificate))));
            string signature = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(credential), Encoding.UTF8.GetBytes(
                AssistanceTrustRegistry.AuthenticationMessage(clientId, challenge.ChallengeId, "view", proof, certificate))));
            var session = registry.JoinTrusted(clientId, credential, challenge.ChallengeId, signature,
                "view", "peer", proof, certificate, "fixture");
            registry.Approve(session.SessionId, "view"); registry.Activate();
            byte[] previous = durable!.ToArray();
            observeSave = () => {
                check(!trust.Allows(clientId, "view"), operation + " denies grant before attempting save");
                refuses(() => registry.Heartbeat(session.SessionId, proof), operation + " terminates session before attempting save");
            };
            fail = true;
            refuses(() => { if (disable) registry.SetEnabled(false); else registry.Revoke(clientId); }, operation + " reports failed save");
            observeSave = null;
            var snapshot = registry.Snapshot();
            check(snapshot.PersistencePending && snapshot.PersistenceError is not null && snapshot.State == "cleanup-pending",
                operation + " exposes pending persistence and terminated session");
            check(snapshot.Message == snapshot.PersistenceError && snapshot.Message.Length <= 80 &&
                !snapshot.Message.Contains("IOException", StringComparison.Ordinal),
                operation + " exposes concise Chinese guidance without exception details");
            check(durable!.SequenceEqual(previous), operation + " does not claim the failed write changed durable storage");
            refuses(() => registry.Heartbeat(session.SessionId, proof), operation + " failure cannot revive heartbeat");
            refuses(() => trust.IssueChallenge(clientId, "view"), operation + " failure blocks new trusted challenges");
            refuses(() => trust.Authenticate(clientId, credential, outstanding.ChallengeId, outstandingProof, "view", proof, certificate),
                operation + " failure discards previously issued challenges");
            refuses(() => registry.RetryPersistence(), operation + " retry failure remains visible");
            check(registry.PersistencePending && !trust.Allows(clientId, "view"), operation + " retry failure retains deny state");
            int beforeBlockedMutation = attempts;
            if (disable)
            {
                refuses(() => trust.SetEnabled(true), "positive enable is rejected while disable persistence is pending");
                check(!trust.Enabled, "blocked positive enable retains effective disable");
            }
            else
            {
                refuses(() => trust.GrantDevice(clientId, "peer", AssistanceTrustRegistry.HashCredential(credential), true, true),
                    "positive regrant is rejected while revocation persistence is pending");
                check(!trust.Allows(clientId, "view"), "blocked positive regrant retains effective revocation");
            }
            refuses(() => trust.GrantDevice("unrelated-client", "other", AssistanceTrustRegistry.HashCredential(credential), true, false),
                operation + " pending persistence blocks unrelated positive grants");
            check(attempts == beforeBlockedMutation, operation + " blocked grants cannot write over queued denials");
            registry.Cleaned();
            await using var host = new HostRuntime(fixture, Path.Combine(fixture, "persistence-" + operation), true);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(HostRuntime).GetField("_assistance", flags)!.SetValue(host, registry);
            var reconcile = typeof(HostRuntime).GetMethod("ReconcileAssistanceAsync", flags)!;
            int beforeRetry = attempts;
            await (Task)reconcile.Invoke(host, [CancellationToken.None])!;
            check(attempts == beforeRetry + 1 && registry.PersistencePending && !trust.Allows(clientId, "view"),
                operation + " service reconcile retries without throwing or restoring old grants");
            fail = false;
            refuses(() => trust.SetEnabled(true), operation + " enable remains blocked after disk recovery until retry commits");
            refuses(() => trust.GrantDevice("unrelated-client", "other", AssistanceTrustRegistry.HashCredential(credential), true, false),
                operation + " grants remain blocked after disk recovery until retry commits");
            typeof(HostRuntime).GetField("_assistancePersistenceRetry", flags)!.SetValue(host, default(DateTimeOffset));
            await (Task)reconcile.Invoke(host, [CancellationToken.None])!;
            check(!registry.PersistencePending && registry.Snapshot().PersistenceError is null,
                operation + " service reconcile clears pending status only after successful write");
            check(!new AssistanceTrustRegistry(_ => { }, durable).Allows(clientId, "view"),
                operation + " retry persists denial across registry restart");
            trust.SetEnabled(true);
            trust.GrantDevice("unrelated-client", "other", AssistanceTrustRegistry.HashCredential(credential), true, false);
            check(trust.Allows("unrelated-client", "view") && (disable || !trust.Allows(clientId, "view")),
                operation + " successful retry permits positive changes without restoring revoked devices");
        }

        await CheckFailedOwnerMutationAsync(check, refuses, credential, certificate, proof, fixture, disable: true);
        await CheckFailedOwnerMutationAsync(check, refuses, credential, certificate, proof, fixture, disable: false);
    }

    private static async Task CheckFailedOwnerMutationAsync(Action<bool, string> check, Action<Action, string> refuses,
        string credential, string certificate, string proof, string fixture, bool disable)
    {
        const string clientId = "fixture-owner-revoke";
        string operation = disable ? "disable" : "revoke";
        bool fail = false;
        byte[]? durable = null;
        var trust = new AssistanceTrustRegistry(bytes => {
            if (fail) throw new IOException("fixture persistence failure");
            durable = bytes;
        });
        trust.GrantDevice(clientId, "peer", AssistanceTrustRegistry.HashCredential(credential), true, false);
        var registry = new AssistanceRegistry(_ => { }, (_, _, _, _) => { }, trust: trust);
        var offer = registry.Start();
        var pending = registry.Join(offer.Code!, "peer", proof, certificate, "fixture", clientId, credential);
        registry.Approve(pending.SessionId, "view"); registry.Activate();
        await using var host = new HostRuntime(fixture, Path.Combine(fixture, "owner-" + operation), true);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(HostRuntime).GetField("_assistance", flags)!.SetValue(host, registry);
        typeof(HostRuntime).GetField("_assistanceMedia", flags)!.SetValue(host,
            new AssistanceMediaProfile(Path.Combine(fixture, "owner-" + operation)));
        var command = typeof(HostRuntime).GetMethod("AssistanceCommandAsync", flags)!;
        var request = disable ? new RuntimeRequest("assistance-enable", AssistanceEnabled: false) :
            new RuntimeRequest("assistance-trust-revoke", ClientId: clientId);
        fail = true;
        var response = await (Task<RuntimeResponse>)command.Invoke(host, [request, CancellationToken.None])!;
        var result = JsonSerializer.Deserialize<AssistanceSnapshot>(response.PayloadJson!, RuntimeProtocol.JsonOptions)!;
        check(!response.Success && response.ErrorStatus == 503 && result.PersistencePending && result.PersistenceError is not null,
            operation + " owner receives failure with precise pending snapshot instead of durable success");
        check(result.State == "ended", operation + " disk failure does not skip media cleanup");
        refuses(() => registry.Heartbeat(pending.SessionId, proof), operation + " owner failure leaves heartbeat denied");
        refuses(() => trust.IssueChallenge(clientId, "view"), operation + " owner failure leaves future trust denied");
        var enableResponse = await (Task<RuntimeResponse>)command.Invoke(host,
            [new RuntimeRequest("assistance-enable", AssistanceEnabled: true), CancellationToken.None])!;
        check(!enableResponse.Success && enableResponse.ErrorStatus == 409 && registry.PersistencePending,
            operation + " service rejects enable before API startup while a denial is pending");
        fail = false;
        typeof(HostRuntime).GetField("_assistancePersistenceRetry", flags)!.SetValue(host, default(DateTimeOffset));
        await (Task)typeof(HostRuntime).GetMethod("ReconcileAssistanceAsync", flags)!.Invoke(host, [CancellationToken.None])!;
        check(!registry.PersistencePending && !new AssistanceTrustRegistry(_ => { }, durable).Allows(clientId, "view"),
            operation + " owner failure is durably retried by service reconcile");
    }

    private static async Task CheckRuntimePolicyAsync(Action<bool, string> check, string certificate, string proof, string fixture)
    {
        await using var host = new HostRuntime(fixture, Path.Combine(fixture, "host-policy"), true);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var clock = new Clock();
        var registry = new AssistanceRegistry(_ => { }, (_, _, _, _) => { }, clock: clock);
        typeof(HostRuntime).GetField("_assistance", flags)!.SetValue(host, registry);
        await using var api = new ControlApiServer(new RuntimeClient((_, _) => throw new Exception("No live transport permitted.")),
            (_, _, _) => Task.CompletedTask, new([], []), X509KeyStorageFlags.EphemeralKeySet);
        typeof(ControlApiServer).GetField("<IsRunning>k__BackingField", flags)!.SetValue(api, true);
        typeof(HostRuntime).GetField("_controlApi", flags)!.SetValue(host, api);
        var lease = (LiveSessionState)typeof(HostRuntime).GetField("_remoteSession", flags)!.GetValue(host)!;
        lease.Prepare("fixture-lease", "fixture-client", DateTimeOffset.UtcNow);
        typeof(HostRuntime).GetField("_tabletLeaseId", flags)!.SetValue(host, "fixture-tablet");
        var command = typeof(HostRuntime).GetMethod("AssistanceCommandAsync", flags)!;
        async Task<RuntimeResponse> Send(RuntimeRequest request) => await (Task<RuntimeResponse>)command.Invoke(host,
            [request, CancellationToken.None])!;
        var login = await Send(new("assistance-login", RequestId: Guid.NewGuid().ToString("D")));
        check(login.Success && registry.Snapshot().Code is not null && lease.HasLease(DateTimeOffset.UtcNow),
            "real owner login offers assistance while an ordinary remote lease remains active");
        var refresh = await Send(new("assistance-refresh"));
        check(refresh.Success && (string?)typeof(HostRuntime).GetField("_tabletLeaseId", flags)!.GetValue(host) == "fixture-tablet",
            "owner refresh preserves an existing tablet lease");
        check((bool)typeof(HostRuntime).GetProperty("AssistanceReserved", flags)!.GetValue(host)! == false,
            "service update and ordinary streaming preflight do not reserve an offered code");
        string oldCode = registry.Snapshot().Code!;
        clock.Advance(TimeSpan.FromMinutes(11));
        await (Task)typeof(HostRuntime).GetMethod("ReconcileAssistanceAsync", flags)!.Invoke(host, [CancellationToken.None])!;
        check(registry.Snapshot().State == "offered" && registry.Snapshot().Code != oldCode && lease.HasLease(DateTimeOffset.UtcNow),
            "periodic service reconcile rotates code without ending ordinary remote lease");
        try
        {
            await Send(new("assistance-join", AssistanceCode: registry.Snapshot().Code, AssistanceProof: proof,
                ClientCertificate: certificate, Name: "peer", AssistanceSource: "fixture"));
            throw new Exception("Busy host accepted assistance.");
        }
        catch (InvalidOperationException) { check(registry.Snapshot().State == "offered", "busy host rejects redemption without consuming offer"); }
        try
        {
            await Send(new("assistance-disable", AssistanceSource: "fixture"));
            throw new Exception("Remote caller disabled assistance.");
        }
        catch (UnauthorizedAccessException) { check(registry.Snapshot().Enabled, "runtime denies owner commands marked as remote"); }
        var disabled = await Send(new("assistance-enable", AssistanceEnabled: false));
        check(disabled.Success && !registry.Snapshot().Enabled && registry.Snapshot().Code is null,
            "UI enable command with false disables and reconciles immediately");
    }

    private static async Task CheckRoutesAsync(Action<bool, string> check, string credential, string certificate, string proof)
    {
        RuntimeRequest? received = null;
        var runtime = new RuntimeClient((request, _) => {
            received = request;
            return Task.FromResult(new RuntimeResponse(true, "fixture", PayloadJson: "{\"state\":\"pending\"}"));
        });
        await using var server = new ControlApiServer(runtime, (_, _, _) => Task.CompletedTask,
            new([], []), X509KeyStorageFlags.EphemeralKeySet);
        var handler = typeof(ControlApiServer).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<int> Send(string route, object body, string method = "POST")
        {
            received = null;
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Method = method; context.Request.Path = "/v1/assistance/" + route;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
            context.Response.Body = new MemoryStream();
            await (Task)handler.Invoke(server, [context])!;
            return context.Response.StatusCode;
        }
        check(await Send("trusted-challenge", new { clientId = "fixture-client", mode = "view" }) == 200 &&
            received?.Command == "assistance-trusted-challenge" && received.ClientId == "fixture-client" &&
            received.AssistanceMode == "view", "HTTP challenge maps exact device and mode to assistance command");
        check(await Send("trusted-join", new { clientId = "fixture-client", credential, challengeId = new string('C', 64),
            authProof = new string('D', 64), mode = "view", proof, certificate, name = "peer" }) == 200 &&
            received?.AssistanceCredential == credential && received.AssistanceAuthProof == new string('D', 64) &&
            received.AssistanceSource == "192.0.2.1" && !received.TrustDevice,
            "HTTP trusted join forwards bound proof and cannot select owner trust");
        foreach (string ownerRoute in new[] { "login", "enable", "disable", "refresh", "trust-list", "trust-revoke", "approve-view", "approve-control" })
            check(await Send(ownerRoute, new { }) == 404 && received is null, "owner-only route not remotely exposed: " + ownerRoute);
        check(await Send("join", new { code = "ABCD-EFGH", name = "peer", proof, certificate, trustDevice = true }) == 400 && received is null,
            "wire refuses remote owner trust flag");
        check(await Send("trusted-join", new { }, "GET") == 400 && received is null, "trusted join requires POST body");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
