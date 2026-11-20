using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using StreamHub.Host.Runtime;
using StreamHub.Service;

if (args.Length == 4 && args[0] == "--media") { await AssistanceMediaInterop.RunAsync(args[1], args[2], args[3]); return; }
if (args.Length == 2 && args[0] == "--input-probe") { InputProbe.Run(args[1]); return; }
if (args.Length == 2) { await ReceiverInterop.RunAsync(args[0], args[1]); return; }

int passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
void Refuses(Action action, string label) { try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or UnauthorizedAccessException or IOException) { Check(true, label); return; } throw new Exception(label); }
using var key = RSA.Create(2048);
using var certificate = new CertificateRequest("CN=isolated-assistance-check", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
string encoded = Convert.ToBase64String(certificate.RawData), proof = new('A', 64);
byte[]? journal = null;
var events = new List<(string Action, string? Mode)>();
var clock = new TestClock();
var registry = new AssistanceRegistry(bytes => journal = bytes, (action, _, _, mode) => events.Add((action, mode)), clock: clock);
var offered = registry.Start();
Check(offered.Code!.Length == 9 && offered.Code[4] == '-', "eight-character assistance code is distinct from six-digit binding");
for (int i = 0; i < 5; i++) Refuses(() => registry.Join("0000-0000", "peer", proof, encoded, "fixture"), "wrong code refuses " + i);
Check(registry.Snapshot().State == "expired", "fifth failure invalidates offer");
offered = registry.Start(); clock.Advance(TimeSpan.FromMinutes(10));
Refuses(() => registry.Join(offered.Code!, "peer", proof, encoded, "fixture"), "expired code refuses");
offered = registry.Start();
var pending = registry.Join(offered.Code!, "peer", proof, encoded, "fixture");
Check(pending.State == "pending" && pending.Mode is null && pending.ServerCertificate is null, "code redemption grants no media or input capability");
Check(journal is not null && !System.Text.Encoding.UTF8.GetString(journal).Contains(proof), "durable journal has no bearer proof");
Check(registry.Snapshot().Comparison == pending.Comparison, "both ends display same comparison");
Refuses(() => registry.Start(), "single session reservation");
Refuses(() => registry.Join(offered.Code!, "peer2", proof, encoded, "fixture"), "consumed code cannot create second request");
Refuses(() => registry.Heartbeat(pending.SessionId, proof), "pending credential cannot heartbeat as active");
Refuses(() => registry.Poll(pending.SessionId, new string('B', 64)), "wrong session proof refuses");
Refuses(() => registry.Approve(pending.SessionId, "default"), "no default or unspecified control mode");
registry.Approve(pending.SessionId, "view"); registry.Activate();
Check(registry.Poll(pending.SessionId, proof).Mode == "view", "actual view mode is returned");
Check(events.Contains(("owner-approved", "view")), "audit records actual owner mode");
Refuses(() => registry.Approve(pending.SessionId, "control"), "peer cannot escalate active mode");
registry.End("fixture-ended");
Refuses(() => registry.Poll(pending.SessionId, proof), "end revokes credential immediately before cleanup");
Refuses(() => registry.Start(), "pending cleanup blocks next session");
var recovered = new AssistanceRegistry(bytes => journal = bytes, (_, _, _, _) => { }, journal);
Check(recovered.Snapshot().State == "cleanup-pending", "restart never resumes a credential or an approved session");
Refuses(() => recovered.Start(), "startup requires cleanup before a new code");
recovered.Cleaned(); Check(recovered.Start().State == "offered", "successful cleanup releases reservation");
registry.Cleaned();
offered = registry.Start(); pending = registry.Join(offered.Code!, "peer", proof, encoded, "fixture");
registry.Approve(pending.SessionId, "control"); registry.Activate(); clock.Advance(TimeSpan.FromSeconds(26));
Refuses(() => registry.Heartbeat(pending.SessionId, proof), "lost peer heartbeat revokes and cannot resurrect");
Check(registry.Snapshot().State == "cleanup-pending", "lost peer queues cleanup");
registry.Cleaned(); offered = registry.Start(); pending = registry.Join(offered.Code!, "peer", proof, encoded, "fixture");
registry.EndRemote(pending.SessionId, proof); Check(registry.Snapshot().State == "cleanup-pending", "waiting peer can cancel without owner authorization");
registry.Cleaned(); offered = registry.Start(); pending = registry.Join(offered.Code!, "peer", proof, encoded, "fixture");
clock.Advance(TimeSpan.FromSeconds(91));
Refuses(() => registry.Poll(pending.SessionId, proof), "unconfirmed request expires without media authorization");
Check(registry.Snapshot().State == "cleanup-pending", "pending timeout still requires exact identity cleanup");
registry.Cleaned(); offered = registry.Start(); pending = registry.Join(offered.Code!, "peer", proof, encoded, "fixture");
registry.Approve(pending.SessionId, "control"); registry.Activate();
for (int i = 0; i < 359; i++) { clock.Advance(TimeSpan.FromSeconds(20)); registry.Heartbeat(pending.SessionId, proof); }
clock.Advance(TimeSpan.FromSeconds(20));
Refuses(() => registry.Heartbeat(pending.SessionId, proof), "heartbeats cannot extend authorization beyond two hours");
bool failSave = true;
var diskFailure = new AssistanceRegistry(_ => { if (failSave) throw new IOException("fixture disk failure"); }, (_, _, _, _) => { });
var diskOffer = diskFailure.Start();
Refuses(() => diskFailure.Join(diskOffer.Code!, "peer", proof, encoded, "fixture"), "journal failure cannot redeem code");
Check(diskFailure.Snapshot().State == "offered" && diskFailure.Snapshot().SessionId is null, "journal failure leaves no pending or authorized session");
failSave = false; var diskPending = diskFailure.Join(diskOffer.Code!, "peer", proof, encoded, "fixture"); failSave = true;
Refuses(() => diskFailure.Approve(diskPending.SessionId, "control"), "mode journal failure cannot grant control");
Check(diskFailure.Snapshot().State == "pending" && diskFailure.Snapshot().Mode is null, "failed approval remains unapproved");
diskFailure.End("fixture-ended");
Refuses(() => diskFailure.Cleaned(), "failed cleanup journal retains recovery reservation");
Refuses(() => diskFailure.Start(), "failed cleanup cannot be replaced by a new session");
failSave = false; diskFailure.Cleaned();
string fixture = args.Length == 1 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass a new isolated fixture directory.");
if (Directory.Exists(fixture)) throw new IOException("Use a new fixture directory.");
Directory.CreateDirectory(fixture);
var media = new AssistanceMediaProfile(fixture);
var session = new AssistanceJournal(1, Guid.NewGuid().ToString("D"), encoded, "peer", "view");
await media.PrepareAsync(session, default);
string config = await File.ReadAllTextAsync(media.ConfigPath);
Check(new[] { "keyboard = disabled", "mouse = disabled", "native_pen_touch = disabled", "controller = disabled" }.All(config.Contains), "host view profile disables every supported input family");
Check(!config.Contains("profiles\\remote") && !config.Contains("profiles\\tablet"), "assistance does not reuse trusted profile state");
string foreign = Path.Combine(fixture, "trusted-state.json"); await File.WriteAllTextAsync(foreign, "untouched");
await media.CleanupAsync(session, default);
using (var after = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(media.DirectoryPath, "state.json"))))
    Check(after.RootElement.GetProperty("root").GetProperty("named_devices").GetArrayLength() == 0, "cleanup reads back empty assistance pairing");
