using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using StreamHub.Installer;
using StreamHub.Host.Runtime;
using StreamHub.Updates;

if(args.Length!=1)throw new ArgumentException("An isolated artifact root is required.");
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);var checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
void Reject(Action action,string message){try{action();throw new Exception("Expected rejection: "+message);}catch(Exception error) when(error is InvalidOperationException or IOException or JsonException){Check(true,message);}}
async Task RejectAsync(Func<Task> action,string message){try{await action();throw new Exception("Expected rejection: "+message);}catch(Exception error) when(error is InvalidOperationException or IOException or HttpRequestException){Check(true,message);}}
using var rsa=RSA.Create(3072);using var wrongKey=RSA.Create(3072);
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
byte[] Sign(UpdateManifest value,RSA? key=null){var bytes=JsonSerializer.SerializeToUtf8Bytes(value,json);return JsonSerializer.SerializeToUtf8Bytes(new UpdateEnvelope(Convert.ToBase64String(bytes),Convert.ToBase64String((key??rsa).SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))),json);}
var trust=new UpdateTrust("https://updates.example.test/internal.json",Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),[new string('A',64)]);
var now=DateTimeOffset.UtcNow;var content="isolated installer fixture"u8.ToArray();
var manifest=new UpdateManifest(1,"StreamHub","internal","win-x64","0.8.5",132,131,now,now.AddDays(1),"https://updates.example.test/app.exe",content.Length,Convert.ToHexString(SHA256.HashData(content)),"测试更新");
UpdateManifest Read(byte[] bytes)=>UpdatePolicy.Read(bytes,trust,"internal","0.8.4",131,now);
Check(Read(Sign(manifest)).VersionCode==132,"trusted signed manifest accepted");
try { Read(Sign(manifest with{Version="0.8.4",VersionCode=131}));throw new Exception("same version reported as an update"); }
catch(NoNewUpdateException){Check(true,"signed current version reports up to date without launching an installer");}
Reject(()=>Read(Sign(manifest,wrongKey)),"another key cannot publish an update");
foreach(var invalid in new[]{manifest with{Channel="stable"},manifest with{VersionCode=131},manifest with{Version="0.8.3"},manifest with{MinimumInstalledCode=140},
 manifest with{ExpiresAt=now.AddSeconds(-1)},manifest with{PublishedAt=now.AddHours(1)},manifest with{Url="http://updates.example.test/app.exe"},
 manifest with{Url="https://attacker.example.test/app.exe"},manifest with{Url="https://user@updates.example.test/app.exe"},manifest with{Size=UpdatePolicy.MaximumPackageBytes+1},
 manifest with{Architecture="win-arm64"},manifest with{Sha256="bad"}})Reject(()=>Read(Sign(invalid)),"invalid channel/version/time/origin/size/identity rejected");
