using System.Collections;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using StreamHub.Host.Networking;

const string Root = @"C:\Program Files\StreamHub";
int count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    count++;
    Console.WriteLine("PASS " + name);
}
bool Throws(Action action) { try { action(); return false; } catch { return true; } }
var strict = NetworkAccessPolicy.Build(Root, new(), []);
var lan = strict.Where(r => r.Name.Contains("-LAN-")).ToArray();
Check(lan.Length == 7 && lan.All(r => r.Profiles == 3 && r.RemoteAddresses == "LocalSubnet"), "fresh LAN control/media/discovery use only Domain/Private");
var outer = strict.Single(r => r.LocalPorts == "41641");
Check(outer.Protocol == 17 && outer.Profiles == 7 && outer.RemoteAddresses == "*" &&
    outer.ApplicationName == Path.Combine(Root, @"components\network\tailscaled.exe"), "outer encrypted transport preserves UDP hole punching");
Check(strict.All(r => r.RemoteAddresses != "100.64.0.0/10"), "no trusted daemon means no CGNAT permission");
Check(NetworkAccessPolicy.Build(Root, new(true), []).Where(r => r.Name.Contains("-LAN-")).All(r => r.Profiles == 7), "explicit Public LAN option covers discovery and media consistently");
var iface = new FirewallInterface("{IFACE-A}", "An alias chosen by the OS", true, false, ["100.89.106.49", "192.168.1.9"]);
var tunnels = NetworkAccessPolicy.IdentifyTunnels(true, ["100.89.106.49"], [iface]);
Check(tunnels is [{ InterfaceId: "{IFACE-A}", InterfaceName: "An alias chosen by the OS", LocalAddress: "100.89.106.49" }], "protected LocalAPI self IP joins exact live interface without guessed names");
Check(NetworkAccessPolicy.IdentifyTunnels(false, ["100.89.106.49"], [iface]).Length == 0, "stopped or untrusted daemon never authenticates a tunnel");
Check(NetworkAccessPolicy.IdentifyTunnels(true, ["192.168.1.9", "100.128.0.1", "100.63.255.255", "::1"], [iface]).Length == 0, "LAN/non-CGNAT/IPv6 self addresses do not widen VPN policy");
Check(NetworkAccessPolicy.IdentifyTunnels(true, ["100.89.106.49"], [iface with { Up = false }]).Length == 0 &&
    NetworkAccessPolicy.IdentifyTunnels(true, ["100.89.106.49"], [iface with { Loopback = true }]).Length == 0, "down and loopback interfaces are not VPN evidence");
Check(NetworkAccessPolicy.IdentifyTunnels(true, ["100.89.106.49"], [iface, iface with { Id = "OTHER", Name = "physical" }]).Length == 0, "ambiguous self IP on two adapters fails closed");
Check(NetworkAccessPolicy.IdentifyTunnels(true, ["100.89.106.49"], [iface with { Name = "*" }]).Length == 0, "wildcard adapter names cannot create all-interface permission");
var withVpn = NetworkAccessPolicy.Build(Root, new(), tunnels);
var vpn = withVpn.Where(r => r.Name.Contains("-VPN-")).ToArray();
Check(vpn.Length == 6 && vpn.All(r => r.Interfaces!.SequenceEqual([iface.Name]) &&
    r.LocalAddresses == "100.89.106.49" && r.RemoteAddresses == "100.64.0.0/10"), "every VPN API/control/media rule binds exact interface AND local self address");
Check(vpn.All(r => r.LocalPorts != "5353"), "LAN discovery is never exported through a CGNAT rule");
Check(Throws(() => NetworkAccessPolicy.Build(Root, new(), [new("id", "*", "100.64.1.1")])), "rule builder independently rejects wildcard tunnel identity");
Check(NetworkAccessConfiguration.Decode(new NetworkAccessConfiguration(true, true, true).Encode()) == new NetworkAccessConfiguration(true, true, true), "strict versioned document round-trips migration flags");
foreach (string json in new[] { "{}", "[]", "null", "{\"schemaVersion\":99,\"allowPublicLan\":true,\"legacyPublicLanPreserved\":false,\"migrationNoticePending\":false}",
    "{\"schemaVersion\":1,\"allowPublicLan\":true,\"legacyPublicLanPreserved\":false,\"migrationNoticePending\":false,\"allowPublicLan\":false}",
    "{\"schemaVersion\":1,\"allowPublicLan\":\"true\",\"legacyPublicLanPreserved\":false,\"migrationNoticePending\":false}",
    "{\"schemaVersion\":1,\"allowPublicLan\":true,\"legacyPublicLanPreserved\":false,\"migrationNoticePending\":true}" })
    Check(Throws(() => NetworkAccessConfiguration.Decode(Encoding.UTF8.GetBytes(json))), "bad/duplicate/unsupported JSON cannot enable Public: " + json[..Math.Min(json.Length, 40)]);