Check(await File.ReadAllTextAsync(foreign) == "untouched", "unrelated trusted state is preserved");
Check(!File.Exists(Path.Combine(media.DirectoryPath, "cakey.pem")), "temporary server key removed");
await media.PrepareAsync(session with { Mode = "control" }, default);
config = await File.ReadAllTextAsync(media.ConfigPath);
Check(config.Contains("keyboard = enabled") && config.Contains("mouse = enabled"), "owner-selected control has host input enabled");
try { await media.CleanupAsync(session with { SessionId = Guid.NewGuid().ToString("D") }, default); throw new Exception("foreign cleanup accepted"); }
catch (InvalidDataException) { Check(true, "foreign session pairing is not removed by name or unpair-all"); }
await media.CleanupAsync(session, default);
string info = "icontrol-help:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new AssistanceConnection(1, ["192.168.1.10"], 49199, new string('A', 64)), RuntimeProtocol.JsonOptions));
Check(AssistanceClient.ParseConnection(info).Addresses[0] == "192.168.1.10", "connection information preserves explicit pinned address");
Refuses(() => AssistanceClient.ParseConnection("https://example.test"), "arbitrary URL cannot supply assistance trust");
string nullPin = "icontrol-help:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"addresses\":[\"192.168.1.10\"],\"port\":49199,\"pin\":null}"));
Refuses(() => AssistanceClient.ParseConnection(nullPin), "null certificate pin is refused as invalid connection information");
await AssistanceTrustChecks.RunAsync(Check, Refuses, encoded, proof, fixture);
Console.WriteLine($"{passed} targeted assistance checks passed. No actual media, input or crash acceptance performed.");

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