var tampered=JsonSerializer.Deserialize<UpdateEnvelope>(Sign(manifest),json)!;
Reject(()=>Read(JsonSerializer.SerializeToUtf8Bytes(tampered with{Payload=Convert.ToBase64String("{}"u8.ToArray())},json)),"payload tampering rejected");
Check(!UpdatePolicy.Configured(new("","",[])),"missing signing conditions keep update channel disabled");
var queryTrust=trust with { PublisherSha256=[] };
Check(UpdatePolicy.QueryConfigured(queryTrust)&&!UpdatePolicy.Configured(queryTrust),"version queries are independent of Windows installation identity");
Check(UpdatePolicy.Read(Sign(manifest),queryTrust,"internal","0.8.4",131,now).VersionCode==132,"signed version notification works before Authenticode is configured");
using(var queryHttp=new HttpClient(new FixtureHandler(_=>Sign(manifest))))
{
 var queryClient=new UpdateClient(queryTrust,queryHttp,(_,_,_)=>throw new Exception("query-only called publisher verifier"));
 var queryManifest=await queryClient.CheckAsync("internal","0.8.4",131,default);
 await RejectAsync(()=>queryClient.DownloadAsync(queryManifest,Path.Combine(root,"query-only"),null,default),"query-only updates cannot download unsigned executables");
 await RejectAsync(async()=>{using var ignored=await queryClient.OpenVerifiedAsync(queryManifest,Path.Combine(root,"absent.exe"),default);},"query-only cannot open an executable for installation");
}
using(var redirectHttp=new HttpClient(new RedirectFixtureHandler()))
{
 var redirectClient=new UpdateClient(trust,redirectHttp,(_,_,_)=>Task.CompletedTask);
 await RejectAsync(()=>redirectClient.CheckAsync("internal","0.8.4",131,default),"redirected response cannot replace the pinned update source");
}
var signerCalled=0;
foreach(var statusCode in new[]{HttpStatusCode.PartialContent,HttpStatusCode.NoContent,HttpStatusCode.MovedPermanently})
{
 using var incompleteHttp=new HttpClient(new StatusFixtureHandler(statusCode,Sign(manifest)));
 var incompleteClient=new UpdateClient(trust,incompleteHttp,(_,_,_)=>throw new Exception("incomplete HTTP response reached publisher check"));
 await RejectAsync(()=>incompleteClient.CheckAsync("internal","0.8.4",131,default),"non-200 manifest response is refused even with signed content: "+statusCode);
}
using(var cacheHttp=new HttpClient(new InspectFixtureHandler(request=>
{
 Check(request.Headers.CacheControl?.NoCache==true,"version check explicitly revalidates the feed cache");return Sign(manifest);
})))
{
 var cacheClient=new UpdateClient(trust,cacheHttp,(_,_,_)=>Task.CompletedTask);
 await cacheClient.CheckAsync("internal","0.8.4",131,default);
}
var attemptPath=Path.Combine(root,"isolated-preferences","install-attempt.json");
UpdateInstallAttempt.Save(attemptPath,new(140,141,"internal",now));
Check(UpdateInstallAttempt.Describe(attemptPath,141,"internal",now).Contains("已完成"),"restart confirms completion from actual installed code");
Check(UpdateInstallAttempt.Describe(attemptPath,140,"internal",now).Contains("尚未确认"),"cancel or interruption never reports installation success");
Check(UpdateInstallAttempt.Describe(attemptPath,141,"stable",now)=="","another channel cannot confirm an old install attempt");
Check(UpdateInstallAttempt.Describe(attemptPath,142,"internal",now)=="","later manual upgrades do not repeat stale success messages");
Check(UpdateInstallAttempt.Describe(attemptPath,141,"internal",now.AddDays(31))=="","expired attempt does not override current update state");
Reject(()=>UpdateInstallAttempt.Save(attemptPath,new(141,140,"internal",now)),"install attempt cannot describe a downgrade");
File.WriteAllText(attemptPath,"broken json");
Check(UpdateInstallAttempt.Describe(attemptPath,141,"internal",now)=="","interrupted local receipt cannot break the update settings page");
var handler=new FixtureHandler(uri=>uri.AbsolutePath.EndsWith(".json")?Sign(manifest):content);
using var http=new HttpClient(handler);
var client=new UpdateClient(trust,http,(path,publishers,token)=>{signerCalled++;Check(publishers.SequenceEqual(trust.PublisherSha256),"publisher verifier receives pinned identity");return Task.CompletedTask;});
var accepted=await client.CheckAsync("internal","0.8.4",131,default);
var path=await client.DownloadAsync(accepted,Path.Combine(root,"downloads"),null,default);
Check(File.ReadAllBytes(path).SequenceEqual(content)&&signerCalled==1,"download checks actual bytes before publisher verification");
using(var locked=await client.OpenVerifiedAsync(accepted,path,default))Reject(()=>File.WriteAllBytes(path,"changed"u8.ToArray()),"verified package remains locked until launch");
File.WriteAllBytes(path,new byte[content.Length]);
await RejectAsync(async()=>{using var file=await client.OpenVerifiedAsync(accepted,path,default);},"same-length tampering is rejected before launch");
foreach(var bytes in new[]{content[..^1],content.Concat(new byte[]{1}).ToArray(),new byte[content.Length]})
{
 using var transport=new HttpClient(new FixtureHandler(uri=>uri.AbsolutePath.EndsWith(".json")?Sign(manifest):bytes));
 var download=new UpdateClient(trust,transport,(_,_,_)=>throw new Exception("bad download reached publisher check"));
 var valid=await download.CheckAsync("internal","0.8.4",131,default);
 await RejectAsync(()=>download.DownloadAsync(valid,Path.Combine(root,"invalid-downloads"),null,default),"short/long/incorrect-hash update is rejected");
}
using(var transport=new HttpClient(new FixtureHandler(uri=>uri.AbsolutePath.EndsWith(".json")?Sign(manifest):content)))
{
 var download=new UpdateClient(trust,transport,(_,_,_)=>throw new InvalidOperationException("untrusted publisher"));
 var valid=await download.CheckAsync("internal","0.8.4",131,default);
 await RejectAsync(()=>download.DownloadAsync(valid,Path.Combine(root,"bad-publisher"),null,default),"untrusted Authenticode result blocks installation");
}
await RejectAsync(()=>AuthenticodeVerifier.VerifyAsync(path,trust.PublisherSha256,default),"actual unsigned Windows file fails WinVerifyTrust");
Check(!Directory.EnumerateFiles(Path.Combine(root,"invalid-downloads"),"*.exe",SearchOption.AllDirectories).Any(),"failed downloads leave no executable");
using(var cancelHttp=new HttpClient(new FixtureHandler(uri=>uri.AbsolutePath.EndsWith(".json")?Sign(manifest):content)))
{
 var cancelClient=new UpdateClient(trust,cancelHttp,(_,_,_)=>Task.CompletedTask);
 var cancelManifest=await cancelClient.CheckAsync("internal","0.8.4",131,default);
 using var cancelled=new CancellationTokenSource();cancelled.Cancel();
 try{await cancelClient.DownloadAsync(cancelManifest,Path.Combine(root,"cancel-download"),null,cancelled.Token);throw new Exception("cancel ignored");}
 catch(OperationCanceledException){Check(true,"cancelled download cannot leave an install candidate");}
 Check(!Directory.EnumerateFiles(Path.Combine(root,"cancel-download"),"*",SearchOption.AllDirectories).Any(),"cancelled download removes its temporary bytes");
 var retried=await cancelClient.DownloadAsync(cancelManifest,Path.Combine(root,"cancel-download"),null,default);
 Check(File.ReadAllBytes(retried).SequenceEqual(content),"a cancelled download can be retried successfully");
}
var preferencePath=Path.Combine(root,"update-preferences.json");var preference=new UpdatePreferences();
Check(preference.Due(now)&&!preference.Succeeded(now).Due(now.AddHours(11)),"background checks wait twelve hours after success");
Check(!preference.Failed(now).Due(now.AddMinutes(4))&&preference.Failed(now).Due(now.AddMinutes(5)),"background network failures back off before retrying");
Check((preference with{AutoCheck=false}).Due(now)==false,"turning off automatic checks disables due work");
UpdatePreferencesFile.Write(preferencePath,preference with{AutoDownload=true});
Check(UpdatePreferencesFile.Read(preferencePath).AutoDownload,"automatic download preference survives restart independently of binding data");
File.WriteAllText(preferencePath,"{broken");Check(UpdatePreferencesFile.Read(preferencePath)==new UpdatePreferences(),"damaged update preferences recover without touching product settings");

