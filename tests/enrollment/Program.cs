using System.Net;
using System.Text;
using System.Text.Json;
using StreamHub.Host.Runtime;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message) { try { action(); throw new Exception("Expected rejection: " + message); } catch (InvalidOperationException) { Check(true, message); } }
var clock = new Clock(); var registry = new LocalBindingRegistry(new string('A',64), clock);
const string client = "client-fixture-1234", nonce = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
var start = registry.Start();
Check(start.Code?.Length == 6 && start.Code.All(char.IsAsciiDigit), "six digits retain leading zeros");
var request = registry.Begin(start.Code!, client, "中文平板", nonce);
Check(request.State == "pending" && request.Invitation is null, "correct code never releases credentials before owner decision");
Check(registry.Snapshot().Code is null, "redemption consumes the code before approval");
Reject(() => registry.Begin(start.Code!, client, "other", nonce), "a redeemed code cannot authorize another device");
Reject(() => registry.Decide("another-request",true), "owner approval must target exact request");
registry.Decide(request.RequestId,true);
Reject(() => registry.Poll(request.RequestId,new string('B',64),()=>"secret"), "different poll nonce cannot steal the approved grant");
var result = registry.Poll(request.RequestId,nonce,()=>"secret");
Check(result.Invitation == "secret" && result.State == "approved", "approved exact request receives one grant");
Reject(() => registry.Poll(request.RequestId,nonce,()=>"secret"), "grant replay is rejected");
Check(!JsonSerializer.Serialize(registry.Snapshot()).Contains("secret"), "owner status never includes binding credential");
start = registry.Start(); request = registry.Begin(start.Code!,client,"test",nonce); registry.Decide(request.RequestId,false);
Check(registry.Poll(request.RequestId,nonce,()=>throw new Exception()).State == "rejected", "rejected request cannot read invitation");
start=registry.Start(); clock.Advance(TimeSpan.FromMinutes(3));
Reject(() => registry.Begin(start.Code!,client,"test",nonce), "expired code rejected");
start=registry.Start(); request=registry.Begin(start.Code!,client,"test",nonce); clock.Advance(TimeSpan.FromSeconds(91));
Reject(() => registry.Decide(request.RequestId,true), "late owner approval cannot resurrect an expired request");
start=registry.Start(); var wrong=start.Code=="000000"?"000001":"000000";
for(var i=0;i<5;i++)Reject(()=>registry.Begin(wrong,client,"test",nonce),"incorrect attempt rejected");
Reject(()=>registry.Begin(start.Code!,client,"test",nonce),"five incorrect attempts close the window");
start=registry.Start(); registry.Cancel(); Reject(()=>registry.Begin(start.Code!,client,"test",nonce),"owner cancel revokes window");
registry.Start(); Reject(()=>registry.Begin("123456","../unsafe","test",nonce),"invalid client ID rejected before guessing code");
var packet=LanDiscoveryPacket.Response(Guid.NewGuid().ToString(),"中文电脑",new string('A',64),IPAddress.Parse("192.168.1.2"));
Check(packet.Length<1024 && (packet[2]&128)!=0 && packet[7]==4,"bounded standard DNS-SD response contains four answers");
Check(!Encoding.UTF8.GetString(packet).Contains("token",StringComparison.OrdinalIgnoreCase),"discovery contains no binding token");
Check(!LanDiscoveryPacket.IsQuery(packet),"responses cannot trigger response loops");
Check(!LanDiscoveryPacket.IsLocalAddress(IPAddress.Parse("100.89.1.2")) && !LanDiscoveryPacket.IsLocalAddress(IPAddress.Parse("8.8.8.8")),"Tailscale and public addresses excluded from LAN enrollment");
Check(!LanDiscoveryPacket.IsLocalAddress(IPAddress.Parse("169.254.2.1")),"link-local address follows the existing usable-route policy on both ends");
var query=new List<byte>(new byte[12]); query[5]=1;
foreach(var label in LanDiscoveryPacket.Service.Split('.')) { var bytes=Encoding.ASCII.GetBytes(label);query.Add((byte)bytes.Length);query.AddRange(bytes); }
query.AddRange(new byte[]{0,0,12,0,1});
Check(LanDiscoveryPacket.IsQuery(query.ToArray()),"standard PTR question is accepted");
Check(!LanDiscoveryPacket.WantsUnicast(query.ToArray()),"standard NSD question retains multicast response routing");
var qu=query.ToArray();qu[^2]=0x80;
Check(LanDiscoveryPacket.IsQuery(qu)&&LanDiscoveryPacket.WantsUnicast(qu),"QU question requests a direct reply");
var otherClass=query.ToArray();otherClass[^1]=3;
Check(!LanDiscoveryPacket.IsQuery(otherClass),"non-IN DNS class cannot trigger an advertisement");
var partial=query.ToArray();partial[5]=2;
Check(!LanDiscoveryPacket.IsQuery(partial),"a valid first question does not hide a truncated second question");
var legacy=query.ToArray();legacy[0]=0x12;legacy[1]=0x34;
var direct=LanDiscoveryPacket.Response("11111111-2222-3333-4444-555555555555","TESTPC",new string('A',64),IPAddress.Parse("10.1.2.3"),legacy);
Check(direct[0]==0x12&&direct[1]==0x34&&direct[5]==1&&direct.AsSpan(12,legacy.Length-12).SequenceEqual(legacy.AsSpan(12)),
 "ephemeral-port reply echoes the legacy transaction and question");
Check(!LanDiscoveryPacket.IsQuery(direct),"direct reply cannot trigger a discovery loop");
var traffic = new LanDiscoveryTraffic();
var lan = IPAddress.Parse("10.1.2.3");
var other = query.ToArray(); other[13] = (byte)'x';
Check(!traffic.Accept(query.ToArray(),IPAddress.Parse("100.89.1.2"),""),"source rejection happens after raw receive accounting");
Check(!traffic.Accept(direct,lan,""),"response is counted separately from a malformed query");
Check(!traffic.Accept(other,lan,""),"another DNS-SD service is observed but not answered");
Check(!traffic.Accept(partial,lan,""),"malformed packet is observed before rejection");
Check(traffic.Accept(query.ToArray(),lan,""),"valid packet reaches reply policy");
Check(traffic.Received==5 && traffic.OutsideLan==1 && traffic.Responses==1 && traffic.OtherService==1 && traffic.Malformed==1,
 "zero accepted queries can no longer masquerade as zero packets on the socket");
if(args.Length==1){Directory.CreateDirectory(args[0]);File.WriteAllText(Path.Combine(args[0],"windows-direct-response.base64"),Convert.ToBase64String(direct));}
for(var i=0;i<query.Count;i++)Check(!LanDiscoveryPacket.IsQuery(query.Take(i).ToArray()),"truncated DNS question rejected");
var random=new Random(71);
for(var i=0;i<2000;i++){var bytes=new byte[random.Next(0,160)];random.NextBytes(bytes);_=LanDiscoveryPacket.IsQuery(bytes);}
Console.WriteLine($"{checks} enrollment/discovery checks passed; 2000 malformed packets exercised.");

sealed class Clock : TimeProvider { private DateTimeOffset now=DateTimeOffset.Parse("2026-11-10T00:00:00Z");public override DateTimeOffset GetUtcNow()=>now;public void Advance(TimeSpan interval)=>now+=interval; }

// The wire port is fixed; this project tests the policy/packet code without opening sockets.
namespace StreamHub.Host.Runtime { public static class ControlApiServer { public const int Port=49199; } }
