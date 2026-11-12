using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MediaTrustChecks;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;
using StreamHub.Service;

var artifacts = Path.GetFullPath(args.Single());
if (!System.Text.RegularExpressions.Regex.IsMatch(artifacts,
    @"^D:\\Deepseek\\build\\stage\d+-tests\\media-trust(?:\\|$)",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    throw new Exception("Use an isolated stageN-tests media-trust directory.");
Directory.CreateDirectory(artifacts);
using var key = RSA.Create(2048);
X509Certificate2 Certificate(string name) => new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
using var client = Certificate("client"); using var other = Certificate("other");
using var remote = Certificate("remote"); using var tablet = Certificate("tablet");
var encoded = Convert.ToBase64String(client.RawData);
var clientPem = client.ExportCertificatePem(); var otherPem = other.ExportCertificatePem();
var remotePem = remote.ExportCertificatePem(); var tabletPem = tablet.ExportCertificatePem();
const string a = "11111111-1111-4111-8111-111111111111", b = "22222222-2222-4222-8222-222222222222",
    c = "33333333-3333-4333-8333-333333333333", d = "44444444-4444-4444-8444-444444444444";
var checks = 0;
void Check(bool value, string reason)
{ if (!value) throw new Exception(reason); checks++; Console.WriteLine("PASS " + reason); }
async Task<MediaIdentityException> Reject(int status, Func<Task> run, string reason)
{
    try { await run(); throw new Exception("Expected rejection: " + reason); }
    catch (MediaIdentityException error)
    { Check(error.Status == status && !error.Message.Contains("fixture-secret") && !error.Message.Contains("BEGIN CERTIFICATE"), reason); return error; }
}
void Invalid(string mode, string certificate, string reason)
{
    try { MediaIdentityPolicy.ParseCertificate(mode, certificate); throw new Exception("Expected invalid certificate"); }
    catch (ArgumentException) { Check(true, reason); }
}
Fixture New(params FixtureClient[] rows)
{
    var f = new Fixture(artifacts, remotePem, tabletPem);
    f.Clients["remote"].AddRange(rows); f.Save("remote"); return f;
}
FixtureClient Row(string id, string? pem = null, bool enabled = true) => new(id, pem ?? clientPem, enabled);

Check(MediaIdentityPolicy.ParseCertificate("remote", encoded).AsSpan().SequenceEqual(client.RawData), "single public DER is accepted without a private key");
Invalid("../tablet", encoded, "profile traversal is rejected before file access");
Invalid("remote", encoded + "\n", "noncanonical Base64 is rejected");
Invalid("remote", clientPem, "PEM cannot enter the public DER request contract");
Invalid("remote", Convert.ToBase64String(client.Export(X509ContentType.Pfx)), "PFX with private key is rejected");
Invalid("remote", Convert.ToBase64String(client.RawData.Concat(new byte[] { 0 }).ToArray()), "trailing DER content is rejected");
Invalid("remote", Convert.ToBase64String(new byte[4097]), "over-limit certificate is rejected before backend work");
using (var f = New(Row(a)))
{
    var result = await f.Inspect(encoded);
    Check(result.Paired && result.ServerCertificate == Convert.ToBase64String(remote.RawData), "one exact enabled identity returns the selected profile public server certificate");
    Check(f.Deleted.Count == 0 && f.GuardCalls == 0, "ordinary identity recovery never unpairs or changes engine state");
}
using (var f = New(Row(a, otherPem)))
{
    var result = await f.Inspect(encoded);
    Check(!result.Paired && result.ServerCertificate is null && f.Deleted.Count == 0, "same visible name with a different certificate stays unpaired and untouched");
}
using (var f = New(Row(c), Row(a), Row(b), Row(d, otherPem)))
{
    var result = await f.Inspect(encoded);
    Check(result.Paired && f.Deleted.SequenceEqual(new[] { b, c }), "three exact enabled duplicates are reduced to the deterministic original keeper");
    Check(f.Clients["remote"].Select(x => x.Uuid).Order().SequenceEqual(new[] { a, d }), "deduplication retains the keeper and every unrelated same-name identity");
    Check(f.GuardCalls == 3 && f.Requests.Where(x => x.Method == "POST").All(x => x.Path == "/api/clients/unpair"), "idle guard is checked before each exact UUID deletion and no broad operation is called");
    Check(f.Clients["tablet"].Count == 0 && f.Requests.All(x => x.Profile == "remote"), "remote repair never queries or mutates the tablet profile");
}
using (var f = New(Row(a), Row(b, enabled: false)))
{
    await Reject(403, async () => { await f.Inspect(encoded); }, "an exact disabled duplicate blocks recovery without re-enabling authorization");
    Check(f.Deleted.Count == 0 && f.Requests.Count == 0, "disabled stored identity is rejected before any admin operation");
}
using (var f = New(Row(a), Row(d, otherPem, false)))
    Check((await f.Inspect(encoded)).Paired && f.Deleted.Count == 0, "an unrelated disabled identity does not revoke the requested certificate");
using (var f = New(Row(a), Row(b)))
{
    f.CanRepair = false;
    await Reject(409, async () => { await f.Inspect(encoded); }, "busy or unready engine refuses duplicate repair");
    Check(f.Deleted.Count == 0, "busy engine preserves all original records");
}
using (var f = New(Row(a), Row(b)))
{
    f.GuardResult = call => call == 1;
    await Reject(409, async () => { await f.Inspect(encoded); }, "engine becoming busy between discovery and deletion is detected");
    Check(f.Deleted.Count == 0, "second idle guard prevents mutation after state changes");
}
using (var f = New(Row(a), Row(b)))
{
    f.BeforeList = (fixture, mode) => { if (fixture.ListCalls == 1) { fixture.Clients[mode].Add(Row(d, otherPem)); fixture.Save(mode); } };
    await Reject(409, async () => { await f.Inspect(encoded); }, "stored projection changing during API cross-check aborts repair");
    Check(f.Deleted.Count == 0, "inconsistent state snapshots never authorize deletion");
}
using (var f = New(Row(a)))
{
    f.ApiOverride = "{\"status\":true,\"named_certs\":[]}";
    await Reject(409, async () => { await f.Inspect(encoded); }, "API and stored UUID sets must agree exactly");
}
using (var f = New(Row(a)))
{
    f.ApiOverride = JsonSerializer.Serialize(new { status = true, named_certs = new[] { new { name = "same", uuid = a, enabled = false } } });
    await Reject(403, async () => { await f.Inspect(encoded); }, "API revocation is not hidden by a still-enabled disk snapshot");
}
using (var f = New(Row(a), Row(b)))
{
    f.NoOpDelete = true;
    var error = await Reject(409, async () => { await f.Inspect(encoded); }, "successful delete response without matching readback is rejected");
    Check(error.Message.Contains("可能已部分清理") && !error.Message.Contains("未更改"), "post-send failure does not claim records were unchanged");
}
using (var f = New(Row(a), Row(b)))
{
    f.FailDelete = true;
    await Reject(503, async () => { await f.Inspect(encoded); }, "failed exact deletion is never reported as recovered");
    Check(f.Clients["remote"].Count == 2, "backend refusal leaves the original authorization rows");
}
using (var f = New(Row(a), Row(b), Row(c)))
{
    f.AfterDelete = (fixture, _, _) => fixture.CanRepair = false;
    var error = await Reject(409, async () => { await f.Inspect(encoded); }, "busy state after one deletion stops a multi-record repair");
    Check(f.Deleted.Count == 1 && error.Message.Contains("可能已部分清理"), "partial repair reports uncertainty and preserves the remaining keeper");
    f.CanRepair = true; f.AfterDelete = null;
    Check((await f.Inspect(encoded)).Paired && f.Clients["remote"].Count == 1, "a new inspection safely resumes a partially completed deduplication");
}
using (var f = New(Row(a), Row(b)))
{
    f.MissingCsrf = true;
    await Reject(503, async () => { await f.Inspect(encoded); }, "missing CSRF token blocks the mutation");
    Check(f.Deleted.Count == 0, "invalid CSRF leaves all original records intact");
}
foreach (var malformed in new[] { "{}", "{\"status\":true,\"status\":true,\"named_certs\":[]}", "not-json", "{\"status\":true,\"named_certs\":[{\"name\":\"n\",\"uuid\":\"bad\",\"enabled\":true}]}" })
    using (var f = New()) { f.ApiOverride = malformed; await Reject(503, async () => { await f.Inspect(encoded); }, "malformed or ambiguous admin list is rejected"); }
using (var f = New())
{
    f.ApiStatus = HttpStatusCode.InternalServerError;
    await Reject(503, async () => { await f.Inspect(encoded); }, "non-success admin HTTP status cannot authorize media recovery");
}
using (var f = New(new FixtureClient(a, clientPem, MissingEnabled: true)))
    Check((await f.Inspect(encoded)).Paired, "legacy missing enabled field follows upstream true default");
using (var f = New(new FixtureClient(a, clientPem, StoredEnabled: true)))
    Check((await f.Inspect(encoded)).Paired, "JSON boolean enabled is accepted alongside upstream string representation");
using (var f = New(new FixtureClient(a, clientPem, StoredEnabled: "yes")))
    await Reject(503, async () => { await f.Inspect(encoded); }, "ambiguous enabled metadata is rejected");
using (var f = New(Row(a), Row(a)))
    await Reject(503, async () => { await f.Inspect(encoded); }, "duplicate stored UUIDs cannot be used for exact deletion");
using (var f = New())
{
    f.StateOverrides["remote"] = "{\"root\":{\"uniqueid\":\"test\",\"named_devices\":\"\"}}"; f.Save("remote");
    Check(!(await f.Inspect(encoded)).Paired, "upstream empty-string client list is recognized as unpaired");
    f.StateOverrides["remote"] = new string('x', 1024 * 1024 + 1); f.Save("remote");
    await Reject(503, async () => { await f.Inspect(encoded); }, "oversized stored state is rejected without raw data disclosure");
}
using (var f = New())
{
    f.Clients["tablet"].Add(Row(a)); f.Save("tablet");
    var result = await f.Inspect(encoded, "tablet");
    Check(result.Paired && result.ServerCertificate == Convert.ToBase64String(tablet.RawData) && f.Requests.All(x => x.Profile == "tablet"), "tablet identity recovery returns only its own selected public certificate");
}
using (var f = New(Row(a)))
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await f.Inspect(encoded, token: cancellation.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(f.Deleted.Count == 0, "caller cancellation propagates without modifying records"); }
}
using (var f = New(Row(a)))
{
    f.BeforeRequest = (_, token) => Task.Delay(Timeout.Infinite, token);
    var started = Stopwatch.StartNew();
    await Reject(504, async () => { await f.Inspect(encoded); }, "cooperative blocked backend is stopped by the media-specific total budget");
    Check(started.Elapsed >= TimeSpan.FromSeconds(2) && started.Elapsed < TimeSpan.FromSeconds(5), "media preflight budget expires before ordinary status and heartbeat deadlines");
}

// Exercise the production bound-device HTTP handler in memory; no listener starts.
var ipc = new RuntimeClient();
await using var api = new ControlApiServer(ipc, (_, _, _) => throw new Exception("Unexpected display access"), null!, X509KeyStorageFlags.DefaultKeySet);
var bearer = RandomNumberGenerator.GetBytes(32);
typeof(ControlApiServer).GetField("_token", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, bearer);
var handler = typeof(ControlApiServer).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
async Task<(int Status, string Body)> Http(string certificate, bool authorized = true)
    => await HttpJson("/v1/media-identity", new { mode = "remote", clientCertificate = certificate }, authorized);
async Task<(int Status, string Body)> HttpJson(string path, object body, bool authorized = true, string address = "192.0.2.38")
{
    var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(address);
    context.Request.Method = "POST"; context.Request.Path = path;
    context.Request.ContentType = "application/json";
    if (authorized) context.Request.Headers.Authorization = "Bearer " + Convert.ToHexString(bearer);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
    context.Request.ContentLength = bytes.Length; context.Request.Body = new MemoryStream(bytes);
    await using var output = new MemoryStream(); context.Response.Body = output;
    await (Task)handler.Invoke(api, [context])!;
    return (context.Response.StatusCode, Encoding.UTF8.GetString(output.ToArray()));
}
Check((await Http(encoded, false)).Status == 401 && ipc.Calls == 0, "HTTP identity endpoint rejects missing binding bearer before IPC");
var bindingRegistry=new LocalBindingRegistry(new string('A',64));
typeof(ControlApiServer).GetField("_localBinding",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,bindingRegistry);
var window=bindingRegistry.Start();var localRequest=new {code=window.Code,clientId="fixture-client",name="中文平板",nonce=new string('B',64)};
foreach(var source in new[]{"8.8.8.8","100.89.1.2","127.0.0.1"})
 Check((await HttpJson("/v1/binding/request",localRequest,false,source)).Status==403,"short-code enrollment excludes public/Tailscale/loopback source");
var begin=await HttpJson("/v1/binding/request",localRequest,false,"192.168.1.44");
using(var result=JsonDocument.Parse(begin.Body))
{
 Check(begin.Status==200&&result.RootElement.GetProperty("result").GetProperty("state").GetString()=="pending"&&!begin.Body.Contains("fixture-invitation"),"LAN request reaches owner approval without an existing bearer");
 var id=result.RootElement.GetProperty("result").GetProperty("requestId").GetString()!;
 Check((await HttpJson("/v1/binding/poll",new {requestId=id,nonce=new string('C',64)},false,"192.168.1.44")).Status==409,"another nonce cannot poll device confirmation");
 bindingRegistry.Decide(id,true);
 typeof(ControlApiServer).GetField("_invitation",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,"fixture-invitation");
 typeof(ControlApiServer).GetProperty("IsRunning")!.SetValue(api,true);
 var grant=await HttpJson("/v1/binding/poll",new {requestId=id,nonce=new string('B',64)},false,"192.168.1.44");
 Check(grant.Status==200&&grant.Body.Contains("fixture-invitation"),"exact approved local poll receives its invitation once");
 Check((await HttpJson("/v1/binding/poll",new {requestId=id,nonce=new string('B',64)},false,"192.168.1.44")).Status==409,"HTTP replay cannot retrieve credentials again");
}
Check((await Http("invalid")).Status == 400 && ipc.Calls == 0, "HTTP identity endpoint validates public certificate before IPC");
ipc.Dispatch = (request, _) => Task.FromResult(new RuntimeResponse(true, "ok", PayloadJson: JsonSerializer.Serialize(new MediaIdentityResult(true, Convert.ToBase64String(remote.RawData)), RuntimeProtocol.JsonOptions)));
var good = await Http(encoded);
Check(good.Status == 200 && good.Body.Contains("serverCertificate") && ipc.Calls == 1, "bound-device handler returns only the validated public media identity result");
foreach (var status in new[] { 403, 409, 503, 504 })
{
    ipc.Dispatch = (_, _) => Task.FromResult(new RuntimeResponse(false, "fixture rejection", ErrorStatus: status));
    Check((await Http(encoded)).Status == status, "HTTP endpoint preserves backend rejection status " + status);
}
ipc.Dispatch = (_, _) => Task.FromResult(new RuntimeResponse(true, "ok", PayloadJson: "{\"paired\":true,\"serverCertificate\":\"bad\"}"));
Check((await Http(encoded)).Status == 503, "malformed IPC certificate cannot be forwarded as trusted media identity");
var sessionStatus = new RuntimeStatus("fixture", "fixture", new("remote",true,true,1,48989,true,"ok",true,true,true),
    new("tablet",false,false,null,49089,false,"off"), new(false,false,null,"off"),"0.8.2",artifacts,DateTimeOffset.UtcNow,
    PrivacyScreenEnabled:true, PrivacyScreenActive:true);
ipc.Dispatch = (request, _) => Task.FromResult(request.Command == "remote-privacy" && request.LeaseId == "lease-owner" && request.ClientId == "client-owner"
    ? new RuntimeResponse(true,"privacy fixture",sessionStatus) : new RuntimeResponse(false,"not current lease",ErrorStatus:409));
var privacyBody = new {mode="remote",leaseId="lease-owner",clientId="client-owner",enabled=true};
Check((await HttpJson("/v1/session/privacy", privacyBody, false)).Status == 401, "privacy endpoint requires binding bearer");
Check((await HttpJson("/v1/session/privacy", new {mode="tablet",leaseId="lease-owner",clientId="client-owner",enabled=true})).Status==400,"secondary display cannot enable physical privacy overlay");
Check((await HttpJson("/v1/session/privacy", new {mode="remote",leaseId="lease-other",clientId="client-owner",enabled=true})).Status==409,"stale privacy lease rejection reaches HTTP caller");
var privacyResult=await HttpJson("/v1/session/privacy",privacyBody);
using(var json=JsonDocument.Parse(privacyResult.Body)) Check(privacyResult.Status==200 && json.RootElement.GetProperty("active").GetBoolean(),"privacy UI reads actual host activation state");
// Reconfiguration preserves a running lease but must re-authorize its exact owner before bypassing BUSY.
await using(var resizeApi=new ControlApiServer(ipc,(_,_,_)=>throw new Exception("Remote resize must not reconfigure VDD"),null!,X509KeyStorageFlags.DefaultKeySet,
    _=>Task.FromResult(new HostDisplayMode(2560,1600,90))))
{
    typeof(ControlApiServer).GetField("_token",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(resizeApi,bearer);
    var requests=new List<string>();
    var releasedLeases=new List<string?>();
    ipc.Dispatch=(request,_)=>{
        requests.Add(request.Command);
        if (request.Command=="remote-release") releasedLeases.Add(request.LeaseId);
        return Task.FromResult(request.Command is "remote-reconfigure" or "remote-release" && (request.LeaseId!="lease-owner" || request.ClientId!="client-owner")
            ?new RuntimeResponse(false,"not owner",ErrorStatus:409):new RuntimeResponse(true,"ok",sessionStatus));
    };
    async Task<(int Status,string Body)> Resize(string lease) {
        var context=new DefaultHttpContext(); context.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.69");
        context.Request.Method="POST";context.Request.Path="/v1/prepare";context.Request.ContentType="application/json";
        context.Request.Headers.Authorization="Bearer "+Convert.ToHexString(bearer);
        var body=JsonSerializer.SerializeToUtf8Bytes(new{mode="remote",leaseId=lease,clientId="client-owner",width=1280,height=800,refreshRate=60,streamSizeOverride=true});
        context.Request.ContentLength=body.Length;context.Request.Body=new MemoryStream(body);context.Response.Body=new MemoryStream();
        await (Task)handler.Invoke(resizeApi,[context])!;
        return(context.Response.StatusCode,Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }
    var rejectedResize = await Resize("lease-other");
    Check(rejectedResize.Status==409 && requests.SequenceEqual(new[]{"remote-reconfigure","remote-release"}) && releasedLeases.SequenceEqual(new[]{"lease-other"}),
        "different lease is rejected before prepare and cleanup cannot release the current owner's lease");
    requests.Clear();var resized=await Resize("lease-owner");
    using(var json=JsonDocument.Parse(resized.Body)) Check(resized.Status==200 && json.RootElement.GetProperty("width").GetInt32()==1280 && json.RootElement.GetProperty("nativeWidth").GetInt32()==2560,
        "immediate stream size is negotiated separately from physical desktop size");
    Check(!requests.Contains("stop") && !requests.Contains("restart"),"resolution reconfiguration preserves the healthy engine");
    var tabletStatus=sessionStatus with { Remote=sessionStatus.Remote with { Streaming=false },
        Tablet=sessionStatus.Tablet with { Running=true, Healthy=true } };
    ipc.Dispatch=(request,_)=>Task.FromResult(new RuntimeResponse(true,"ok",tabletStatus,
        request.Command=="tablet-prepare" ? "{\"width\":1080,\"height\":1920,\"refreshRate\":60,\"orientation\":90,\"degraded\":false,\"supportedModes\":[{\"width\":1080,\"height\":1920,\"refreshRate\":60,\"orientation\":90}]}" : null));
    var tabletContext=new DefaultHttpContext(); tabletContext.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.69");
    tabletContext.Request.Method="POST";tabletContext.Request.Path="/v1/prepare";tabletContext.Request.ContentType="application/json";
    tabletContext.Request.Headers.Authorization="Bearer "+Convert.ToHexString(bearer);
    var tabletBody=JsonSerializer.SerializeToUtf8Bytes(new{mode="tablet",leaseId="lease-owner",clientId="client-owner",width=1080,height=1920,refreshRate=60,orientation=90});
    tabletContext.Request.ContentLength=tabletBody.Length;tabletContext.Request.Body=new MemoryStream(tabletBody);tabletContext.Response.Body=new MemoryStream();
    await (Task)handler.Invoke(resizeApi,[tabletContext])!;
    using(var json=JsonDocument.Parse(((MemoryStream)tabletContext.Response.Body).ToArray()))
        Check(tabletContext.Response.StatusCode==200 && json.RootElement.GetProperty("supportedModes")[0].GetProperty("width").GetInt32()==1080,
            "secondary-display negotiation forwards actual driver modes to the resolution menu");
}
// Generated tablet profiles must leave display ownership to the product helper.
using (var f = New(Row(a)))
{
    var profile = f.Profiles;
    var config = profile.ConfigFor("tablet");
    var state = await File.ReadAllBytesAsync(Path.Combine(profile.DirectoryFor("tablet"), "state.json"));
    var certificate = await File.ReadAllBytesAsync(profile.CertificateFor("tablet"));
    await File.WriteAllTextAsync(config, "sunshine_name = 我控制 · 平板副屏\ndd_configuration_option = ensure_active\ndd_config_revert_on_disconnect = enabled\ndd_manual_resolution = 2064x3096\ndd_manual_refresh_rate = 90\ncustom_option = keep\n");
    await profile.EnsureAsync("tablet", default);
    var migrated = await File.ReadAllTextAsync(config);
    Check(migrated.Contains("dd_configuration_option = disabled") && migrated.Contains("dd_config_revert_on_disconnect = disabled"), "existing generated tablet profile stops competing for display ownership");
    Check(migrated.Contains("dd_manual_resolution = 2064x3096") && migrated.Contains("dd_manual_refresh_rate = 90") && migrated.Contains("custom_option = keep"), "display ownership migration preserves negotiated mode and unrelated preferences");
    await profile.EnsureAsync("tablet", default);
    Check(migrated == await File.ReadAllTextAsync(config), "display ownership migration is idempotent");
    await profile.ConfigureTabletAsync(90, true, null, default, 1600, 2400, 60);
    var configured = await File.ReadAllTextAsync(config);
    Check(configured.Contains("dd_configuration_option = disabled") && configured.Contains("dd_config_revert_on_disconnect = disabled"), "explicit display negotiation keeps backend reconfiguration and restoration disabled");
    Check(configured.Contains("dd_manual_resolution = 1600x2400") && configured.Contains("dd_manual_refresh_rate = 60"), "new negotiated display metadata is retained without backend mode application");
    Check(state.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(profile.DirectoryFor("tablet"), "state.json"))) && certificate.SequenceEqual(await File.ReadAllBytesAsync(profile.CertificateFor("tablet"))), "display migration and negotiation preserve media identity and pairing state");
}
Console.WriteLine($"{checks} media trust and profile checks passed; temporary profiles and in-memory transport only, no installed state or real network accessed.");
