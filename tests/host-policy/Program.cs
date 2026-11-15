using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;
using StreamHub.Service;

// Actual production API and service commands, synthetic in-memory transport/input/secret.
// No sockets, service connection, Windows input, lock operation or real password is used.
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
const string secret = "synthetic-host-policy-secret-never-log";
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
var neverCreated = Path.Combine(AppContext.BaseDirectory, "policy-fixture-not-created");
var runtime = new HostRuntime(neverCreated, neverCreated, true);
void Set(string field, object? value) => typeof(HostRuntime).GetField(field, flags)!.SetValue(runtime, value);
T Get<T>(string field) => (T)typeof(HostRuntime).GetField(field, flags)!.GetValue(runtime)!;
Set("_controlDevices", new ControlDeviceRegistry()); // Same explicit legacy migration policy as the synthetic API.
async Task<RuntimeResponse> Send(RuntimeRequest request) => await (Task<RuntimeResponse>)typeof(HostRuntime)
    .GetMethod("HandleAsync", flags)!.Invoke(runtime, [request, CancellationToken.None])!;
var lease = Get<LiveSessionState>("_remoteSession");
void Ready()
{
    lease = new LiveSessionState(); Set("_remoteSession", lease);
    lease.Prepare("fixture-lease", "fixture-client", DateTimeOffset.UtcNow);
    lease.Heartbeat("fixture-lease", "fixture-client", true, DateTimeOffset.UtcNow);
    Get<HashSet<string>>("_loginInputUsed").Clear(); Set("_desktopInputReadAt", default(DateTimeOffset));
}
Ready(); Set("_deviceId", "fixture-computer"); Set("_lockOnExitEnabled", true);
var secrets = new FakeSecrets(secret); Set("_hostLoginSecrets", secrets);
int fills = 0, observations = 0; bool focused = true, locked = true, inputFailure = false, expireDuringProbe = false;
runtime.DesktopInputRequest = (password, submit, token) =>
{
    if (password is null) { observations++; if (expireDuringProbe) lease.Reset(); }
    else { fills++; Check(password == secret, "only the computer-owned password reaches the input helper"); if (inputFailure) throw new IOException(secret); }
    return Task.FromResult((new DesktopInputState(true, locked, focused), new LoginInputResult(password is null ? "status" : "filled", password is not null, submit)));
};
RuntimeRequest Desktop(string action, string client = "fixture-client") => new("remote-desktop", "remote",
    LeaseId: "fixture-lease", ClientId: client, ClipboardAction: action);
foreach (bool legacy in new[] { false, true })
{
    var configured = await Send(Desktop("configure") with { LockOnExit = legacy });
    Check(configured.Success && Get<bool>("_lockOnExitEnabled"), "legacy Android lock flag cannot disable host exit-lock policy");
}
Set("_lockOnExitEnabled", false);
Check((await Send(Desktop("configure") with { LockOnExit = true })).Success && !Get<bool>("_lockOnExitEnabled"), "legacy flag cannot enable host exit-lock policy");
foreach (bool enabled in new[] { false, true })
{
    Set("_privacyScreenEnabled", enabled);
    var denied = await Send(new("remote-privacy", "remote", LeaseId: "fixture-lease", ClientId: "fixture-client", PrivacyEnabled: !enabled));
    Check(!denied.Success && Get<bool>("_privacyScreenEnabled") == enabled, "remote privacy mutation cannot override host policy");
}
Set("_privacyScreenEnabled", false);
var deniedSecret = await Send(Desktop("login-fill") with { LoginSecret = secret });
Check(!deniedSecret.Success && fills == 0 && secrets.Reads == 0 && !deniedSecret.Message.Contains(secret), "service rejects remote password without reading host secret or logging supplied content");
Set("_automaticLoginEnabled", false);
Check((await Send(Desktop("status"))).Success && fills == 0 && secrets.Reads == 0, "disabled host auto-login leaves Windows input and password untouched");
Set("_automaticLoginEnabled", true); focused = false; Set("_desktopInputReadAt", default(DateTimeOffset));
await Send(Desktop("status")); Check(fills == 0 && secrets.Reads == 0, "unfocused Windows password field blocks secret access");
focused = true; locked = false; Set("_desktopInputReadAt", default(DateTimeOffset));
await Send(Desktop("status")); Check(fills == 0 && secrets.Reads == 0, "unlocked desktop blocks secret access");
locked = true; Set("_desktopInputReadAt", default(DateTimeOffset));
var wrongLease = await Send(Desktop("status", "foreign-client"));
Check(!wrongLease.Success && fills == 0 && secrets.Reads == 0, "foreign live-lease identity is denied before the input helper");
expireDuringProbe = true; var expired = await Send(Desktop("status")); expireDuringProbe = false;
Check(!expired.Success && fills == 0 && secrets.Reads == 0, "lease ending during desktop observation blocks password access");
Ready(); var filled = await Send(Desktop("status"));
await Send(Desktop("status")); await Send(Desktop("login-fill"));
Check(filled.Success && fills == 1 && secrets.Reads == 1, "status and fill requests share one attempt for the live lease");
Check(!filled.PayloadJson!.Contains(secret) && !filled.Message.Contains(secret), "desktop response contains state but never the stored password");
Ready(); inputFailure = true; int before = fills;
var uncertain = await Send(Desktop("status")); await Send(Desktop("status")); inputFailure = false;
Check(fills == before + 1 && !uncertain.PayloadJson!.Contains(secret), "uncertain input failure stays consumed and suppresses credential-bearing exception text");
Check(!Directory.Exists(neverCreated), "production boundary checks created no state/log directory");
int locks = 0; runtime.LockDesktopRequest = _ => { locks++; return Task.CompletedTask; };
foreach (bool hostLock in new[] { false, true })
foreach (bool clientLock in new[] { false, true })
{
    Ready(); Set("_lockOnExitEnabled",hostLock); int priorLocks = locks;
    await Send(new("remote-release","remote",LeaseId:"fixture-lease",ClientId:"fixture-client",ExitReason:"user",LockOnExit:clientLock));
    Check(locks == priorLocks + (hostLock ? 1 : 0), "actual end-session lock decision follows the computer policy regardless of old Android flag");
}
Ready(); int internalLocks=locks;
await Send(new("remote-release","remote",LeaseId:"fixture-lease",ClientId:"fixture-client",ExitReason:"internal",LockOnExit:true));
Check(locks==internalLocks,"rotation and resolution reconnect cannot trigger host exit-lock policy");