var install=Path.Combine(root,"installed");var catalogRoot=Path.Combine(root,"installer");var transaction=Guid.NewGuid().ToString("N");
var backup=Path.Combine(catalogRoot,transaction,"backup");Directory.CreateDirectory(backup);
File.WriteAllText(Path.Combine(backup,".streamhub-install.json"),"{\"productId\":\"StreamHub.PC.0b1e5f94\",\"version\":\"0.8.3\"}");
File.WriteAllText(Path.Combine(backup,"app.exe"),"old program");
var catalog=new RollbackCatalog(catalogRoot,install);var record=catalog.Capture(transaction);catalog.Commit(record);
Check(catalog.Verify(catalog.Read())==backup,"rollback source must match protected transaction and full hashes");
File.WriteAllText(Path.Combine(backup,"app.exe"),"tampered program");Reject(()=>catalog.Read(),"modified backup cannot be restored");
File.WriteAllText(Path.Combine(backup,"app.exe"),"old program");
File.WriteAllText(Path.Combine(backup,"extra.exe"),"extra");Reject(()=>catalog.Read(),"unlisted extra executable invalidates backup");
File.Delete(Path.Combine(backup,"extra.exe"));
Reject(()=>catalog.Verify(record with{Transaction="..\\elsewhere"}),"rollback traversal rejected");
Reject(()=>new RollbackCatalog(catalogRoot,Path.Combine(root,"other-product")).Read(),"backup cannot target a different installation");
Reject(()=>catalog.Verify(record with{Files=record.Files.Select((f,i)=>i==0?f with{Path="..\\outside"}:f).ToArray()}),"manifest traversal rejected");
Check(catalog.Read().Transaction==transaction,"failed checks preserve usable rollback pointer");
Directory.CreateDirectory(install);
File.WriteAllText(Path.Combine(install,".streamhub-install.json"),"{\"productId\":\"StreamHub.PC.0b1e5f94\",\"version\":\"0.8.4\"}");
File.WriteAllText(Path.Combine(install,"app.exe"),"current program");
var settings=Path.Combine(root,"settings.json");File.WriteAllText(settings,"binding and personal settings");
var failed=false;
Reject(()=>RollbackTransaction.Execute(install,backup,Path.Combine(root,"failed-recovery"),()=>{},()=>{},restored=>
 {if(restored&&!failed){failed=true;throw new IOException("injected service start failure");}}),"failed activation restores the original installed files");