Check(Throws(() => NetworkAccessConfiguration.Decode(new byte[4097])), "oversized config is rejected before JSON parsing");

var legacy = new ProductFirewallRule("我控制 · 设备绑定与副屏请求", "legacy", Path.Combine(Root, @"service\串流中心后台.exe"), 6, "49199", "LocalSubnet,100.64.0.0/10", 7);
var oldInstallerNames = new (string Title, string Program)[]
{
    ("远程主屏控制", @"components\streaming\remote\sunshine.exe"),
    ("远程主屏音视频", @"components\streaming\remote\sunshine.exe"),
    ("平板副屏控制", @"components\streaming\tablet\sunshine.exe"),
    ("平板副屏音视频", @"components\streaming\tablet\sunshine.exe"),
    ("可信设备连接", @"service\串流中心后台.exe"),
    ("设备绑定与副屏请求", @"service\串流中心后台.exe"),
    ("异地直连", @"components\network\tailscaled.exe")
};
var oldInstallerRules = oldInstallerNames.Select(item => legacy with
{
    Name = "串流中心 · " + item.Title, ApplicationName = Path.Combine(Root, item.Program)
}).ToArray();
Check(oldInstallerRules.All(rule => NetworkAccessPolicy.IsOwnedName(rule.Name) && NetworkAccessPolicy.IsOwned(Root, rule)),
    "installed legacy Chinese product names are recognized for all seven observed endpoints");
Check(oldInstallerRules.All(rule => !NetworkAccessPolicy.IsOwned(Root, rule with { ApplicationName = @"C:\Program Files\Other\server.exe" })),
    "legacy Chinese names never authorize removing another executable rule");
var oldComPolicy = new MockPolicy();
using (var oldCom = new WindowsProductFirewallAdapter(oldComPolicy, () => new MockRule()))
{
    foreach (var rule in oldInstallerRules) oldCom.Add(rule);
    oldComPolicy.Rules.Items.Add(new MockRule { Name = "Tailscale", ApplicationName = @"C:\Program Files\Tailscale\tailscaled.exe" });
    ProductFirewallTransaction.Apply(oldCom, Root, [], []);
    Check(oldComPolicy.Rules.Items.Count == 1 && oldComPolicy.Rules.Items[0].Name == "Tailscale",
        "uninstall reads and removes all old Chinese names through COM while preserving independent Tailscale");
}
var upstream = legacy with { Name = "Sunshine", ApplicationName = @"C:\Program Files\Sunshine\sunshine.exe" };
var foreignCollision = legacy with { Name = "StreamHub-Remote-TCP", ApplicationName = @"C:\Program Files\Other\server.exe" };
Check(NetworkAccessPolicy.HasLegacyPublicLan(Root, [legacy]) && !NetworkAccessPolicy.HasLegacyPublicLan(Root, [foreignCollision]) &&
    !NetworkAccessPolicy.HasLegacyPublicLan(Root, [legacy with { Enabled = false }]), "migration requires active product-owned Public LocalSubnet rule");
var adapter = new MemoryFirewall([legacy, upstream, foreignCollision]);
var store = new MemoryStore();
var manager = new NetworkAccessManager(Root, store, adapter);
var state = manager.Refresh([], true);
Check(state.ConfigurationValid && state.FirewallApplied && state.AllowPublicLan && state.LegacyPublicLanPreserved &&
    state.MigrationNoticePending && store.Value?.AllowPublicLan == true, "first legacy upgrade persists explicit Public compatibility notice");
Check(adapter.Rules.Contains(upstream) && adapter.Rules.Contains(foreignCollision), "foreign Sunshine/other program rules survive product migration");
Check(!adapter.Rules.Contains(legacy), "legacy unscoped CGNAT rule is removed");
state = manager.Save(false, tunnels);
Check(state.FirewallApplied && !state.AllowPublicLan && !state.MigrationNoticePending && state.LegacyPublicLanPreserved, "owner save disables Public and acknowledges migration notice");
state = manager.Refresh([], true);
Check(!state.AllowPublicLan && !state.MigrationNoticePending, "repeated migration hook never re-enables saved Public policy");
Check(adapter.Rules.Where(r => NetworkAccessPolicy.IsOwned(Root, r)).All(r => !(r.Interfaces?.Length > 0)), "unverifiable/disappeared tunnel removes all old VPN rules");
var renamed = new FirewallTunnel(iface.Id, "renamed-vpn", "100.89.106.50");
manager.Refresh(tunnels);
manager.Refresh([renamed]);
Check(adapter.Rules.Where(r => r.Name.Contains("-VPN-")).All(r => r.Interfaces!.SequenceEqual(["renamed-vpn"]) &&
    r.LocalAddresses == "100.89.106.50"), "runtime interface rename/IP change revokes old rules and installs exact replacement");

