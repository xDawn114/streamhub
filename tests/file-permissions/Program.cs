using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;

var root = Path.GetFullPath(args.Single());
if (!System.Text.RegularExpressions.Regex.IsMatch(root, @"^D:\\Deepseek\\build\\stage\d+-tests\\file-permissions\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    throw new IOException("An isolated file-permissions fixture is required.");
Directory.CreateDirectory(root);
var shared = Path.Combine(root, "shared"); Directory.CreateDirectory(shared);
var source = Encoding.UTF8.GetBytes("scope-controlled download 中文");
File.WriteAllBytes(Path.Combine(shared, "sample.txt"), source);
const string client = "bound-tablet", noFiles = "bound-no-files", legacy = "legacy-client";
string key = new('a', 64), otherKey = new('b', 64);
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void Denied(Action action, int status, string label) {
    try { action(); throw new Exception("Unexpectedly allowed: " + label); }
    catch (TransferError error) { Check(error.Status == status, label); }
}
byte[]? savedRegistry = null;
var registry = new ControlDeviceRegistry(save: bytes => savedRegistry = bytes);
var grant = registry.Issue(client, "tablet");
var limited = registry.Issue(noFiles, "phone", ControlScope.Remote);
var spool = Path.Combine(root, "spool");
var vault = new FileTransferVault(spool, throttle: false) { DeviceFilePermission = registry.FilePermission };
vault.Configure(true, shared, true);
var registered = vault.Register(client, "tablet", key);
Check(registered.Allowed && registered.UsesDevicePermissions, "bound file scope allows access without a second grant");
Check(vault.Browse(client, key, "").Entries.Single().Name == "sample.txt", "browse is available without a streaming lease");
Check(vault.Register(client, "tablet", key).Allowed, "reopening the file client reuses its existing identity without another grant");
vault.SetDevicePermissions(client, false, true);
var restoredRegistry = new ControlDeviceRegistry(savedRegistry);
var restoredVault = new FileTransferVault(spool, throttle: false) { DeviceFilePermission = restoredRegistry.FilePermission };
var restoredDevice = restoredVault.Register(client, "tablet", key);
Check(restoredDevice.Allowed && !restoredDevice.UploadAllowed && restoredDevice.DeleteAllowed,
    "central trust and separate upload/delete permissions survive a vault and registry restart");
Check(restoredVault.Browse(client, key, "").Entries.Single().Name == "sample.txt", "restarted trusted client browses the existing roots directly");
using (var lockedSettings = new FileStream(Path.Combine(spool, "devices.json.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
{
    try { vault.SetDevicePermissions(client, true, false); throw new Exception("Failed save accepted"); }
    catch (IOException) { Check(!vault.Snapshot().Devices.Single(x => x.ClientId == client).UploadAllowed,
        "a failed permission save leaves the previous in-memory permissions intact"); }
    try { vault.Register("unsaved-device", "unsaved", otherKey); throw new Exception("Failed registration accepted"); }
    catch (IOException) { Check(!vault.Snapshot().Devices.Any(x => x.ClientId == "unsaved-device"),
        "a failed registration is not retained as an in-memory identity"); }
}
restoredVault = new FileTransferVault(spool, throttle: false) { DeviceFilePermission = restoredRegistry.FilePermission };
Check(!restoredVault.Snapshot().Devices.Single(x => x.ClientId == client).UploadAllowed &&
    restoredVault.Snapshot().Devices.Single(x => x.ClientId == client).DeleteAllowed, "failed writes also preserve permissions on disk");
vault.RemoveDevice(client);
Denied(() => vault.Browse(client, key, ""), 403, "a removed file identity cannot use its old credential");
Check(vault.Register(client, "tablet", key).Allowed, "re-registration still derives trust from the central device permission");
Check(!vault.Register(noFiles, "phone", otherKey).Allowed, "remote-only scope does not grant file access");
Denied(() => vault.Grant(noFiles, true), 409, "legacy grant cannot bypass a bound device's central scope");
Denied(() => vault.Browse(noFiles, otherKey, ""), 403, "missing central file scope blocks browsing");
Denied(() => vault.Register(client, "tablet", otherKey), 403, "binding does not bypass the file key");
Denied(() => vault.Browse(client, otherKey, ""), 403, "incorrect file key still blocks reads");
try { ControlDeviceRegistry.EnsureClient(grant.Principal, noFiles); throw new Exception("Mismatched client accepted"); }
catch (UnauthorizedAccessException) { Check(true, "control identity cannot act as another client"); }
var api = new FileTransferApi(vault, (_, id) => ControlDeviceRegistry.EnsureClient(grant.Principal, id));
var context = new DefaultHttpContext();
var body = JsonSerializer.SerializeToUtf8Bytes(new { clientId = client, name = "tablet", key });
context.Request.Method = "POST"; context.Request.Path = "/v1/files/register";
context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length;
context.Request.Body = new MemoryStream(body); context.Response.Body = new MemoryStream();
await api.HandleAsync(context, CancellationToken.None); context.Response.Body.Position = 0;
using (var response = await JsonDocument.ParseAsync(context.Response.Body)) {
    Check(context.Response.StatusCode == 200 && response.RootElement.GetProperty("allowed").GetBoolean() &&
        response.RootElement.GetProperty("usesDevicePermissions").GetBoolean(), "registration reports the single central permission to Android");
}
using (var download = vault.OpenSharedDownload(client, key, "sample.txt")) {
    await vault.StageOutgoingAsync(download.Item.Id, download.Source, CancellationToken.None);
    Check((await vault.ReadChunkAsync(download.Item.Id, client, key, 0, CancellationToken.None)).SequenceEqual(source), "authorized download returns the exact selected bytes");
    registry.SetScope(grant.Principal.Id, ControlScope.Remote);
    Check(!vault.Snapshot().Devices.Single(x => x.ClientId == client).Allowed, "desktop snapshot follows scope removal immediately");
    try { await vault.ReadChunkAsync(download.Item.Id, client, key, 0, CancellationToken.None); throw new Exception("Revoked chunk accepted"); }
    catch (TransferError error) { Check(error.Status == 403, "removing file scope stops the next queued download chunk"); }
    registry.SetScope(grant.Principal.Id, ControlScope.All);
    Check(vault.Get(download.Item.Id, client, key).State == "ready", "restoring file scope restores the existing task");
    vault.Configure(false, ""); Denied(() => vault.Get(download.Item.Id, client, key), 403, "global file switch still stops queued reads");
    vault.Configure(true, shared, false); Denied(() => vault.Get(download.Item.Id, client, key), 403, "browse switch still revokes a staged shared source");
    vault.Configure(true, shared, true);
}
vault.SetDevicePermissions(client, false, false);
var staged = vault.QueueOutgoing(client, "queued.txt", source.Length);
registry.SetScope(grant.Principal.Id, ControlScope.Remote);
try { await vault.StageOutgoingAsync(staged.Id, new MemoryStream(source), CancellationToken.None); throw new Exception("Revoked staging accepted"); }
catch (OperationCanceledException) { Check(true, "removing file scope also stops an outgoing staging job"); }
registry.SetScope(grant.Principal.Id, ControlScope.All);
Denied(() => vault.CreateUpload(client, key, "upload.txt", 0, Convert.ToHexString(SHA256.HashData([]))), 403, "download permission does not bypass the separate upload switch");
Denied(() => vault.RecycleSharedFile(client, key, "legacy", "sample.txt"), 403, "download permission does not bypass the separate delete switch");
Check(File.Exists(Path.Combine(shared, "sample.txt")), "permission checks preserve source files");
vault.Register(legacy, "legacy", otherKey);
Denied(() => vault.Browse(legacy, otherKey, ""), 403, "legacy file identities remain explicitly opt-in");
vault.Grant(legacy, true);
Check(vault.Browse(legacy, otherKey, "").Entries.Length == 1, "existing legacy grant remains compatible");
var promoted = registry.Issue(legacy, "legacy");
Check(vault.Register(legacy, "legacy", otherKey).UsesDevicePermissions, "an existing legacy identity can move to central permissions");
registry.Revoke(grant.Principal.Id);
Denied(() => vault.Browse(client, key, ""), 403, "revoking the binding immediately blocks browsing");
var restarted = new FileTransferVault(spool, throttle: false) { DeviceFilePermission = new ControlDeviceRegistry().FilePermission };
Denied(() => restarted.Browse(client, key, ""), 403, "lost or pruned binding records cannot fall back to a file grant after restart");
Denied(() => restarted.Browse(legacy, otherKey, ""), 403, "a migrated legacy grant cannot reappear after restart");
Denied(() => restarted.Grant(legacy, true), 409, "central permission mode remains durable across restart");
Check(File.ReadAllBytes(Path.Combine(shared, "sample.txt")).SequenceEqual(source), "all checks leave the exact source bytes intact");
var inactiveSpool=Path.Combine(root,"inactive-spool");
var inactiveVault=new FileTransferVault(inactiveSpool, throttle:false);
inactiveVault.Configure(false,shared);
Check(!inactiveVault.Snapshot().Settings.Enabled&&inactiveVault.Snapshot().Settings.ReceiveDirectory==shared,"first receive directory can be saved while transfers remain off");
var inactiveRestored=new FileTransferVault(inactiveSpool, throttle:false);
Check(!inactiveRestored.Snapshot().Settings.Enabled&&inactiveRestored.Snapshot().Settings.ReceiveDirectory==shared,"disabled receive directory survives a vault restart");
Check(inactiveRestored.Snapshot().Devices.Length==0&&!inactiveRestored.Snapshot().Settings.BrowseEnabled,"choosing a directory does not grant a device or enable browsing");
var alternate=Path.Combine(root,"alternate");Directory.CreateDirectory(alternate);
inactiveRestored.Configure(false,alternate);
Check(!inactiveRestored.Snapshot().Settings.Enabled&&inactiveRestored.Snapshot().Settings.ReceiveDirectory==alternate,"changing a disabled receive directory saves only its path");
inactiveRestored.Configure(false,"");
Check(inactiveRestored.Snapshot().Settings.ReceiveDirectory==alternate,"turning off without a new directory preserves the saved path");
Denied(()=>inactiveRestored.Configure(false,Path.Combine(root,"missing")),400,"invalid receive directory is rejected even while disabled");
Check(inactiveRestored.Snapshot().Settings.ReceiveDirectory==alternate,"invalid directory leaves the saved configuration intact");
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, installedServiceCalls = 0 }));
Console.WriteLine($"{checks} file permission checks passed.");
