using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;

var root = Path.GetFullPath(args.Single());
if (!System.Text.RegularExpressions.Regex.IsMatch(root,@"^D:\\Deepseek\\build\\stage\d+-tests\\files\\",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new Exception("Use an isolated stageN files fixture.");
Directory.CreateDirectory(root);
var shared = Path.Combine(root, "shared"); Directory.CreateDirectory(Path.Combine(shared, "中文 子目录"));
var content = Encoding.UTF8.GetBytes("离线文件访问，不启动远控 😀");
File.WriteAllBytes(Path.Combine(shared, "中文 子目录", "hello.txt"), content);
File.WriteAllText(Path.Combine(root, "outside.txt"), "private");
var vault = new FileTransferVault(Path.Combine(root, "spool"), throttle: false);
const string client = "fixture-client", other = "fixture-other";
var key = new string('a', 64); var otherKey = new string('b', 64); int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Denied(Action action, string message)
{ try { action(); throw new Exception("Allowed: " + message); } catch (TransferError error) { Check(error.Status is 400 or 403 or 404, message); } }
vault.Register(client, "tablet", key); vault.Register(other, "other", otherKey); vault.Configure(true, shared);
Denied(() => vault.Browse(client, key, ""), "binding alone cannot grant file access");
vault.Grant(client, true);
Denied(() => vault.Browse(client, key, ""), "browsing stays opt-in after transfer authorization");
vault.Configure(true, shared, true);
Check(vault.Browse(client, key, "").Entries.Single().Directory, "list a selected shared folder without a streaming lease");
Check(vault.Browse(client, key, "中文 子目录").Entries.Single().Name == "hello.txt", "Unicode paths round trip");
foreach (var path in new[] { "..", "../outside.txt", "中文 子目录/../../outside.txt", @"C:\Windows", "/Windows", "hello.txt:secret", "中文 子目录//hello.txt", "中文 子目录/./hello.txt" })
    Denied(() => vault.OpenSharedDownload(client, key, path), "reject traversal / ADS / invalid path " + path);
Denied(() => vault.Browse(client, otherKey, ""), "wrong device key cannot browse");
Denied(() => vault.Browse(other, otherKey, ""), "unapproved second device cannot browse");
using (var download = vault.OpenSharedDownload(client, key, "中文 子目录/hello.txt"))
{
    await vault.StageOutgoingAsync(download.Item.Id, download.Source, CancellationToken.None);
    var item = vault.Get(download.Item.Id, client, key);
    Check(item.State == "ready" && item.Sha256 == Convert.ToHexString(SHA256.HashData(content)), "snapshot prepares a resumable download with exact SHA256");
    Check((await vault.ReadChunkAsync(item.Id, client, key, 0, CancellationToken.None)).SequenceEqual(content), "download bytes match source");
    vault.Grant(client, false);
    try { await vault.ReadChunkAsync(item.Id, client, key, 0, CancellationToken.None); throw new Exception("revocation failed"); }
    catch (TransferError) { Check(true, "revocation blocks the next download chunk"); }
    vault.Grant(client, true);
    vault.Configure(true, shared, false);
    try { await vault.ReadChunkAsync(item.Id, client, key, 0, CancellationToken.None); throw new Exception("browse revocation failed"); }
    catch (TransferError) { Check(true, "turning off browsing revokes already staged shared downloads"); }
    vault.Configure(true, shared, true);
}
// Junctions do not require symlink developer mode. Never follow them outside the shared tree.
var junction = Path.Combine(shared, "escape");
var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
foreach (var value in new[] { "/d", "/c", "mklink", "/J", junction, root }) start.ArgumentList.Add(value);
using (var process=System.Diagnostics.Process.Start(start)!) { await process.WaitForExitAsync(); Check(process.ExitCode==0, "created isolated junction fixture"); }
Denied(() => vault.OpenSharedDownload(client, key, "escape/outside.txt"), "junction escape is rejected");
Check(!vault.Browse(client, key, "").Entries.Any(x=>x.Name=="escape"), "junction is omitted from browser listings");
Directory.Delete(junction); // Remove only the junction itself, never its target.
var api = new FileTransferApi(vault);
var context = new DefaultHttpContext(); var body = Encoding.UTF8.GetBytes("{\"path\":\"中文 子目录\"}");
context.Request.Method="POST"; context.Request.Path="/v1/files/browse"; context.Request.ContentType="application/json";
context.Request.ContentLength=body.Length; context.Request.Body=new MemoryStream(body); context.Response.Body=new MemoryStream();
context.Request.Headers["X-StreamHub-Client"]=client; context.Request.Headers["X-StreamHub-Key"]=key;
await api.HandleAsync(context,CancellationToken.None); context.Response.Body.Position=0;
using (var response=await JsonDocument.ParseAsync(context.Response.Body)) Check(response.RootElement.GetProperty("listing").GetProperty("entries").GetArrayLength()==1,"HTTP browser reaches authorized production vault");
var fetch=new DefaultHttpContext(); var fetchBody=Encoding.UTF8.GetBytes("{\"path\":\"中文 子目录/hello.txt\"}");
fetch.Request.Method="POST"; fetch.Request.Path="/v1/files/fetch"; fetch.Request.ContentType="application/json";
fetch.Request.ContentLength=fetchBody.Length; fetch.Request.Body=new MemoryStream(fetchBody); fetch.Response.Body=new MemoryStream();
fetch.Request.Headers["X-StreamHub-Client"]=client; fetch.Request.Headers["X-StreamHub-Key"]=key;
using(var requestLifetime=new CancellationTokenSource())
{
    await api.HandleAsync(fetch,requestLifetime.Token);
    requestLifetime.Cancel(); // The HTTP lifetime must not cancel service-owned staging.
    fetch.Response.Body.Position=0;
    using var response=await JsonDocument.ParseAsync(fetch.Response.Body);
    string id=response.RootElement.GetProperty("task").GetProperty("id").GetString()!;
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while(vault.Get(id,client,key).State=="staging") await Task.Delay(20,deadline.Token);
    Check(vault.Get(id,client,key).State=="ready","HTTP fetch finishes staging after its request lifetime ends");
    Check((await vault.ReadChunkAsync(id,client,key,0,deadline.Token)).SequenceEqual(content),"HTTP fetch downloads exact selected bytes without a streaming session");
}
// Multiple explicit roots never inherit service-account file permissions.
string owner=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
var second=Path.Combine(root,"second");Directory.CreateDirectory(second);File.WriteAllText(Path.Combine(second,"same.txt"),"second-root");
vault.AddBrowseRoot(shared,owner);vault.AddBrowseRoot(second,owner);
var roots=vault.Snapshot().Settings.BrowseRoots!;string secondId=roots.Single(r=>r.Path==second).Id;
Check(vault.Browse(client,key,"",rootId:secondId).Entries.Single().Name=="same.txt","explicit root identifier selects the right authorized directory");
File.WriteAllText(Path.Combine(second,"z-large.txt"),new string('z',128));
Check(vault.Browse(client,key,"",rootId:secondId,sort:"size").Entries.First().Name=="z-large.txt","size sorting orders the whole directory before pagination");
Check(vault.Browse(client,key,"",rootId:secondId,sort:"name-desc").Entries.First().Name=="z-large.txt","descending name sort is deterministic");
Denied(()=>vault.Browse(client,key,"",rootId:secondId,sort:"invalid"),"unknown sorting is rejected");
Denied(()=>vault.Browse(client,key,"",rootId:"revoked-root"),"unknown root cannot fall back to another folder");
vault.SetDevicePermissions(client,false,false);
Check(vault.Browse(client,key,"",rootId:secondId).Entries.Length==2,"read-only device can browse without upload or delete permission");
Denied(()=>vault.CreateUpload(client,key,"test.txt",0,new string('0',64)),"upload permission enforced independently");
Denied(()=>vault.RecycleSharedFile(client,key,secondId,"same.txt"),"read-only download grant cannot delete source files");
Check(File.Exists(Path.Combine(second,"same.txt")),"rejected deletion preserves source");
using(var download=vault.OpenSharedDownload(client,key,"same.txt",secondId)) {
    await vault.StageOutgoingAsync(download.Item.Id,download.Source,CancellationToken.None);
    vault.RemoveBrowseRoot(secondId);
    Denied(()=>vault.Get(download.Item.Id,client,key),"removing root revokes already staged download");
}
var spy=new FileAccessSpy(owner);vault.UserFiles=spy;vault.SetDevicePermissions(client,false,true);
string firstId=vault.Snapshot().Settings.BrowseRoots!.First(r=>r.Path==shared).Id;
Denied(()=>vault.RecycleSharedFile(client,key,firstId,"../outside.txt"),"delete rejects traversal even after explicit permission");
Denied(()=>vault.RecycleSharedFile(client,key,firstId,""),"delete cannot remove an authorization root");
vault.RecycleSharedFile(client,key,firstId,"中文 子目录/hello.txt");
Check(spy.Recycled==Path.Combine(shared,"中文 子目录","hello.txt") && spy.Owner==owner,"recycle runs through owner context; isolated spy never deletes files");
Check(File.Exists(Path.Combine(shared,"中文 子目录","hello.txt")),"permission tests leave source fixture intact");
using(var cancelled=vault.OpenSharedDownload(client,key,"中文 子目录/hello.txt",firstId)) {
    await vault.StageOutgoingAsync(cancelled.Item.Id,cancelled.Source,CancellationToken.None);
    await vault.CancelAsync(cancelled.Item.Id,CancellationToken.None);
    await vault.ClearFinishedAsync(CancellationToken.None);
    Check(File.ReadAllBytes(Path.Combine(shared,"中文 子目录","hello.txt")).SequenceEqual(content),"cancelling a staged download and clearing history preserve the exact source bytes");
}
var recyclePath=Path.Combine(root,"stage74-recycle-fixture.txt");File.WriteAllText(recyclePath,"isolated recycle fixture");
try {
    RecycleBin.Move(recyclePath);
    Check(!File.Exists(recyclePath),"Windows Shell confirms recycle of an isolated fixture");
} catch(TransferError error) when(error.Status is 403 or 409) {
    Check(File.Exists(recyclePath),"unavailable or unconfirmed recycle is rejected without deleting fixture source");
    Console.WriteLine("Recycle execution unavailable in this user token; installed owner-session acceptance remains pending.");
}
foreach(var entry in vault.Snapshot().Settings.BrowseRoots!)vault.RemoveBrowseRoot(entry.Id);
Check(vault.BrowseRoots(client,key).Length==0 && !vault.Snapshot().Settings.BrowseEnabled,"last root revocation never restores legacy receive-folder browsing");
vault.Configure(false, ""); Denied(()=>vault.Browse(client,key,""),"disabling files revokes browsing");
Console.WriteLine($"{checks} shared-file checks passed; no installed service or streaming activity used.");

sealed class FileAccessSpy(string expected) : ISharedFileAccess {
    public string? Owner,Recycled;
    public T Run<T>(string? owner,Func<T> operation){if(owner!=expected)throw new Exception("Wrong owner");Owner=owner;return operation();}
    public void Recycle(string path){Recycled=path;}
}
