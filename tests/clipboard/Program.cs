using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;
using StreamHub.Service;

var checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
void Reject(Action action,string message){try{action();throw new Exception("Expected rejection: "+message);}catch(InvalidOperationException){Check(true,message);}}
var maximum=Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("😀",ClipboardPolicy.MaximumBytes/4)));
Check(ClipboardPolicy.Decode(ClipboardPolicy.Encode("text/plain",maximum)).SequenceEqual(maximum),"emoji round trip exactly 1 MiB");
Reject(()=>ClipboardPolicy.Encode("text/plain",maximum.Concat(new byte[]{65}).ToArray()),"1 MiB plus one rejected");
foreach(var bad in new[]{"Zh==","Zg==\n"," Zg==","wK8=","7aCA","====",""})Reject(()=>ClipboardPolicy.Decode(new("text/plain",bad)),"noncanonical Base64/malformed UTF8 rejected");
var html=Encoding.UTF8.GetBytes("<p>中文 😀 &amp; <b>格式</b></p>");
Check(ClipboardPolicy.Decode(ClipboardPolicy.Encode("text/html",html)).SequenceEqual(html),"HTML preserved without converting to plain text");
var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a3sAAAAASUVORK5CYII=");
Check(ClipboardPolicy.Decode(ClipboardPolicy.Encode("image/png",png)).SequenceEqual(png),"valid PNG round trip");
Reject(()=>ClipboardPolicy.Encode("image/png",new byte[24]),"malformed PNG rejected");
var huge=png.ToArray();huge[16]=1;Reject(()=>ClipboardPolicy.Encode("image/png",huge),"PNG pixel dimension limit enforced");
Reject(()=>ClipboardPolicy.Encode("application/octet-stream",new byte[]{1}),"unsupported clipboard format rejected");

var broker=new ClipboardBroker();var alpha=ClipboardPolicy.Encode("text/plain","alpha 中文 😀"u8.ToArray());var beta=ClipboardPolicy.Encode("text/html",html);
using var cancelled=new CancellationTokenSource();
var first=broker.RequestAsync("remote","lease-alpha","client-alpha","get",null,cancelled.Token);
var second=broker.RequestAsync("tablet","lease-beta","client-beta","set",beta,cancelled.Token);
var a=broker.Take()!;var b=broker.Take()!;
Check(a.LeaseId=="lease-alpha"&&a.ClientId=="client-alpha"&&b.LeaseId=="lease-beta"&&b.ClientId=="client-beta","queued clipboard jobs retain mode/device/session identity");
broker.Complete(a.Id,true,alpha,null);Check(!first.IsCompleted,"unclaimed result cannot mutate clipboard response");
Check(broker.Claim(a.Id)!=null&&broker.Claim(a.Id)==null,"each desktop job can be claimed once");
Check(broker.Claim("another-job")==null,"different request cannot claim clipboard content");
broker.Complete(a.Id,true,alpha,null);Check(await first==alpha&&!second.IsCompleted,"one job completion never leaks into another session");
Check(broker.Claim(b.Id)?.Payload==beta,"set carries its own exact HTML payload");broker.Complete(b.Id,true,null,null);Check(await second is null,"set completion contains no old clipboard content");
cancelled.Cancel();
using var abort=new CancellationTokenSource();var pending=broker.RequestAsync("remote","lease-cancel","client-cancel","get",null,abort.Token);var expired=broker.Take()!;abort.Cancel();
try{await pending;throw new Exception("cancellation ignored");}catch(OperationCanceledException){checks++;}
Check(broker.Claim(expired.Id)==null,"cancelled clipboard request cannot be replayed");

