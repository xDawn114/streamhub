using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using StreamHub.Host.Runtime;

// Exercise the compiled production command, without construction or access to
// installed service, network process, profile, binding, account or desktop.
var type = typeof(RuntimeRequest).Assembly.GetType("StreamHub.Service.HostRuntime", true)!;
var runtime = RuntimeHelpers.GetUninitializedObject(type);
using var gate = new SemaphoreSlim(0, 1);
void Set(string name, object? value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);
Set("_gate", gate);
Set("_maintenance",new MaintenanceWindow());
using var networkGate = new SemaphoreSlim(1, 1);
Set("_networkGate", networkGate);
var handle = type.GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
Task<RuntimeResponse> Send(string command = "network-provider", CancellationToken token = default) =>
    (Task<RuntimeResponse>)handle.Invoke(runtime, [new RuntimeRequest(command, Provider: "lan"), token])!;
var checks = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; Console.WriteLine("PASS " + reason); }
foreach (var command in new[] { "network-provider", "NETWORK-PROVIDER" })
{
    var watch = Stopwatch.StartNew();
    var response = await Send(command).WaitAsync(TimeSpan.FromSeconds(1));
    Check(response.ErrorStatus == 409 && !response.Success, "busy runtime rejects provider change");
    Check(watch.Elapsed < TimeSpan.FromSeconds(1) && gate.CurrentCount == 0, "provider does not wait for or release another request's gate");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await Send(token: cancelled.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(gate.CurrentCount == 0, "cancelled provider admission does not affect runtime gate"); }
}
gate.Release();
var remote = new LiveSessionState(); remote.Prepare("fixture-lease", "fixture-client", DateTimeOffset.UtcNow);
Set("_remoteSession", remote);
var result = await Send();
Check(!result.Success && result.ErrorStatus == 409, "active main-screen lease blocks provider mutation before touching daemon");
Check(gate.CurrentCount == 1, "rejected active main-screen change releases owned gate");
remote.Reset(); Set("_tabletLeaseId", "fixture-tablet-lease");
result = await Send();
Check(!result.Success && result.ErrorStatus == 409 && gate.CurrentCount == 1, "active secondary-display lease blocks provider mutation");
Set("_tabletLeaseId", null);
var root = Path.GetFullPath(args.Single());
if (!System.Text.RegularExpressions.Regex.IsMatch(root,@"^D:\\Deepseek\\build\\stage\d+-tests\\providers(?:\\|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new Exception("Unexpected fixture root");
var vault = new FileTransferVault(Path.Combine(root, "queue"), throttle: false);
var key = new string('a', 64);
Directory.CreateDirectory(Path.Combine(root, "received"));
vault.Register("fixture-device", "fixture", key); vault.Configure(true, Path.Combine(root, "received")); vault.Grant("fixture-device", true);
vault.CreateUpload("fixture-device", key, "fixture-empty.bin", 0, new string('0', 64));
Set("_files", vault);
result = await Send();
Check(!result.Success && result.ErrorStatus == 409 && gate.CurrentCount == 1, "unfinished file task blocks provider mutation");
// A stalled network operation must not own media admission, and a media
// operation must not block independent network preference reads.
await networkGate.WaitAsync();
var mediaResponse = await ((Task<RuntimeResponse>)handle.Invoke(runtime,
    [new RuntimeRequest("media-identity", Profile: "remote", ClientCertificate: "bad"), CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(1));
Check(mediaResponse.ErrorStatus == 400 && gate.CurrentCount == 1 && networkGate.CurrentCount == 0,
    "network operation leaves media admission and certificate validation available");
networkGate.Release();
Set("_transportSettings", new StreamHub.Host.Networking.TransportSettingsStore(Path.Combine(root, "network-preferences")));
await gate.WaitAsync();
var configResponse = await Send("network-config").WaitAsync(TimeSpan.FromSeconds(1));
Check(configResponse.Success && gate.CurrentCount == 0 && networkGate.CurrentCount == 1,
    "occupied media gate does not block network configuration reads");
gate.Release();
// A delayed transition to Running must replace the startup-time login hint.
using (var networkClient = (StreamHub.Host.Networking.EmbeddedNetworkClient)typeof(StreamHub.Host.Networking.EmbeddedNetworkClient)
    .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(HttpClient)], null)!
    .Invoke([new HttpClient(new ReadyNetworkApi()) { BaseAddress = new Uri("http://memory/localapi/v0/") }]))
{
    Set("_networkClient", networkClient); Set("_networkMessage", "组网已启动，等待账号登录");
    await gate.WaitAsync();
    var response = await Send("network-status").WaitAsync(TimeSpan.FromSeconds(1));
    var observed = System.Text.Json.JsonSerializer.Deserialize<StreamHub.Host.Networking.NetworkStatus>(response.PayloadJson!, RuntimeProtocol.JsonOptions)!;
    Check(response.Success && observed.LoggedIn && response.Message == observed.StateText,
        "a live logged-in status replaces the earlier startup login message");
    var snapshot = type.GetField("_networkSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
    Check((string)snapshot.GetType().GetProperty("Message")!.GetValue(snapshot)! == observed.StateText,
        "ordinary runtime snapshot receives the same observed network message");
    Check(gate.CurrentCount == 0 && networkGate.CurrentCount == 1,
        "network status publication does not acquire or release media admission");
    gate.Release();
}
Console.WriteLine($"{checks} provider runtime checks passed; uninitialized production object and isolated fixture directory only.");

sealed class ReadyNetworkApi : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var body = request.RequestUri!.AbsolutePath.EndsWith("/prefs") ? "{\"ControlURL\":\"https://controlplane.tailscale.com\"}" :
            "{\"BackendState\":\"Running\",\"TailscaleIPs\":[\"100.70.1.2\"],\"Self\":{\"UserID\":123},\"Peer\":{}}";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
