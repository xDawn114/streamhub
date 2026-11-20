using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using StreamHub.Host.Runtime;

if (args.Length == 2) { await ReceiverInterop.RunAsync(args[0], args[1]); return; }
if (args.Length != 1) throw new ArgumentException("Pass a new isolated fixture directory.");
string root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Use a new isolated fixture directory.");
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    passed++;
    Console.WriteLine("PASS " + label);
}
void Refuses(Action action, string label)
{
    try { action(); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or CryptographicException or JsonException)
    { Check(true, label); return; }
    throw new Exception(label);
}
async Task RefusesAsync(Func<Task> action, string label)
{
    try { await action(); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or CryptographicException or JsonException or OperationCanceledException or HttpRequestException)
    { Check(true, label); return; }
    throw new Exception(label);
}
string Connection(params string[] addresses) => "icontrol-help:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
    new AssistanceConnection(1, addresses.Length == 0 ? ["192.168.10.2"] : addresses, 49199, new string('A', 64), "ABCD-EFGH"), RuntimeProtocol.JsonOptions));
string path = Path.Combine(root, "preferences", "assistance-peers.json");
var store = new AssistancePeerStore(path);
Check(store.List().Count == 0 && !Directory.Exists(Path.GetDirectoryName(path)), "preview listing creates no files or live store");
var connection = AssistanceClient.ParseConnection(Connection());
var binding = store.Prepare(connection);
Check(store.List().Count == 0, "unapproved identity is never a saved peer");
var reopened = new AssistancePeerStore(path);
var restored = reopened.Prepare(connection);
Check(restored.ClientId == binding.ClientId && restored.Credential == binding.Credential,
    "random client identity and per-server credential survive new store instances");
Check(binding.ClientId.Length == 32 && binding.Credential.Length == 64, "identity and credential have the agreed wire sizes");
string disk = File.ReadAllText(path);
Check(!disk.Contains(binding.Credential) && !disk.Contains(binding.ClientId) && !disk.Contains("192.168.10.2"),
    "DPAPI envelope contains no raw credential, client identity or connection");
reopened.Confirm(restored, "control", "Trusted PC");
var peer = store.List().Single();
Check(peer.Id == connection.Pin && peer.Name == "Trusted PC" && peer.Mode == "control", "confirmed peer exposes UI metadata");
Check(AssistanceClient.ParseConnection(peer.ConnectionInfo).Code is null, "saved connection never contains a temporary code");
Check(typeof(AssistancePeer).GetProperties().All(p => p.Name is "Id" or "Name" or "ConnectionInfo" or "Mode"),
    "UI peer record has no secret properties");
Check(!JsonSerializer.Serialize(peer).Contains(binding.Credential) && !peer.ToString().Contains(binding.Credential),
    "UI serialization and display do not disclose credentials");
var rebound = store.Bind(peer, Connection("192.168.10.3"));
Check(AssistanceClient.ParseConnection(rebound.ConnectionInfo).Addresses.Single() == "192.168.10.3", "binding refreshes a saved pinned address");
string changedPin = "icontrol-help:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(connection with { Pin = new string('B', 64) }, RuntimeProtocol.JsonOptions));
Refuses(() => store.Bind(peer, changedPin), "binding cannot replace a trusted certificate pin");
store.Forget(peer);
restored = reopened.Prepare(connection);
Check(store.List().Count == 0 && restored.Credential != binding.Credential && restored.ClientId == binding.ClientId,
    "forget removes peer secret while preserving this client identity");
store.Confirm(binding, "control", "stale reply");
Check(store.List().Count == 0, "late approval cannot restore a forgotten credential");
store.DiscardPending(restored);
Refuses(() => store.Resolve(peer.Id), "pending or forgotten peer cannot be used for trusted join");
string corruptPath = Path.Combine(root, "corrupt.json");
File.WriteAllText(corruptPath, "{\"version\":1,\"protectedData\":\"invalid\"}");
Refuses(() => new AssistancePeerStore(corruptPath).Prepare(connection), "corrupt store is refused instead of silently replacing identity");
Check(File.ReadAllText(corruptPath).Contains("invalid"), "corrupt store remains available for recovery");

