using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;

// Exercise the actual handler against memory contexts. No sockets, named pipes,
// service calls, installed state or persisted certificates are opened.
int checks=0;
void Check(bool value,string label) {if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
using var key=RSA.Create(2048);
using var cert=new CertificateRequest("CN=Isolated binding API",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
var pin=Convert.ToHexString(SHA256.HashData(cert.RawData));
var registry=new LocalBindingRegistry(pin);
var api=new ControlApiServer(new RuntimeClient((_,_)=>throw new Exception("Unexpected service call")),
    (_,_,_)=>throw new Exception("Unexpected display change"),new ControlApiIdentity([],[]),X509KeyStorageFlags.EphemeralKeySet,
    discoveryDeviceId:"11111111-2222-3333-4444-555555555555");
void Set(string field,object value)=>typeof(ControlApiServer).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(api,value);
Set("_localBinding",registry);Set("_certificate",cert);Set("_invitation","fixture-grant");
typeof(ControlApiServer).GetProperty("IsRunning")!.SetValue(api,true);
async Task<(int Status,JsonElement Body)> Request(string path="/v1/binding/identity",string source="192.168.1.4",string method="GET",object? body=null,string query="") {
    var context=new DefaultHttpContext();context.Connection.RemoteIpAddress=IPAddress.Parse(source);
    context.Request.Path=path;context.Request.Method=method;context.Request.QueryString=new QueryString(query);
    if(body is not null){byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(body);context.Request.Body=new MemoryStream(bytes);context.Request.ContentLength=bytes.Length;context.Request.ContentType="application/json";}
    context.Response.Body=new MemoryStream();
    await (Task)typeof(ControlApiServer).GetMethod("HandleAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(api,[context])!;
    context.Response.Body.Position=0;using var json=await JsonDocument.ParseAsync(context.Response.Body);
    return(context.Response.StatusCode,json.RootElement.Clone());
}
Check((await Request()).Status==409,"address identity unavailable until owner opens a code window");
var window=registry.Start();var identity=await Request();
Check(identity.Status==200 && identity.Body.GetProperty("result").GetProperty("pin").GetString()==pin,"bootstrap returns the current TLS certificate identity");
Check(!identity.Body.GetRawText().Contains(window.Code!)&&!identity.Body.GetRawText().Contains("fixture-grant"),"bootstrap exposes neither short code nor binding grant");
foreach(var source in new[]{"8.8.8.8","100.89.1.2","127.0.0.1"})Check((await Request(source:source)).Status==403,"non-LAN enrollment source denied: "+source);
Check((await Request(source:"::ffff:192.168.1.5")).Status==200,"IPv4-mapped LAN address is normalized");
Check((await Request(query:"?code=123456")).Status==403,"query-string credentials cannot reach enrollment");
Check((await Request(method:"PUT")).Status==404,"unsupported enrollment method rejected");
const string nonce="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
var begin=await Request("/v1/binding/request",method:"POST",body:new{code=window.Code,clientId="tablet-fixture",name="中文平板",nonce});
var requestId=begin.Body.GetProperty("result").GetProperty("requestId").GetString()!;
Check(begin.Status==200&&!begin.Body.GetRawText().Contains("fixture-grant"),"valid code creates only a pending request");
Check((await Request()).Status==409,"identity endpoint closes when one-use code is consumed");
var pending=await Request("/v1/binding/poll",method:"POST",body:new{requestId,nonce});
Check(pending.Body.GetProperty("result").GetProperty("state").GetString()=="pending","no binding grant before explicit owner approval");
registry.Decide(requestId,true);
var approved=await Request("/v1/binding/poll",method:"POST",body:new{requestId,nonce});
Check(approved.Body.GetProperty("result").GetProperty("invitation").GetString()=="fixture-grant","approved exact request receives its binding grant");
registry.Cancel();Check((await Request()).Status==409,"cancelled code window cannot expose identity");
Console.WriteLine($"{checks} isolated binding API checks passed.");