adapter = new([]); store = new(); manager = new(Root, store, adapter);
state = manager.Refresh([], true);
Check(state.ConfigurationValid && state.FirewallApplied && !state.AllowPublicLan && store.Value == new NetworkAccessConfiguration(), "fresh install persists strict default");
var cached = manager.Snapshot;
Check(!cached.VpnReady && cached.VpnInterfaces.Length == 0, "snapshot contains only non-secret network access state");
store.FailRead = true; adapter.Rules.Add(legacy);
state = manager.Refresh([], true);
Check(!state.ConfigurationValid && state.FirewallApplied && !state.AllowPublicLan && state.LastError is not null &&
    !adapter.Rules.Contains(legacy), "corrupt config never migrates back to Public and revokes legacy broad rules");
state = manager.Save(false, []);
Check(state.ConfigurationValid && state.FirewallApplied && !state.AllowPublicLan, "explicit authorized save repairs bad config with chosen strict policy");

adapter = new(strict); store = new() { Value = new() }; manager = new(Root, store, adapter);
store.FailWrite = true;
state = manager.Save(true, []);
Check(!state.FirewallApplied && state.LastError is not null && store.Value.AllowPublicLan == false &&
    adapter.Rules.Where(r => r.Name.Contains("-LAN-")).All(r => r.Profiles == 3), "atomic config failure restores old strict firewall and does not report success");
store.FailWrite = false; adapter.FailAdd = r => r.Profiles == 7 && r.Name.Contains("-LAN-");
state = manager.Save(true, []);
Check(!state.FirewallApplied && !store.Value.AllowPublicLan && adapter.Rules.Count == strict.Length &&
    adapter.Rules.Where(r => r.Name.Contains("-LAN-")).All(r => r.Profiles == 3), "COM add failure rolls back only previously valid safe rules");
adapter = new([legacy]); store = new() { FailRead = true }; manager = new(Root, store, adapter);
adapter.FailAdd = r => r.LocalPorts == "5353";
state = manager.Refresh([], true);
Check(!state.FirewallApplied && !adapter.Rules.Contains(legacy) && !state.AllowPublicLan, "failed strict remediation never restores legacy broad allow");
adapter = new(withVpn); store = new() { Value = new() }; manager = new(Root, store, adapter);
adapter.FailAdd = r => r.Interfaces?.Contains("renamed-vpn") == true;
state = manager.Refresh([renamed]);
Check(!state.FirewallApplied && adapter.Rules.All(r => !(r.Interfaces?.Contains(iface.Name) == true)), "failed VPN refresh never restores stale interface permission");
adapter = new(strict); store = new() { Value = new() }; manager = new(Root, store, adapter);
adapter.FailAdd = _ => true;
state = manager.Save(true, []);
Check(state.LastError?.Contains("回滚") == true && !state.FirewallApplied && !store.Value.AllowPublicLan, "rollback failure is explicit and old stored config remains unchanged");
var occupied = strict[0] with { ApplicationName = @"C:\Program Files\Other\server.exe" };
adapter = new([occupied]); store = new() { Value = new() }; manager = new(Root, store, adapter);
state = manager.Refresh([]);
Check(!state.FirewallApplied && adapter.Rules.Contains(occupied), "foreign executable occupying a product rule name is reported and never removed");