// The fake manifest enables protocol checks without launching a receiver or touching an installation.
string install = Path.Combine(root, "install");
string receiver = Path.Combine(install, "components", "assistance-receiver");
Directory.CreateDirectory(receiver);
byte[] executable = Encoding.ASCII.GetBytes("isolated fixture; never execute");
File.WriteAllBytes(Path.Combine(receiver, "Moonlight.exe"), executable);
Directory.CreateDirectory(Path.Combine(receiver, "tls"));
File.WriteAllBytes(Path.Combine(receiver, "tls", "qopensslbackend.dll"), executable);
File.WriteAllBytes(Path.Combine(receiver, "source.tar.gz"), executable);
File.WriteAllBytes(Path.Combine(receiver, "source.tar.xz"), executable);
File.WriteAllText(Path.Combine(receiver, "receiver-files.json"), JsonSerializer.Serialize(new {
    files = new[] { new { path = "Moonlight.exe", sha256 = Convert.ToHexString(SHA256.HashData(executable)) },
        new { path = Path.Combine("tls", "qopensslbackend.dll"), sha256 = Convert.ToHexString(SHA256.HashData(executable)) },
        new { path = "source.tar.gz", sha256 = Convert.ToHexString(SHA256.HashData(executable)) },
        new { path = "source.tar.xz", sha256 = Convert.ToHexString(SHA256.HashData(executable)) } }
}));
string sessionCopy = Path.Combine(root, "session-copy");
Directory.CreateDirectory(sessionCopy);
AssistanceClient.CopyRuntime(receiver, sessionCopy);
Check(File.Exists(Path.Combine(sessionCopy, "Moonlight.exe")) && File.Exists(Path.Combine(sessionCopy, "tls", "qopensslbackend.dll")) &&
    !Directory.GetFiles(sessionCopy, "*.tar.*").Any(), "session copies retain runtime files and omit source archives");
File.WriteAllText(Path.Combine(receiver, "source.tar.xz"), "changed source");
await using (var tampered = new AssistanceClient(install, new AssistancePeerStore(Path.Combine(root, "tampered.json"))))
    await RefusesAsync(() => tampered.JoinAsync(Connection(), "ABCD-EFGH", default),
        "streamed manifest checks still reject modified archives before connecting");
