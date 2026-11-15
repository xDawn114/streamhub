using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;
using StreamHub.Service;

// No sockets, installed service, Windows password, real device or desktop input.
string root = Path.GetFullPath(args.Single()); Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
void Denied(Action action, string label)
{ try { action(); } catch (UnauthorizedAccessException) { checks.Add(label); return; } throw new Exception(label); }
byte[]? saved = null;
var devices = new ControlDeviceRegistry(save: bytes => saved = bytes.ToArray());
byte[] legacy = RandomNumberGenerator.GetBytes(32);
var a = devices.Issue("fixture-client-aaaa", "平板夹具");
var b = devices.Issue("fixture-client-bbbb", "手机夹具");
string Bearer(byte[] token) => "Bearer " + Convert.ToHexString(token);
Check(devices.Authenticate(Bearer(a.Token), legacy)?.Id == a.Principal.Id, "independent token authenticates");
Check(!Encoding.UTF8.GetString(saved!).Contains(Convert.ToHexString(a.Token)), "persisted database contains no raw token");
var restored = new ControlDeviceRegistry(saved);
Check(restored.Authenticate(Bearer(a.Token), legacy)?.ClientId == a.Principal.ClientId, "database round trip retains credential");
Denied(() => ControlDeviceRegistry.EnsureClient(a.Principal, b.Principal.ClientId), "client spoof rejected");
devices.SetScope(b.Principal.Id, ControlScope.Files);
Denied(() => devices.EnsureCurrent(b.Principal, b.Principal.ClientId, ControlScope.Remote), "removed remote scope rejected");
devices.SetLegacyAllowed(false);
Check(devices.Authenticate(Bearer(legacy), legacy) is null, "legacy disabled without replacing TLS identity");
devices.SetLegacyAllowed(true);
Check(devices.Authenticate(Bearer(legacy), legacy)?.Legacy == true, "legacy migration is explicit");
var failing = new ControlDeviceRegistry(saved, _ => throw new IOException("fixture-save-failure"));
try { failing.Revoke(a.Principal.Id); } catch (IOException) { }
Check(failing.Authenticate(Bearer(a.Token), legacy) is not null, "save failure preserves prior state");
try { _ = new ControlDeviceRegistry(Encoding.UTF8.GetBytes("{\"version\":1,\"legacyAllowed\":true,\"devices\":[null]}")); throw new Exception("null record accepted"); }
catch (InvalidDataException) { checks.Add("malformed persisted record fails closed"); }

// Device presentation metadata must never rotate credentials or widen grants.
byte[]? metadataSaved = null; int metadataWrites = 0;
var metadata = new ControlDeviceRegistry(save: bytes => { metadataSaved = bytes.ToArray(); metadataWrites++; });
var metadataGrant = metadata.Issue("metadata-client", "PA2573", ControlScope.Remote | ControlScope.Tablet);
long grantRevision = metadata.Revision;
metadata.Seen(metadataGrant.Principal, "tablet");
int firstSeenWrites = metadataWrites;
metadata.Seen(metadataGrant.Principal);
Check(metadata.Revision == grantRevision && metadataWrites == firstSeenWrites && metadata.Snapshot().Devices[0].LastSeen is not null,
    "authenticated heartbeat updates contact without grant reconciliation or repeated disk writes");
metadata.Rename(metadataGrant.Principal.Id, "宿舍平板");
var metadataRestored = new ControlDeviceRegistry(metadataSaved);
Check(metadataRestored.Authenticate(Bearer(metadataGrant.Token), legacy) is { Name: "宿舍平板" } restoredMetadataPrincipal &&
    restoredMetadataPrincipal.Scope == (ControlScope.Remote | ControlScope.Tablet) &&
    metadataRestored.Snapshot().Devices[0] is { DeviceType: "tablet", LastSeen: not null }, "alias and type persist while identity, token and scope stay intact");