var fw = new MockPolicy();
using (var com = new WindowsProductFirewallAdapter(fw, () => new MockRule()))
{
    foreach (var rule in withVpn) com.Add(rule);
    Check(fw.Rules.Items.Count == withVpn.Length, "mock COM adapter installs only completely configured rules");
    var nativeVpn = fw.Rules.Items.Where(r => r.Name.Contains("-VPN-")).ToArray();
    Check(nativeVpn.All(r => r.Interfaces is object[] values && values.SequenceEqual([iface.Name]) &&
        r.LocalAddresses == "100.89.106.49" && r.Profiles == 7 && !r.EdgeTraversal), "COM mapping preserves interface SAFEARRAY/local-IP/profile/edge boundaries");
    var observed = com.ReadRules();
    Check(observed.Count == withVpn.Length && observed.All(r => withVpn.Any(w => NetworkAccessPolicy.Equivalent(r, w))), "mock COM readback agrees with pure policy");
    com.Remove(withVpn[0].Name);
    Check(fw.Rules.Items.Count == withVpn.Length - 1, "mock COM remove targets exact owned rule");
    Check(Throws(() => com.Remove("Tailscale")), "COM adapter refuses upstream rule name");
}
fw = new();
using (var com = new WindowsProductFirewallAdapter(fw, () => new MockRule { RejectInterfaces = true }))
{
    Check(Throws(() => com.Add(vpn[0])) && fw.Rules.Items.Count == 0, "failed COM boundary setter never adds partially configured broad rule");
}
fw = new();
using (var com = new WindowsProductFirewallAdapter(fw, () => new MockRule { DropInterfaces = true }))
{
    store = new() { Value = new() }; manager = new(Root, store, com);
    state = manager.Refresh(tunnels);
    Check(!state.FirewallApplied && !state.VpnReady && fw.Rules.Items.All(r => !r.Name.Contains("-VPN-")),
        "COM readback detects silently lost interface constraint and rolls back the unsafe rule");
}
var acl = new FileSecurity();
var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
acl.SetOwner(admins); acl.SetAccessRuleProtection(true, false);
acl.AddAccessRule(new(admins, FileSystemRights.FullControl, AccessControlType.Allow));
acl.AddAccessRule(new(everyone, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
Check(!Throws(() => ProtectedNetworkAccessStore.ValidateSecurity(acl)), "in-memory ACL validation permits non-secret owner read without write");
acl.AddAccessRule(new(everyone, FileSystemRights.WriteData, AccessControlType.Allow));
Check(Throws(() => ProtectedNetworkAccessStore.ValidateSecurity(acl)), "in-memory ACL rejects non-admin policy writers");
acl = new(); acl.SetOwner(everyone);
Check(Throws(() => ProtectedNetworkAccessStore.ValidateSecurity(acl)), "in-memory ACL rejects untrusted policy owner");
var runtime = new StreamHub.Service.HostRuntime();
Check(!runtime.ReadForTest().FirewallApplied, "service cached status is read-only and initially unapplied");
var response = await runtime.RefreshForTest();
using (var payload = System.Text.Json.JsonDocument.Parse(response.PayloadJson!))
    Check(!response.Success && payload.RootElement.GetProperty("lastError").GetString()!.Contains("交互预览"), "interactive preview never activates COM or changes machine policy");

// Upgrade failure restoration: the old 151 service cannot rebuild scoped VPN permissions.
// Capture/restore only in-memory adapters; never activate firewall COM or install/start a service.
var legacyMedia = legacy with { Name = "我控制 · 远程主屏控制", ApplicationName = Path.Combine(Root, @"components\streaming\remote\sunshine.exe"),
    LocalPorts = "48984,48989,49010" };
adapter = new([legacy, legacyMedia, upstream, foreignCollision]); store = new();
var checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([], true); checkpoint.PolicyApplied();
checkpoint.Restore(adapter, store);
Check(adapter.Rules.Contains(legacy) && adapter.Rules.Contains(legacyMedia) && adapter.Rules.Count == 4,
    "failed legacy upgrade restores captured API/media CGNAT permissions before old service restart");
Check(adapter.Rules.Contains(upstream) && adapter.Rules.Contains(foreignCollision) &&
    store.Value == new NetworkAccessConfiguration(true, true, true),
    "upgrade rollback preserves independent rules and migrated protected config without overwriting owner choice");

adapter = new([legacy]); store = new(); checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([], true); checkpoint.PolicyApplied(); manager.Save(false, []);
checkpoint.Restore(adapter, store);
Check(adapter.Rules.Single() is { RemoteAddresses: "LocalSubnet", Profiles: 3 } && store.Value?.AllowPublicLan == false,
    "owner selecting strict mode after migration prevents wider Public or unscoped VPN restoration");

adapter = new([legacy]); store = new() { Value = new() };
checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([], true); checkpoint.PolicyApplied(); checkpoint.Restore(adapter, store);
Check(adapter.Rules.Single() is { RemoteAddresses: "LocalSubnet", Profiles: 3 },
    "strict policy saved before upgrade overrides stale broad legacy rules in checkpoint");

adapter = new([legacy]); store = new(); checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([], true); checkpoint.PolicyApplied(); manager.Save(true, []);
checkpoint.Restore(adapter, store);
Check(adapter.Rules.Single() is { RemoteAddresses: "LocalSubnet", Profiles: 7 } && store.Value?.MigrationNoticePending == false,
    "explicitly allowing Public LAN does not authorize restoring unscoped CGNAT VPN permissions");

adapter = new([legacy]); store = new(); checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([], true); checkpoint.PolicyApplied(); store.FailRead = true;
checkpoint.Restore(adapter, store);
Check(adapter.Rules.Single() is { RemoteAddresses: "LocalSubnet", Profiles: 3 } && store.FailRead,
    "unreadable current config fails closed during upgrade rollback without rewriting config");

adapter = new([legacy]); store = new(); checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([], true); checkpoint.PolicyApplied(); store.Value = null;
checkpoint.Restore(adapter, store);
Check(adapter.Rules.Single() is { RemoteAddresses: "LocalSubnet", Profiles: 3 },
    "deleted initialized config is not mistaken for an old never-configured installation");

adapter = new([legacy]); store = new(); checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
adapter.Rules.Clear(); checkpoint.Restore(adapter, store);
Check(adapter.Rules.Single() == legacy && store.Value is null,
    "early failed upgrade with no policy write restores original legacy authorization");

adapter = new(withVpn); store = new() { Value = new() };
checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([]); checkpoint.PolicyApplied(); checkpoint.Restore(adapter, store);
Check(adapter.Rules.Count == withVpn.Length && adapter.Rules.Where(r => r.Name.Contains("-VPN-")).All(r =>
    r.Interfaces!.SequenceEqual([iface.Name]) && r.LocalAddresses == "100.89.106.49"),
    "failed upgrade of a scoped installation restores original exact interface/IP constraints");

adapter = new([legacy]); store = new(); checkpoint = StreamHub.Installer.FirewallUpgradeRollback.Capture(Root, adapter, store);
manager = new(Root, store, adapter); manager.Refresh([], true); checkpoint.PolicyApplied(); adapter.FailAdd = r => r.Name == legacy.Name;
Check(Throws(() => checkpoint.Restore(adapter, store)), "COM failure while restoring old upgrade permissions is reported to installer");

adapter = new(withVpn.Concat([upstream]));
ProductFirewallTransaction.Apply(adapter, Root, [], []);
Check(adapter.Rules.Count == 1 && adapter.Rules.Single() == upstream,
    "new installation failure cleanup removes only owned product rules");
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed = true, count,
    realFirewallChanged = false, networkInterfacesChanged = false, serviceStarted = false, localApiCalled = false }));