Check(File.ReadAllText(Path.Combine(install,"app.exe"))=="current program","failed rollback leaves current program usable");
RollbackTransaction.Execute(install,backup,Path.Combine(root,"success-recovery"),()=>{},()=>{},_=>{});
Check(File.ReadAllText(Path.Combine(install,"app.exe"))=="old program","actual file transaction restores previous version");
Check(File.ReadAllText(settings)=="binding and personal settings","rollback never alters external binding/settings data");
Reject(()=>RollbackTransaction.Execute(install,backup,Path.Combine(install,"nested-recovery"),()=>{},()=>{},_=>{}),"nested recovery cannot recursively consume an installation");
var clock=new FakeClock();var maintenance=new MaintenanceWindow(clock);
using(var active=maintenance.Enter())Reject(()=>maintenance.Begin(transaction,false),"active file operation prevents maintenance");
Reject(()=>maintenance.Begin(transaction,true),"active/preparing stream prevents maintenance");
maintenance.Begin(transaction,false);Reject(()=>maintenance.Enter(),"maintenance blocks new file operations");
Reject(()=>maintenance.Begin(Guid.NewGuid().ToString("N"),false),"another installer cannot steal maintenance");
maintenance.End(Guid.NewGuid().ToString("N"));Check(maintenance.Active,"wrong lease cannot cancel maintenance");
clock.Now=clock.Now.AddMinutes(3);using(var recovered=maintenance.Enter())Check(!maintenance.Active,"abandoned maintenance automatically expires");
Console.WriteLine($"{checks} delivery checks passed. Production certificate/feed tests remain pending.");

sealed class FixtureHandler(Func<Uri,byte[]> reply):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(reply(request.RequestUri!)),RequestMessage=request});}
sealed class FakeClock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
sealed class RedirectFixtureHandler:HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent([]),RequestMessage=new HttpRequestMessage(HttpMethod.Get,"https://another.example.test/feed.json")});}
sealed class StatusFixtureHandler(HttpStatusCode status,byte[] content):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(status){Content=new ByteArrayContent(content),RequestMessage=request});}
sealed class InspectFixtureHandler(Func<HttpRequestMessage,byte[]> reply):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(reply(request)),RequestMessage=request});}