metadata.Rename(metadataGrant.Principal.Id, "  ");
Check(metadata.Resolve(metadataGrant.Principal.Id).Name == "PA2573", "blank alias restores original model name");
try { metadata.Rename(metadataGrant.Principal.Id, "bad\nname"); throw new Exception("control character accepted"); }
catch (ArgumentException) { checks.Add("alias rejects control characters"); }
var oldDatabase = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(metadataSaved!))!;
foreach (var item in oldDatabase["devices"]!.AsArray()) { item!.AsObject().Remove("alias"); item.AsObject().Remove("deviceType"); item.AsObject().Remove("lastSeen"); }
var oldRestored = new ControlDeviceRegistry(Encoding.UTF8.GetBytes(oldDatabase.ToJsonString()));
Check(oldRestored.Authenticate(Bearer(metadataGrant.Token), legacy) is not null && oldRestored.Snapshot().Devices[0].DeviceType == "android",
    "155 database without metadata loads without rebinding");
metadata.Revoke(metadataGrant.Principal.Id); var revokedSeen = metadata.Snapshot().Devices[0].LastSeen;
metadata.Seen(metadataGrant.Principal, "phone");
Check(metadata.Snapshot().Devices[0].LastSeen == revokedSeen, "revoked principal cannot update contact metadata");
var failedTelemetry = new ControlDeviceRegistry(Encoding.UTF8.GetBytes(oldDatabase.ToJsonString()), _ => throw new CryptographicException("fixture"));
failedTelemetry.Seen(metadataGrant.Principal, "phone");
Check(failedTelemetry.Snapshot().Devices[0].DeviceType == "phone", "metadata persistence failure does not interrupt authenticated traffic");

using var key = RSA.Create(2048);
using var certificate = new CertificateRequest("CN=DeviceGrantFixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
string cert = Convert.ToBase64String(certificate.RawData);
devices.BindMedia(a.Principal.Id, "remote", cert);
devices.EnsureMediaBound(a.Principal.Id, "remote"); checks.Add("individual credential binds exact media certificate");
Denied(() => devices.EnsureMediaBound(a.Principal.Id, "tablet"), "pairing requires media preflight");
devices.SetScope(b.Principal.Id, ControlScope.All);
Denied(() => devices.BindMedia(b.Principal.Id, "remote", cert), "another device cannot claim existing media identity");

var flags = BindingFlags.Instance | BindingFlags.NonPublic;
var calls = new List<RuntimeRequest>();
var engine = new EngineStatus("remote", true, true, null, 48989, true, "fixture");
var status = new RuntimeStatus("fixture", "fixture", engine, engine with { Profile="tablet", Port=49089 }, new(false,false,null,"fixture"), "fixture", root, DateTimeOffset.UtcNow);
var runtime = new RuntimeClient((request, _) => { calls.Add(request); return Task.FromResult(new RuntimeResponse(true,"fixture",status)); });
var vault = new FileTransferVault(Path.Combine(root,"vault"));
await using var api = new ControlApiServer(runtime, (_,_,_) => Task.CompletedTask, new([],legacy), X509KeyStorageFlags.EphemeralKeySet,
    _ => Task.FromResult(new HostDisplayMode(1280,720,60)), vault,
    (_,_,_,_,_,_) => Task.FromResult<ClipboardPayload?>(new("text","fixture")), deviceRegistry:devices);
typeof(ControlApiServer).GetField("_token",flags)!.SetValue(api,legacy);
async Task<(int Status,string Body)> Send(byte[] token, string path, object? body = null, string? fileClient = null)
{
    var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Loopback;
    context.Request.Method = body is null ? "GET" : "POST"; context.Request.Path = path;
    context.Request.Headers.Authorization = Bearer(token); context.Response.Body = new MemoryStream();
    if (fileClient is not null) context.Request.Headers["X-StreamHub-Client"] = fileClient;
    if (body is not null)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body); context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length; context.Request.Body = new MemoryStream(bytes);
    }
    await (Task)typeof(ControlApiServer).GetMethod("HandleAsync",flags)!.Invoke(api,[context])!;
    context.Response.Body.Position = 0;
    return (context.Response.StatusCode,await new StreamReader(context.Response.Body).ReadToEndAsync());
}
string lease = "fixture-lease-aaaa";
var infoMetadata = JsonDocument.Parse((await Send(a.Token,"/v1/info")).Body);
Check(infoMetadata.RootElement.GetProperty("capabilities").GetProperty("deviceMetadata").GetBoolean(), "metadata capability advertised for backward-compatible Android polling");
Check((await Send(a.Token,"/v1/receiver/poll",new {clientId=a.Principal.ClientId,name="fixture",width=1280,height=720,refreshRate=60,deviceType="bad"})).Status == 400,
    "unknown receiver type is a client error");
