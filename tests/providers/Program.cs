using System.Net;
using System.Text;
using System.Text.Json;
using StreamHub.Host.Networking;

var checks = 0;
void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); checks++; Console.WriteLine("PASS " + reason); }
void Reject(Action action, string reason)
{
    try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Check(true, reason); return; }
    throw new Exception("Expected rejection: " + reason);
}
async Task RejectAsync(Func<Task> action, string reason)
{
    try { await action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Check(true, reason); return; }
    throw new Exception("Expected rejection: " + reason);
}
var official = new TransportSettings().Validate();
var self = new TransportSettings("self-hosted", "https://vpn.example.com:8443").Validate();
Check(HubTransports.All.Length == 4 && official.Provider == "tailscale", "four providers; missing preference defaults to official");
Reject(() => new TransportSettings("unknown").Validate(), "unknown provider is rejected");
foreach (var url in new[] { "http://vpn.example.com", "https://u:p@vpn.example.com", "https://@vpn.example.com", "https://vpn.example.com/a",
    "https://vpn.example.com/a/..", "https://vpn.example.com/?key=x", "https://vpn.example.com/#fragment", "https://vpn.example.com\\@evil.test", "https://vpn.example.com\n", "https://vpn.example.com:0", "https:////vpn.example.com" })
    Reject(() => HubTransports.ServerUrl(url), "reject invalid server " + JsonSerializer.Serialize(url));
Check(HubTransports.ServerUrl("https://VPN.EXAMPLE.COM:443/") == "https://vpn.example.com", "server root and default port normalize");
Check(HubTransports.AuthorizationUrl(official, "https://login.tailscale.com/a").Length > 0, "official authorization accepted");
Check(HubTransports.AuthorizationUrl(self, "https://vpn.example.com:8443/register/node").Length > 0, "exact self hosted origin accepted");
foreach (var url in new[] { "https://vpn.example.com/a", "https://sub.vpn.example.com:8443/a", "https://vpn.example.com.evil.test:8443/a",
    "https://u@vpn.example.com:8443/a", "http://vpn.example.com:8443/a", "https://login.tailscale.com/a" })
    Check(HubTransports.AuthorizationUrl(self, url).Length == 0, "self hosted auth rejects foreign or changed origin");
foreach (var url in new[] { "https://evil.tailscale.com.evil.test/a", "https://tailscale.com:444/a", "https://u@tailscale.com/a", "https://vpn.example.com/a" })
    Check(HubTransports.AuthorizationUrl(official, url).Length == 0, "official auth rejects foreign or credential URL");
foreach (var value in new[] { "", ",,,", "192.168.1.2,evil.test", "192.168.1.2:49199", "127.0.0.1", "192.168.01.2", "224.1.1.1" })
    Reject(() => new TransportSettings("manual", ManualAddresses: value).Validate(), "invalid manual address rejected");
Reject(() => HubTransports.ManualAddresses(string.Join(",", Enumerable.Range(1, 9).Select(i => "192.168.1." + i))), "more than eight manual hosts rejected");
var manual = new TransportSettings("manual", ManualAddresses: "192.168.1.30,100.71.1.2,192.168.1.30").Validate();
Check(manual.ManualAddresses == "192.168.1.30,100.71.1.2", "manual addresses deduplicated");
Check(manual.Transport.Addresses(["192.168.1.99"], manual).SequenceEqual(["192.168.1.30", "100.71.1.2"]), "manual publishing never uses discovered addresses");
var lan = new TransportSettings("lan").Validate();
Check(lan.Transport.Addresses(["100.70.1.2", "192.168.1.20"], lan).SequenceEqual(["192.168.1.20"]), "local publishing excludes tailnet");
foreach (var local in new[] { lan, manual })
    Check(!local.Transport.UsesAccount && HubTransports.AuthorizationUrl(local, "https://login.tailscale.com/a").Length == 0, "local providers have no cloud login");