byte[] bearer = RandomNumberGenerator.GetBytes(32);
var forwarded = new List<RuntimeRequest>();
var api = new ControlApiServer(new RuntimeClient(async (request, token) => { forwarded.Add(request); return await Send(request); }),
    (_, _, _) => throw new Exception("unexpected display operation"), new ControlApiIdentity([], []), X509KeyStorageFlags.EphemeralKeySet);
typeof(ControlApiServer).GetField("_token", flags)!.SetValue(api, bearer);
async Task<(int Status, string Body)> Http(string path, object? body = null)
{
    var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.23.2");
    context.Request.Path = path; context.Request.Method = body is null ? "GET" : "POST";
    context.Request.Headers.Authorization = "Bearer " + Convert.ToHexString(bearer);
    if (body is not null) { var bytes = JsonSerializer.SerializeToUtf8Bytes(body); context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json"; }
    context.Response.Body = new MemoryStream();
    await (Task)typeof(ControlApiServer).GetMethod("HandleAsync", flags)!.Invoke(api, [context])!;
    return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
}
Ready(); Set("_automaticLoginEnabled", false);
foreach (var payload in new object[] {
    new { leaseId="fixture-lease",clientId="fixture-client",action="login-fill",password=secret },
    new { leaseId="fixture-lease",clientId="fixture-client",action="status",password=secret },
    new { leaseId="fixture-lease",clientId="fixture-client",action="configure",submit=true },
    new { leaseId="fixture-lease",clientId="fixture-client",action="lock",submit=true } })
{
    forwarded.Clear(); var answer = await Http("/v1/session/desktop", payload);
    Check(answer.Status == 403 && forwarded.Count == 0 && !answer.Body.Contains(secret), "HTTP refuses remote password/submit fields before forwarding any runtime command");
}
forwarded.Clear(); await Http("/v1/session/desktop", new { leaseId="fixture-lease",clientId="fixture-client",action="configure",enabled=true,password=(string?)null,submit=false });
Check(forwarded.Count == 1 && forwarded[0].LoginSecret is null && forwarded[0].LoginSubmit is null && forwarded[0].LockOnExit is null,
    "explicit null password/false submit and legacy flags are never forwarded as host policy");
Set("_lockOnExitEnabled", true); forwarded.Clear();
await Http("/v1/session/end", new {leaseId="stale-lease",clientId="fixture-client",mode="remote",exitReason="user",lockOnExit=false});
Check(forwarded.Count == 1 && forwarded[0].LockOnExit is null && Get<bool>("_lockOnExitEnabled"), "legacy end payload cannot overwrite computer lock policy");
forwarded.Clear(); Check((await Http("/v1/session/privacy", new {leaseId="fixture-lease",clientId="fixture-client",enabled=true})).Status == 403 && forwarded.Count == 0,
    "HTTP cannot enable persistent privacy via Bearer credential");
Set("_privacyScreenEnabled", true);
var recovery = await Http("/v1/session/privacy", new {leaseId="fixture-lease",clientId="fixture-client",enabled=false});
Check(recovery.Status == 200 && Get<bool>("_privacyScreenEnabled") && Get<string>("_privacySuppressedLease") == "fixture-lease",
    "instant remote recovery restores only this lease and preserves persistent privacy setting");
var policy = await Http("/v1/policy");
Check(policy.Status == 200 && !policy.Body.Contains(secret) && !policy.Body.Contains("loginSecret", StringComparison.OrdinalIgnoreCase), "read-only policy endpoint contains no password");
Check((await Http("/v1/policy", new {lockOnExit=false})).Status == 404 && Get<bool>("_lockOnExitEnabled"), "Bearer policy writes have no route");
foreach (bool sameUser in new[] { true, false })
    Check(DesktopInputPolicy.CanFill(sameUser, true, true, true, true, true, "密码") == sameUser, "Windows input helper independently gates owner identity");
Check(!DesktopInputPolicy.CanFill(true,true,false,true,true,true,"Password") && !DesktopInputPolicy.CanFill(true,true,true,false,true,true,"Password"), "normal desktop and untrusted LogonUI cannot receive automatic password");
Check(!Directory.Exists(neverCreated), "all HTTP/service checks used no installed or persisted state");
if (!System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
{
    var localStore = new HostLoginSecretStore(neverCreated, System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
    bool rejected = false; try { localStore.Save(secret); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && !Directory.Exists(neverCreated), "an owner/admin process cannot save a machine-DPAPI credential outside the service boundary");
}
Console.WriteLine($"{checks} host policy boundary checks passed; no installation, desktop action, real credential or service access.");

sealed class FakeSecrets(string value) : IHostLoginSecretStore
{
    public bool Configured => true;
    public int Reads { get; private set; }
    public string Read() { Reads++; return value; }
    public void Save(string password) => throw new Exception("not used");
    public void Clear() => throw new Exception("not used");
}