Check((await Send(a.Token,"/v1/receiver/poll",new {clientId=a.Principal.ClientId,name="fixture",width=1280,height=720,refreshRate=60,deviceType="tablet"})).Status == 200 &&
    devices.Snapshot().Devices.Single(d=>d.Id==a.Principal.Id).DeviceType == "tablet", "authenticated poll persists receiver type");

// Exercise the real paused-state gate without running engines, display changes or sockets.
string pausedRoot=Path.Combine(root,"paused-runtime");Directory.CreateDirectory(Path.Combine(pausedRoot,"logs"));
await using (var pausedRuntime=new HostRuntime(pausedRoot,pausedRoot,false))
{
    typeof(HostRuntime).GetField("_connectionsPaused",flags)!.SetValue(pausedRuntime,true);
    foreach(string command in new[]{"remote-prepare","tablet-prepare","start","restart","pair","tablet-request","media-identity"})
    {
        var blocked=await (Task<RuntimeResponse>)typeof(HostRuntime).GetMethod("HandleAsync",flags)!.Invoke(pausedRuntime,[new RuntimeRequest(command,Profile:"remote"),CancellationToken.None])!;
        Check(!blocked.Success,"paused runtime rejects "+command);
    }
    await (Task)typeof(HostRuntime).GetMethod("SaveDesiredAsync",flags)!.Invoke(pausedRuntime,[CancellationToken.None])!;
    var pausedDesired=JsonSerializer.Deserialize<DesiredState>(await File.ReadAllTextAsync(Path.Combine(pausedRoot,"desired.json")));
    Check(pausedDesired?.ConnectionsPaused==true,"pause survives durable desired-state serialization");
}
devices.SetScope(b.Principal.Id, ControlScope.Files);
Check((await Send(b.Token,"/v1/session/desktop",new { leaseId=lease,clientId=b.Principal.ClientId,action="status" })).Status == 403,"desktop endpoint requires remote scope");
Check((await Send(b.Token,"/v1/session/privacy",new { leaseId=lease,clientId=b.Principal.ClientId })).Status == 403,"default privacy mode cannot bypass remote scope");
Check((await Send(b.Token,"/v1/session/heartbeat",new { leaseId=lease,clientId=b.Principal.ClientId })).Status == 403,"default tablet mode cannot bypass scope");
Check((await Send(a.Token,"/v1/session/heartbeat",new { leaseId=lease,mode="remote",clientId=b.Principal.ClientId })).Status == 403,"heartbeat rejects spoofed client");
Check((await Send(a.Token,"/v1/session/desktop",new { LeaseId=lease,ClientId=b.Principal.ClientId,Action="status" })).Status == 403,"case variant cannot bypass client identity check");
Check((await Send(b.Token,"/v1/prepare-jobs",new { Mode="remote",ClientId=b.Principal.ClientId,LeaseId=lease })).Status == 403,"case variant cannot bypass mode scope check");
Check((await Send(a.Token,"/v1/clipboard",new { leaseId=lease,mode="remote",clientId=b.Principal.ClientId,action="read" })).Status == 403,"clipboard separately checks device identity");
Check((await Send(a.Token,"/v1/files/register",new { clientId=b.Principal.ClientId,name="fixture",key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) },a.Principal.ClientId)).Status == 403,"file registration checks body and header identity");
var preparation = await Send(a.Token,"/v1/prepare-jobs",new { mode="remote",clientId=a.Principal.ClientId,leaseId=lease });
Check(preparation.Status == 200,"authorized preparation accepted against fixture runtime");
string jobId = JsonDocument.Parse(preparation.Body).RootElement.GetProperty("jobId").GetString()!;
devices.SetScope(b.Principal.Id,ControlScope.All);
Check((await Send(b.Token,"/v1/jobs/"+jobId)).Status == 403,"other device cannot read prepare result");
Check((await Send(b.Token,"/v1/jobs/"+jobId+"/cancel",new { })).Status == 403,"other device cannot cancel prepare job");
Check((await Send(a.Token,"/v1/jobs/"+jobId)).Status == 200,"owner can inspect preparation job");
Check((await Send(a.Token,"/v1/session/heartbeat",new { leaseId=lease,mode="remote",clientId=a.Principal.ClientId,streamReady=true })).Status == 200,"authorized heartbeat reaches synthetic runtime");
Check(calls.Any(r => r.Command == "remote-heartbeat" && r.ControllerCredentialId == a.Principal.Id && r.ControllerName == "平板夹具"),"session label comes from verified credential");
devices.Revoke(a.Principal.Id);
Check((await Send(a.Token,"/v1/info")).Status == 401,"revoked token rejected by production handler");
Denied(() => devices.EnsureCurrent(a.Principal,a.Principal.ClientId,ControlScope.Remote),"in-flight principal is revalidated");
Check(devices.BlockedMedia("remote").SequenceEqual(new[]{cert}),"revoked media certificate queued for removal");
var newGrant = devices.Issue(a.Principal.ClientId!,"平板夹具");
Check(devices.Authenticate(Bearer(a.Token),legacy) is null && devices.Authenticate(Bearer(newGrant.Token),legacy) is not null,"confirmed re-enrollment rotates token");