// Exercise the same agent coordinator used by the hidden STA helper, without
// changing this developer's clipboard or requiring the desktop GUI to be open.
var agentBroker=new ClipboardBroker();var desktop=alpha;var allowClaim=true;var reads=0;var writes=0;
var agentRuntime=new RuntimeClient((request,token)=> {
 RuntimeResponse response;
 if(request.Command=="clipboard-take")response=new(true,"",PayloadJson:JsonSerializer.Serialize(agentBroker.Take(request.RequestId),ClipboardPolicy.WireJsonOptions));
 else if(request.Command=="clipboard-claim")response=new(allowClaim && agentBroker.Claim(request.RequestId)!=null,"");
 else if(request.Command=="clipboard-complete"){
  agentBroker.Complete(request.RequestId,request.ClipboardSuccess==true,
    request.ClipboardFormat is not null && request.ClipboardData is not null?new(request.ClipboardFormat,request.ClipboardData):null,request.ClipboardError);
  response=new(true,"");
 }else throw new Exception("unexpected agent command");
 return Task.FromResult(response);
});
var agent=new ClipboardAgent(agentRuntime);
Task Dispatch(ClipboardJob job,CancellationToken token)=>agent.ExecuteAsync(job.Id,()=>{reads++;return desktop;},value=>{writes++;desktop=value;},token);
var fetched=await agentBroker.RequestAsync("remote","lease-agent","client-agent","get",null,default,Dispatch);
Check(fetched==alpha && reads==1 && writes==0,"on-demand agent fetches without a main window or polling timer");
try{
 await agentBroker.RequestAsync("remote","lease-empty","client-agent","get",null,default,(job,token)=>{
  agentBroker.Take(job.Id);agentBroker.Claim(job.Id);agentBroker.Complete(job.Id,false,null,"电脑剪贴板没有可用内容。");
  throw new InvalidOperationException("generic helper exit");
 });throw new Exception("empty clipboard accepted");
}catch(InvalidOperationException error){Check(error.Message=="电脑剪贴板没有可用内容。","helper process failure preserves its specific clipboard error");}
await agentBroker.RequestAsync("remote","lease-agent","client-agent","set",beta,default,Dispatch);
Check(desktop==beta && writes==1,"on-demand agent writes exactly the requested payload");
Check(await agent.ExecuteAsync("not-an-id",()=>throw new Exception(),_=>throw new Exception(),default)==1,"malformed helper request cannot reach clipboard");
using(var denied=new CancellationTokenSource()){
 allowClaim=false;
 var task=agentBroker.RequestAsync("remote","lease-ended","client-agent","get",null,denied.Token);
 var job=agentBroker.Take()!;
 // Return a delivered job to an agent whose live-session claim is denied.
 var deniedRuntime=new RuntimeClient((request,token)=>Task.FromResult(request.Command=="clipboard-take"
  ?new RuntimeResponse(true,"",PayloadJson:JsonSerializer.Serialize(job,ClipboardPolicy.WireJsonOptions)):new RuntimeResponse(false,"session ended")));
 Check(await new ClipboardAgent(deniedRuntime).ExecuteAsync(job.Id,()=>{reads++;return desktop;},_=>writes++,default)==1 && reads==1 && writes==1,
   "failed live-session claim never reads or writes clipboard");
 denied.Cancel();try{await task;}catch(OperationCanceledException){}
}
using(var failed=new CancellationTokenSource()){
 try{await agentBroker.RequestAsync("remote","lease-fail","client-agent","get",null,failed.Token,(_,_)=>throw new InvalidOperationException("launch failed"));}
 catch(InvalidOperationException){}
 Check(agentBroker.Take()==null,"failed helper launch removes its pending request");
}

var called=0;
var api=new ClipboardApi((mode,lease,client,token)=>
{if(mode!="remote"||lease!="lease-alpha"||client!="client-alpha")throw new InvalidOperationException("not this live session");return Task.CompletedTask;},
(mode,lease,client,action,payload,token)=>{called++;return Task.FromResult<ClipboardPayload?>(alpha);});
async Task<DefaultHttpContext> Request(string client,string mode="remote",string lease="lease-alpha",string? content=null)
{
 var context=new DefaultHttpContext();context.Request.ContentType="application/json";context.Response.Body=new MemoryStream();
 var body=content??JsonSerializer.Serialize(new{mode,leaseId=lease,clientId=client,action="get"});context.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(body));
 await api.HandleAsync(context,default);return context;
}
foreach(var client in new[]{"client-beta","client-unknown"})
{try{await Request(client);throw new Exception("another client accessed clipboard");}catch(InvalidOperationException){checks++;}}
try{await Request("client-alpha","tablet");throw new Exception("another mode accessed clipboard");}catch(InvalidOperationException){checks++;}
try{await Request("client-alpha",lease:"lease-expired");throw new Exception("expired lease accessed clipboard");}catch(InvalidOperationException){checks++;}
Check(called==0,"unauthorized device/mode/lease never reaches desktop broker");
await Request("client-alpha");Check(called==1,"authorized live session reaches broker once");
var maxPayload=ClipboardPolicy.Encode("text/plain",maximum);
var maxApi=new ClipboardApi((_,_,_,_)=>Task.CompletedTask,(_,_,_,_,_,_)=>Task.FromResult<ClipboardPayload?>(maxPayload));
var maxContext=new DefaultHttpContext();maxContext.Request.ContentType="application/json";
maxContext.Request.Body=new MemoryStream("{\"mode\":\"remote\",\"leaseId\":\"lease-alpha\",\"clientId\":\"client-alpha\",\"action\":\"get\"}"u8.ToArray());
maxContext.Response.Body=new MemoryStream();await maxApi.HandleAsync(maxContext,default);
Check(maxContext.Response.Body.Length<=ClipboardPolicy.MaximumEnvelopeBytes,"1 MiB emoji response fits the actual HTTP wire envelope");
maxContext.Response.Body.Position=0;
using(var response=await JsonDocument.ParseAsync(maxContext.Response.Body))Check(response.RootElement.GetProperty("payload").GetProperty("data").GetString()==maxPayload.Data,"maximum-size HTTP clipboard response preserves exact data");
var longContext=new DefaultHttpContext();longContext.Request.ContentType="application/json";longContext.Request.Body=new MemoryStream(new byte[ClipboardPolicy.MaximumEnvelopeBytes+1]);
try{await api.HandleAsync(longContext,default);throw new Exception("oversize chunked JSON accepted");}catch(InvalidOperationException){checks++;}
Console.WriteLine($"{checks} clipboard boundary/isolation checks passed. System clipboard was not modified.");