var testRoot = Path.GetFullPath(args.Single());
if (!System.Text.RegularExpressions.Regex.IsMatch(testRoot,
    @"^D:\\Deepseek\\build\\stage\d+-tests(?:\\|$)",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new Exception("Unexpected test root");
var storeRoot = Path.Combine(testRoot, "preferences");
var store = new TransportSettingsStore(storeRoot);
Check(store.Load() == official && !Directory.Exists(storeRoot), "default load makes no disk writes");
store.Save(self); Check(store.Load() == self, "atomic preferences round trip self hosted provider");
Check(Directory.GetFiles(storeRoot, "*.tmp").Length == 0, "successful save leaves no temporary preferences");
Reject(() => store.Save(new("self-hosted", "http://bad.test")), "invalid save rejected before write");
Check(store.Load() == self, "failed save keeps old configuration");
foreach (var malformed in new[] { "{", "{}", "[]", "null", "{\"Provider\":\"unknown\",\"ServerUrl\":\"\",\"ManualAddresses\":\"\"}", new string('x', 4097) })
{
    File.WriteAllText(Path.Combine(storeRoot, "transport.json"), malformed);
    Reject(() => store.Load(), "corrupt persisted configuration does not revert to official");
}
store.Save(lan); Check(store.Load() == lan, "explicit save repairs invalid configuration");

var api = new FakeApi { LoggedIn = true };
using var client = new EmbeddedNetworkClient(new HttpClient(api) { BaseAddress = new("http://memory/localapi/v0/") });
await RejectAsync(() => client.ConfigureAsync(self, default), "switching control server while logged in is refused");
Check(api.Mutations.Count == 0, "refused switch performs no mutation or logout");
api.ControlUrl = "https://login.tailscale.com";
await client.ConfigureAsync(official, default);
Check(api.ControlUrl == "https://login.tailscale.com" && api.Mutations.Count == 1, "explicit pause on official alias preserves account without rebuilding client");
api.Mutations.Clear();
api.LoggedIn = false;
await client.ConfigureAsync(self, default);
Check(api.ControlUrl == self.ControlUrl && api.Mutations.Count == 2 && api.Mutations[1].Path == "start", "logged out switch patches server then rebuilds controlclient");
using (var patch = JsonDocument.Parse(api.Mutations.First().Body!))
{
    Check(patch.RootElement.EnumerateObject().Count() == 4, "control switch edits only server and pause fields");
    Check(patch.RootElement.GetProperty("ControlURLSet").GetBoolean() && !patch.RootElement.GetProperty("WantRunning").GetBoolean(), "control URL is masked; network paused");
}
api.LoggedIn = true;
api.AuthUrl = "https://vpn.example.com:8443/register/node";
var status = await client.GetStatusAsync(settings: self);
Check(status.LoggedIn && status.BrowseUrl == api.AuthUrl && status.Transport == self, "status carries provider and permitted authorization");
api.AuthUrl = "https://login.tailscale.com/a";
status = await client.GetStatusAsync(settings: self);
Check(status.BrowseUrl.Length == 0, "self hosted status cannot surface stale official login URL");
await client.ConfigureAsync(lan, default);
Check(api.ControlUrl == self.ControlUrl && api.LoggedIn, "local pause preserves identity and control server");
var calls = api.Calls.Count;
status = await client.GetStatusAsync(settings: lan);
Check(status.State == "Local" && status.Peers.Length == 0 && !status.LoggedIn && api.Calls.Count == calls, "local status never contacts the daemon");
await client.ConfigureAsync(self, default);
Check(api.ControlUrl == self.ControlUrl, "same server can resume using the saved identity");
api.ControlUrl = "https://other.example.com";
var writes = api.Mutations.Count;
await RejectAsync(() => client.SetRunningAsync(true, settings: self), "running is blocked when daemon and settings disagree");
await RejectAsync(() => client.BeginLoginAsync(settings: self), "interactive login is blocked on a mismatched server");
await RejectAsync(() => client.GetStatusAsync(settings: self), "mismatched server cannot surface peers or authorization");
Check(api.Mutations.Count == writes, "mismatched actions perform no writes");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await client.ConfigureAsync(official, cancelled.Token); throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { Check(api.Mutations.Count == writes, "cancellation before server change performs no writes"); }
api.ControlUrl = HubTransports.OfficialUrl; api.LoggedIn = true; api.CurrentId = "current"; api.Mutations.Clear();
await client.ResumeAsync(official, "fixture-owner", default);
Check(api.LoggedIn && api.Mutations.Count == 1 && api.Mutations[0].Method == "PATCH", "ordinary resume never pauses or restarts a saved account");
using (var patch = JsonDocument.Parse(api.Mutations[0].Body!))
    Check(patch.RootElement.GetProperty("WantRunning").GetBoolean() && patch.RootElement.GetProperty("ForceDaemon").GetBoolean(), "resume persists unattended Windows startup intent");
api.CurrentId = ""; api.LoggedIn = false; api.Mutations.Clear();
api.Profiles = "[{\"ID\":\"saved1\",\"Key\":\"profile-one\",\"LocalUserID\":\"fixture-owner\",\"ControlURL\":\"https://controlplane.tailscale.com\"}]";
await client.ResumeAsync(official, "fixture-owner", default);
Check(api.CurrentId == "saved1" && api.LoggedIn && api.Mutations.All(m => m.Path != "start" && m.Path != "logout"), "empty Windows current profile restores the sole matching saved account");
api.CurrentId = ""; api.LoggedIn = false; api.Mutations.Clear();
api.Profiles = api.Profiles.Replace("fixture-owner", "other-owner");
await client.ResumeAsync(official, "fixture-owner", default);
Check(api.CurrentId == "" && api.Mutations.All(m => !m.Path.StartsWith("profiles/")), "another Windows user's saved identity is never selected");
api.Profiles = "[{\"ID\":\"a\",\"Key\":\"p-a\",\"LocalUserID\":\"\",\"ControlURL\":\"\"},{\"ID\":\"b\",\"Key\":\"p-b\",\"LocalUserID\":\"\",\"ControlURL\":\"\"}]";
api.Mutations.Clear();
await RejectAsync(() => client.ResumeAsync(official, "fixture-owner", default), "multiple saved identities require explicit account confirmation");
Check(api.Mutations.Count == 0, "ambiguous recovery performs no mutation");
api.CurrentId = "saved-other"; api.ControlUrl = self.ControlUrl;
await RejectAsync(() => client.ResumeAsync(official, "fixture-owner", default), "resume does not overwrite a saved account from a different server");
Check(api.Mutations.Count == 0, "server mismatch preserves all saved identity state");
Console.WriteLine($"{checks} provider checks passed; memory HTTP and stage42 fixture directory only.");

sealed class FakeApi : HttpMessageHandler
{
    public string ControlUrl = "", AuthUrl = "";
    public bool LoggedIn;
    public string CurrentId = "current", Profiles = "[]";
    public List<(string Method, string Path, string? Body)> Calls = [], Mutations = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
        var entry = (request.Method.Method, request.RequestUri!.AbsolutePath.Split("/localapi/v0/")[1], body);
        Calls.Add(entry);
        string payload;
        if (request.Method == HttpMethod.Get && entry.Item2 == "prefs") payload = JsonSerializer.Serialize(new { ControlURL = ControlUrl });
        else if (request.Method == HttpMethod.Get && entry.Item2 == "profiles/current") payload = JsonSerializer.Serialize(new { ID = CurrentId });
        else if (request.Method == HttpMethod.Get && entry.Item2 == "profiles/") payload = Profiles;
        else if (request.Method == HttpMethod.Get && entry.Item2 == "status") payload = JsonSerializer.Serialize(new
        {
            BackendState = LoggedIn ? "Running" : "NeedsLogin", TailscaleIPs = LoggedIn ? new[] { "100.70.1.2" } : [],
            Self = new { UserID = LoggedIn ? "123" : "0" }, AuthURL = AuthUrl
        });
        else
        {
            Mutations.Add(entry);
            if (request.Method == HttpMethod.Post && entry.Item2.StartsWith("profiles/"))
            {
                CurrentId = entry.Item2["profiles/".Length..]; LoggedIn = true;
                return new(HttpStatusCode.NoContent) { Content = new StringContent("") };
            }
            if (request.Method == HttpMethod.Post && entry.Item2 == "start")
            {
                if (body != "{}") throw new Exception("Start must preserve saved preferences");
                return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (request.Method != HttpMethod.Patch || entry.Item2 != "prefs") throw new Exception("Unexpected native mutation");
            using var patch = JsonDocument.Parse(body!);
            if (patch.RootElement.TryGetProperty("ControlURLSet", out var set) && set.GetBoolean()) ControlUrl = patch.RootElement.GetProperty("ControlURL").GetString()!;
            payload = "{}";
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    }
}