File.WriteAllBytes(Path.Combine(receiver, "source.tar.xz"), executable);
string clientStorePath = Path.Combine(root, "client.json");
var clientStore = new AssistancePeerStore(clientStorePath);
var server = new FakeServer();
var client = new AssistanceClient(install, clientStore, server.Handler);
var pending = await client.JoinAsync(Connection(), "ABCD-EFGH", default);
Check(pending.State == "pending" && clientStore.List().Count == 0, "code join sends identity without granting local trust");
Check(server.LastCode == "ABCD-EFGH" && server.Credential is { Length: 64 }, "code join supplies clientId and credential to host");
server.Trust = true;
server.State = "active";
await client.PollAsync(default);
peer = clientStore.List().Single();
Check(peer.Name == "Host fixture" && peer.Mode == "control", "poll persists only an explicit host trust acknowledgement");
await client.EndAsync();
Check(server.Ends == 1 && clientStore.List().Count == 1, "ending session preserves an acknowledged grant");
string originalCredential = server.Credential!;
client = new AssistanceClient(install, new AssistancePeerStore(clientStorePath), server.Handler);
var active = await client.JoinTrustedAsync(peer.Id, default);
Check(active.State == "active" && server.TrustedJoins == 1 && server.Challenges == 1, "later client instance joins with a fresh server challenge");
Check(server.Credential == originalCredential && server.LastTrustedCodeAbsent, "trusted join uses stored credential and sends no code");
Check(server.LastMode == "control" && server.Authenticated, "client HMAC matches the actual backend authentication helper");
await client.EndAsync();
await RefusesAsync(() => client.JoinTrustedAsync(peer.Id, "view", default), "client refuses a mode outside the stored grant");
server.Reject = true;
await RefusesAsync(() => client.JoinTrustedAsync(peer.Id, default), "revoked or denied trusted authentication fails without code fallback");
Check(server.CodeJoins == 1, "failed trusted join does not retry as an ordinary join");
server.Reject = false;
server.Trust = false;
server.State = "active";
string untrustedPath = Path.Combine(root, "temporary.json");
var temporaryStore = new AssistancePeerStore(untrustedPath);
await using (var temporary = new AssistanceClient(install, temporaryStore, server.Handler))
{
    await temporary.JoinAsync(Connection(), "ABCD-EFGH", default);
    Check(temporaryStore.List().Count == 0, "ordinary active session without trust flag is not saved");
}
Check(new AssistancePeerStore(untrustedPath).List().Count == 0, "ordinary session cleanup keeps saved list empty");
string deniedPath = Path.Combine(root, "denied.json");
server.Reject = true;
await using (var denied = new AssistanceClient(install, new AssistancePeerStore(deniedPath), server.Handler))
    await RefusesAsync(() => denied.JoinAsync(Connection(), "ABCD-EFGH", default), "rejected code request is surfaced");
var deniedStore = new AssistancePeerStore(deniedPath);
Check(deniedStore.List().Count == 0, "refusal leaves no saved peer");
using (var json = JsonDocument.Parse(File.ReadAllText(deniedPath)))
{
    byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(json.RootElement.GetProperty("protectedData").GetString()!),
        Encoding.UTF8.GetBytes("StreamHub.Assistance.Peers.v1"), DataProtectionScope.CurrentUser);
    try
    {
        using var data = JsonDocument.Parse(plain);
        Check(data.RootElement.GetProperty("peers").GetArrayLength() == 0, "refusal removes the pending credential from encrypted storage");
    }
    finally { CryptographicOperations.ZeroMemory(plain); }
}
server.Reject = false;
server.Trust = true;
server.WrongSessionOnPoll = true;
server.State = "pending";
var mismatchedStore = new AssistancePeerStore(Path.Combine(root, "mismatched.json"));
await using (var mismatched = new AssistanceClient(install, mismatchedStore, server.Handler))
{
    await mismatched.JoinAsync(Connection(), "ABCD-EFGH", default);
    server.State = "active";
    await RefusesAsync(() => mismatched.PollAsync(default), "trust echo from another session is rejected");
    Check(mismatchedStore.List().Count == 0, "unrelated session cannot save a grant");
}
server.WrongSessionOnPoll = false;
server.State = "pending";
server.FailJoinTransport = true;
var ambiguousStore = new AssistancePeerStore(Path.Combine(root, "ambiguous.json"));
int joinsBefore = server.CodeJoins;
await using (var ambiguous = new AssistanceClient(install, ambiguousStore, server.Handler))
    await RefusesAsync(() => ambiguous.JoinAsync(Connection("192.168.10.2", "192.168.10.3"), "ABCD-EFGH", default),
        "ambiguous redemption failure is returned");