sealed class MemoryStore : INetworkAccessStore
{
    public NetworkAccessConfiguration? Value;
    public bool FailRead, FailWrite;
    public NetworkAccessConfiguration? Read() => FailRead ? throw new IOException("corrupt config") : Value;
    public void Write(NetworkAccessConfiguration value)
    { if (FailWrite) throw new IOException("atomic write failed"); Value = value; FailRead = false; }
}
sealed class MemoryFirewall(IEnumerable<ProductFirewallRule> initial) : IProductFirewallAdapter
{
    public List<ProductFirewallRule> Rules { get; } = initial.ToList();
    public Func<ProductFirewallRule, bool>? FailAdd;
    public IReadOnlyList<ProductFirewallRule> ReadRules() => Rules.ToArray();
    public void Add(ProductFirewallRule rule) { if (FailAdd?.Invoke(rule) == true) throw new IOException("simulated COM add failure"); Rules.Add(rule); }
    public void Remove(string name) => Rules.RemoveAll(rule => rule.Name == name);
}
public sealed class MockPolicy { public MockRules Rules { get; } = new(); }
public sealed class MockRules : IEnumerable
{
    public List<MockRule> Items { get; } = [];
    public void Add(MockRule rule) => Items.Add(rule);
    public void Remove(string name) => Items.RemoveAll(r => r.Name == name);
    public IEnumerator GetEnumerator() => Items.GetEnumerator();
}
public sealed class MockRule
{
    public bool RejectInterfaces { get; init; }
    public bool DropInterfaces { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ApplicationName { get; set; } = "";
    public string Grouping { get; set; } = "";
    public int Protocol { get; set; }
    public string LocalPorts { get; set; } = "*";
    public string RemotePorts { get; set; } = "*";
    public string LocalAddresses { get; set; } = "*";
    public string RemoteAddresses { get; set; } = "*";
    public int Profiles { get; set; }
    private object? _interfaces;
    public object? Interfaces { get => _interfaces; set { if (RejectInterfaces) throw new IOException("simulated setter failure"); if (!DropInterfaces) _interfaces = value; } }
    public string InterfaceTypes { get; set; } = "All";
    public string ServiceName { get; set; } = "";
    public int Direction { get; set; }
    public int Action { get; set; }
    public bool Enabled { get; set; }
    public bool EdgeTraversal { get; set; }
}