// Exact-certificate revocation on a stopped synthetic profile, preserving unrelated trust/settings.
var profiles = new ProfileStore(root,Path.Combine(root,"profile-fixture"));
string directory = profiles.DirectoryFor("remote"); Directory.CreateDirectory(directory);
using var otherKey = RSA.Create(2048);
using var otherCertificate = new CertificateRequest("CN=UnrelatedFixture",otherKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
string keepId = Guid.NewGuid().ToString();
var state = new { fixtureSetting="preserve",root=new { uniqueid="fixture",named_devices=new[] {
    new { uuid=Guid.NewGuid().ToString(),cert=PemEncoding.WriteString("CERTIFICATE",certificate.RawData),enabled=true },
    new { uuid=keepId,cert=PemEncoding.WriteString("CERTIFICATE",otherCertificate.RawData),enabled=true } } } };
await File.WriteAllTextAsync(Path.Combine(directory,"state.json"),JsonSerializer.Serialize(state));
await profiles.RemoveMediaTrustAsync("remote",devices.BlockedMedia("remote"),CancellationToken.None);
var remaining = await profiles.ReadMediaTrustAsync("remote",CancellationToken.None);
Check(remaining.Length==1 && remaining[0].Uuid==keepId,"media revoke preserves unrelated identity");
Check((await File.ReadAllTextAsync(Path.Combine(directory,"state.json"))).Contains("preserve"),"media revoke preserves unrelated settings");
devices.BindMedia(newGrant.Principal.Id,"remote",cert);
Check(devices.BlockedMedia("remote").Length==0,"confirmed renewed grant can re-pair same device identity");
devices.SetScope(newGrant.Principal.Id,ControlScope.Files);
Check(devices.BlockedMedia("remote").Length==1,"removing remote permission also revokes associated media");

// The claimed preflight certificate must equal the identity actually added by
// /pin. Persist recovery intent first, so service death cannot retain a substitute.
byte[]? journalSaved=null;
var pairingDevices=new ControlDeviceRegistry(save:bytes=>journalSaved=bytes.ToArray());
var pairingGrant=pairingDevices.Issue("fixture-client-pair","配对夹具");
pairingDevices.BindMedia(pairingGrant.Principal.Id,"remote",cert);
var pairingProfiles=new ProfileStore(root,Path.Combine(root,"pairing-fixture"));
string pairingDirectory=pairingProfiles.DirectoryFor("remote");Directory.CreateDirectory(pairingDirectory);
string pairingPath=Path.Combine(pairingDirectory,"state.json");
string oldId=Guid.NewGuid().ToString();
object Client(string id,X509Certificate2 c)=>new {uuid=id,cert=PemEncoding.WriteString("CERTIFICATE",c.RawData),enabled=true};
Task WritePairingState(params object[] clients)=>File.WriteAllTextAsync(pairingPath,JsonSerializer.Serialize(new {fixtureSetting="pair-preserve",root=new {uniqueid="fixture",named_devices=clients}}));
await WritePairingState(Client(oldId,otherCertificate));
var pairingGuard=new DeviceMediaPairing(pairingDevices,pairingProfiles);
await pairingGuard.BeginAsync(pairingGrant.Principal.Id,"remote",CancellationToken.None);
Check(journalSaved is not null && new ControlDeviceRegistry(journalSaved).PendingMediaPairing("remote") is not null,"pairing fence persists before backend approval");
using var surpriseKey=RSA.Create(2048);
using var surpriseCert=new CertificateRequest("CN=SubstitutedFixture",surpriseKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
await WritePairingState(Client(oldId,otherCertificate),Client(Guid.NewGuid().ToString(),surpriseCert));
try {await pairingGuard.CompleteAsync("remote",CancellationToken.None);throw new Exception("substitute pairing accepted");}
catch(UnauthorizedAccessException){checks.Add("actual paired certificate must match preflight identity");}
var recoveredDevices=new ControlDeviceRegistry(journalSaved);
var recoveredGuard=new DeviceMediaPairing(recoveredDevices,pairingProfiles);
await recoveredGuard.RecoverAsync("remote",CancellationToken.None);
Check((await pairingProfiles.ReadMediaTrustAsync("remote",CancellationToken.None)).Single().Uuid==oldId &&
    (await File.ReadAllTextAsync(pairingPath)).Contains("pair-preserve"),"crash recovery removes only added media identity and preserves baseline/settings");
Check(recoveredDevices.PendingMediaPairing("remote") is null,"successful recovery clears durable fence");
await recoveredGuard.BeginAsync(pairingGrant.Principal.Id,"remote",CancellationToken.None);
await WritePairingState(Client(oldId,otherCertificate),Client(Guid.NewGuid().ToString(),certificate));
await recoveredGuard.CompleteAsync("remote",CancellationToken.None);
Check(recoveredDevices.PendingMediaPairing("remote") is null,"expected completed handshake commits pairing");
await WritePairingState(Client(oldId,otherCertificate));
await recoveredGuard.BeginAsync(pairingGrant.Principal.Id,"remote",CancellationToken.None);
await WritePairingState(Client(oldId,otherCertificate),Client(Guid.NewGuid().ToString(),certificate));
recoveredDevices.Revoke(pairingGrant.Principal.Id);
try {await recoveredGuard.CompleteAsync("remote",CancellationToken.None);throw new Exception("revoked pairing accepted");}
catch(UnauthorizedAccessException){checks.Add("revocation during pairing prevents commit");}
await recoveredGuard.RecoverAsync("remote",CancellationToken.None);
Check((await pairingProfiles.ReadMediaTrustAsync("remote",CancellationToken.None)).Single().Uuid==oldId,"cancelled or revoked handshake rolls back its added certificate");

string firstDirectory=Path.Combine(root,"receive-a"),secondDirectory=Path.Combine(root,"receive-b");
Directory.CreateDirectory(firstDirectory);Directory.CreateDirectory(secondDirectory);
vault.Configure(true,firstDirectory,true);
vault.Configure(true,secondDirectory);
var fileSettings=vault.Snapshot().Settings;
Check(fileSettings.ReceiveDirectory==secondDirectory && fileSettings.BrowseRoots is {Length:1} && fileSettings.BrowseRoots[0].Path==firstDirectory,
    "changing receive directory preserves legacy browse grant instead of expanding it");
if (!System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
{
    string protectedRoot=Path.Combine(root,"must-not-be-created");
    try { _=ControlDeviceStore.Open(protectedRoot);throw new Exception("non-service store access accepted"); }
    catch(InvalidOperationException) { Check(!Directory.Exists(protectedRoot),"owner process cannot initialize service-only device database"); }
}

await File.WriteAllTextAsync(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new { passed=true,count=checks.Count,checks,installedServiceCalls=0,socketsOpened=0,desktopInputOperations=0 },new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks.Count} device-grant and production API checks; no installed service or input used.");