Check(server.CodeJoins == joinsBefore + 1 && ambiguousStore.List().Count == 0, "ambiguous redemption is not replayed on alternate address");
server.FailJoinTransport = false;
server.FailProbeAddress = "192.168.10.2";
await using (var failover = new AssistanceClient(install, new AssistancePeerStore(Path.Combine(root, "failover.json")), server.Handler))
{
    await failover.JoinAsync(Connection("192.168.10.2", "192.168.10.3"), "ABCD-EFGH", default);
    Check(server.LastAddress == "192.168.10.3", "unconsumed pinned probe can fail over to a reachable address");
}
server.FailProbeAddress = null;
using var key = RSA.Create(2048);
using var cert = new CertificateRequest("CN=pin-check", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
var pinned = connection with { Pin = Convert.ToHexString(SHA256.HashData(cert.RawData)) };
using var handler = AssistanceClient.CreateHandler(pinned, pinned.Addresses[0]);
Check(!handler.AllowAutoRedirect && !handler.UseProxy && !handler.UseCookies, "control channel disables redirects, proxy and cookies");
Check(handler.ServerCertificateCustomValidationCallback!(new HttpRequestMessage(), cert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors),
    "exact pinned certificate is accepted");
using var otherCert = new CertificateRequest("CN=wrong-pin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
Check(!handler.ServerCertificateCustomValidationCallback!(new HttpRequestMessage(), otherCert, null, System.Net.Security.SslPolicyErrors.None),
    "different certificate is refused even without TLS policy errors");
Check(!Directory.GetFiles(root, "*.new", SearchOption.AllDirectories).Any(), "atomic store updates leave no temporary secret files");
Console.WriteLine($"{passed} isolated client checks passed. No installed service or receiver session was used.");

sealed class FakeServer
{
    public string State = "pending";
    public bool Trust, Reject, Authenticated, LastTrustedCodeAbsent, WrongSessionOnPoll, FailJoinTransport;
    public string? ClientId, Credential, LastCode, LastMode, LastAddress, FailProbeAddress;
    public int Ends, Challenges, TrustedJoins, CodeJoins;
    private string _session = Guid.NewGuid().ToString("D");
    private AssistanceTrustRegistry _registry = new(_ => { });
    public HttpMessageHandler Handler(AssistanceConnection connection, string address) => new DelegateHandler(async (request, token) => {
        LastAddress = address;
        string operation = request.RequestUri!.Segments.Last();
        if (operation == "probe")
        {
            if (address == FailProbeAddress) throw new HttpRequestException("Fixture unavailable address.");
            return new(HttpStatusCode.BadRequest);
        }
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var body = document.RootElement;
        string Text(string property) => body.GetProperty(property).GetString()!;
        if (operation == "end") { Ends++; return Ok(new { state = "ended" }); }
        if (operation == "join")
        {
            CodeJoins++;
            _session = Guid.NewGuid().ToString("D");
            ClientId = Text("clientId"); Credential = Text("credential"); LastCode = Text("code");
            if (FailJoinTransport) throw new HttpRequestException("Ambiguous fixture response.");
            _registry = new(_ => { });
            _registry.GrantDevice(ClientId, "fixture", AssistanceTrustRegistry.HashCredential(Credential), false, true);
        }
        if (Reject) return new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { success = false, message = "Fixture refused." }) };
        if (operation == "trusted-challenge")
        {
            Challenges++;
            return Ok(_registry.IssueChallenge(Text("clientId"), Text("mode")));
        }
        if (operation == "trusted-join")
        {
            TrustedJoins++;
            LastTrustedCodeAbsent = !body.TryGetProperty("code", out _);
            LastMode = Text("mode");
            _registry.Authenticate(Text("clientId"), Text("credential"), Text("challengeId"), Text("authProof"),
                LastMode, Text("proof"), Text("certificate"));
            Authenticated = true;
            _session = Guid.NewGuid().ToString("D");
            State = "active";
        }
        return Ok(new AssistanceReply(operation == "poll" && WrongSessionOnPoll ? Guid.NewGuid().ToString("D") : _session,
            State, "ABCD-EFGH", State == "pending" ? null : "control", Trusted: Trust,
            TrustedMode: Trust ? "control" : null, HostName: "Host fixture"));
    });
    private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) {
        Content = JsonContent.Create(new { success = true, result = value }, options: RuntimeProtocol.JsonOptions)
    };
}
sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
