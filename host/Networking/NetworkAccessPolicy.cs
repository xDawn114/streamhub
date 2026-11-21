using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Networking;

public sealed record NetworkAccessConfiguration(bool AllowPublicLan = false,
    bool LegacyPublicLanPreserved = false, bool MigrationNoticePending = false)
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 4096;

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = SchemaVersion, allowPublicLan = AllowPublicLan,
        legacyPublicLanPreserved = LegacyPublicLanPreserved, migrationNoticePending = MigrationNoticePending
    });

    public static NetworkAccessConfiguration Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new IOException("网络访问配置大小无效。");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        string[] expected = ["schemaVersion", "allowPublicLan", "legacyPublicLanPreserved", "migrationNoticePending"];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != expected.Length ||
            root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            expected.Any(name => !root.TryGetProperty(name, out _)) ||
            !root.GetProperty("schemaVersion").TryGetInt32(out int version) || version != SchemaVersion ||
            expected.Skip(1).Any(name => root.GetProperty(name).ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new IOException("网络访问配置格式或版本无效，未启用公共网络兼容。");
        bool preserved = root.GetProperty("legacyPublicLanPreserved").GetBoolean();
        bool notice = root.GetProperty("migrationNoticePending").GetBoolean();
        if (notice && !preserved) throw new IOException("网络访问迁移记录无效。");
        return new(root.GetProperty("allowPublicLan").GetBoolean(), preserved, notice);
    }
}

// A non-secret projection for the local owner UI. Addresses identify only this PC.
public sealed record NetworkAccessState(bool AllowPublicLan, bool LegacyPublicLanPreserved,
    bool MigrationNoticePending, bool ConfigurationValid, bool FirewallApplied, bool VpnReady,
    string[] VpnInterfaces, string[] VpnLocalAddresses, string Message, string? LastError)
{
    public static NetworkAccessState Pending => new(false, false, false, false, false, false, [], [],
        "网络访问策略尚未应用。", null);
    public NetworkAccessState Copy() => this with
    { VpnInterfaces = VpnInterfaces.ToArray(), VpnLocalAddresses = VpnLocalAddresses.ToArray() };
}

public sealed record FirewallInterface(string Id, string Name, bool Up, bool Loopback, string[] Addresses);
public sealed record FirewallTunnel(string InterfaceId, string InterfaceName, string LocalAddress);

public sealed record ProductFirewallRule(string Name, string Description, string ApplicationName,
    int Protocol, string LocalPorts, string RemoteAddresses, int Profiles,
    string LocalAddresses = "*", string[]? Interfaces = null, string Grouping = "我控制",
    int Direction = 1, int Action = 1, bool Enabled = true, string RemotePorts = "*",
    string InterfaceTypes = "All", string ServiceName = "", bool EdgeTraversal = false);

public interface INetworkAccessStore
{
    // null means absent, not unreadable. A failed atomic write must leave the old document intact.
    NetworkAccessConfiguration? Read();
    void Write(NetworkAccessConfiguration configuration);
}

public interface IProductFirewallAdapter
{
    IReadOnlyList<ProductFirewallRule> ReadRules();
    void Add(ProductFirewallRule rule);
    void Remove(string name);
}

/// <summary>Pure rule construction: neither a 100.x address nor a guessed adapter name is VPN identity.</summary>
public static class NetworkAccessPolicy
{
    public const int DomainPrivate = 3;
    public const int AllProfiles = 7;
    private const string Prefix = "StreamHub-Access-";
    private sealed record Endpoint(string Id, string Title, string RelativeProgram, int Protocol, string Ports);
    private static readonly Endpoint[] Endpoints =
    [
        new("Remote-TCP", "远程主屏控制", "components/streaming/remote/sunshine.exe", 6, "48984,48989,49010"),
        new("Remote-UDP", "远程主屏音视频", "components/streaming/remote/sunshine.exe", 17, "48998-49000,49002,49010"),
        new("Tablet-TCP", "平板副屏控制", "components/streaming/tablet/sunshine.exe", 6, "49084,49089,49110"),
        new("Tablet-UDP", "平板副屏音视频", "components/streaming/tablet/sunshine.exe", 17, "49098-49100,49102,49110"),
        new("Control-TCP", "可信设备连接", "service/串流中心后台.exe", 6, "48980"),
        new("Product-Control-TCP", "设备绑定与副屏请求", "service/串流中心后台.exe", 6, "49199"),
        new("Discovery-UDP", "附近电脑发现", "service/串流中心后台.exe", 17, "5353"),
        new("Network-UDP", "异地直连", "components/network/tailscaled.exe", 17, "41641")
    ];

    public static FirewallTunnel[] IdentifyTunnels(bool trustedDaemonRunning, IEnumerable<string> daemonSelfAddresses,
        IEnumerable<FirewallInterface> interfaces)
    {
        if (!trustedDaemonRunning) return [];
        var adapters = interfaces.Where(n => n.Up && !n.Loopback && SafeIdentity(n.Id) && SafeIdentity(n.Name)).ToArray();
        var result = new List<FirewallTunnel>();
        foreach (string address in daemonSelfAddresses.Distinct(StringComparer.Ordinal).Where(IsTunnelIpv4))
        {
            // LocalAPI is the product's protected pipe. Require its exact self IP on one real,
            // live interface. Wintun may report Ethernet; names/types alone are not trustworthy.
            var matches = adapters.Where(n => n.Addresses.Contains(address, StringComparer.Ordinal)).ToArray();
            if (matches.Length == 1 && adapters.Count(n => n.Id.Equals(matches[0].Id, StringComparison.OrdinalIgnoreCase)) == 1)
                result.Add(new(matches[0].Id, matches[0].Name, address));
        }
        return result.OrderBy(n => n.InterfaceId, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.LocalAddress, StringComparer.Ordinal).ToArray();
    }

    private static bool SafeIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        !value.Any(c => char.IsControl(c) || c is '*' or ',' or ';');

    private static bool IsTunnelIpv4(string value)
    {
        if (!UsableIpv4.IsUsable(value) || !IPAddress.TryParse(value, out var address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    public static ProductFirewallRule[] Build(string root, NetworkAccessConfiguration configuration,
        IReadOnlyList<FirewallTunnel> tunnels)
    {
        var result = new List<ProductFirewallRule>();
        foreach (var endpoint in Endpoints)
        {
            string program = ProgramPath(root, endpoint.RelativeProgram);
            if (endpoint.Id == "Network-UDP")
            {
                // Outer encrypted transport, not a control/API permission. Keep UDP hole punching.
                result.Add(new(Prefix + "Outer-" + endpoint.Id, "我控制 · 异地直连（外层传输）", program,
                    endpoint.Protocol, endpoint.Ports, "*", AllProfiles));
                continue;
            }
            result.Add(new(Prefix + "LAN-" + endpoint.Id, "我控制 · " + endpoint.Title + "（局域网）", program,
                endpoint.Protocol, endpoint.Ports, "LocalSubnet", configuration.AllowPublicLan ? AllProfiles : DomainPrivate));
            if (endpoint.Id == "Discovery-UDP") continue; // Discovery is LAN only.
            foreach (var tunnel in tunnels.Distinct())
            {
                if (!SafeIdentity(tunnel.InterfaceId) || !SafeIdentity(tunnel.InterfaceName) || !IsTunnelIpv4(tunnel.LocalAddress))
                    throw new ArgumentException("隧道接口必须已核实且具有本机隧道 IPv4 地址。");
                string key = tunnel.InterfaceId.ToUpperInvariant() + "|" + tunnel.InterfaceName.ToUpperInvariant() + "|" + tunnel.LocalAddress;
                string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
                result.Add(new(Prefix + "VPN-" + endpoint.Id + "-" + suffix,
                    "我控制 · " + endpoint.Title + "（已核实隧道）", program, endpoint.Protocol, endpoint.Ports,
                    "100.64.0.0/10", AllProfiles, tunnel.LocalAddress, [tunnel.InterfaceName]));
            }
        }
        return result.ToArray();
    }

    private static string ProgramPath(string root, string relative) => Path.GetFullPath(Path.Combine(root,
        relative.Replace('/', Path.DirectorySeparatorChar)));

    public static bool IsOwnedName(string name) => Endpoints.Any(endpoint =>
        IsEndpointName(name, endpoint.Id, endpoint.Title)) || IsObsoleteName(name);

    public static bool IsOwned(string root, ProductFirewallRule rule)
    {
        try
        {
            if (Endpoints.Any(endpoint => IsEndpointName(rule.Name, endpoint.Id, endpoint.Title) &&
                Path.GetFullPath(rule.ApplicationName).Equals(ProgramPath(root, endpoint.RelativeProgram), StringComparison.OrdinalIgnoreCase))) return true;
            return IsObsoleteName(rule.Name) && Path.GetFullPath(rule.ApplicationName).Equals(
                ProgramPath(root, "components/streaming/sunshine.exe"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static bool IsObsoleteName(string name) => name is "StreamHub-Streaming-TCP" or "StreamHub-Streaming-UDP" or
        "我控制 · 画面与控制" or "我控制 · 音视频";

    private static bool IsEndpointName(string name, string id, string title)
    {
        if (name == "StreamHub-" + id || name == "我控制 · " + title || name == "串流中心 · " + title || name == Prefix + "LAN-" + id ||
            id == "Network-UDP" && name == Prefix + "Outer-" + id) return true;
        string prefix = Prefix + "VPN-" + id + "-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length == prefix.Length + 16 &&
            name[prefix.Length..].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    public static bool HasLegacyPublicLan(string root, IEnumerable<ProductFirewallRule> rules) => rules.Any(rule =>
        IsOwned(root, rule) && !rule.Name.StartsWith(Prefix, StringComparison.Ordinal) && rule.Enabled && rule.Direction == 1 &&
        rule.Action == 1 && (rule.Profiles & 4) != 0 && rule.RemoteAddresses.Split(',').Any(scope =>
            scope.Trim().Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase)));

    public static bool Equivalent(ProductFirewallRule left, ProductFirewallRule right) =>
        left.Name == right.Name && left.Description == right.Description &&
        string.Equals(left.ApplicationName, right.ApplicationName, StringComparison.OrdinalIgnoreCase) &&
        left.Protocol == right.Protocol && left.LocalPorts == right.LocalPorts && left.Profiles == right.Profiles &&
        AddressFilter(left.LocalAddresses) == AddressFilter(right.LocalAddresses) &&
        AddressFilter(left.RemoteAddresses) == AddressFilter(right.RemoteAddresses) &&
        (left.Interfaces ?? []).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            (right.Interfaces ?? []).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) &&
        left.Grouping == right.Grouping && left.Direction == right.Direction && left.Action == right.Action &&
        left.Enabled == right.Enabled && left.RemotePorts == right.RemotePorts &&
        left.InterfaceTypes.Equals(right.InterfaceTypes, StringComparison.OrdinalIgnoreCase) &&
        left.ServiceName == right.ServiceName && left.EdgeTraversal == right.EdgeTraversal;

    private static string AddressFilter(string value) => string.Join(',', value.Split(',').Select(part =>
    {
        var bits = part.Trim().ToLowerInvariant().Split('/');
        if (!IPAddress.TryParse(bits[0], out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return part.Trim().ToLowerInvariant();
        int prefix = 32;
        if (bits.Length == 2 && !int.TryParse(bits[1], out prefix) && IPAddress.TryParse(bits[1], out var mask))
        {
            string binary = string.Concat(mask.GetAddressBytes().Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
            prefix = binary.TakeWhile(c => c == '1').Count();
            if (binary.Skip(prefix).Any(c => c != '0')) return part;
        }
        return address + (prefix == 32 ? "" : "/" + prefix);
    }).Order(StringComparer.Ordinal));
}

/// <summary>Only restores old rules that remain allowed by today's config AND today's verified interfaces.</summary>
public sealed class ProductFirewallTransaction
{
    private readonly IProductFirewallAdapter _adapter;
    private readonly string _root;
    private readonly ProductFirewallRule[] _rollback;
    private ProductFirewallTransaction(IProductFirewallAdapter adapter, string root, ProductFirewallRule[] rollback)
        => (_adapter, _root, _rollback) = (adapter, root, rollback);

    public static ProductFirewallTransaction Apply(IProductFirewallAdapter adapter, string root,
        ProductFirewallRule[] desired, ProductFirewallRule[] safeRollbackPolicy)
    {
        var previous = adapter.ReadRules().Where(rule => NetworkAccessPolicy.IsOwned(root, rule)).ToArray();
        var transaction = new ProductFirewallTransaction(adapter, root, previous.Where(old =>
            safeRollbackPolicy.Any(safe => NetworkAccessPolicy.Equivalent(old, safe))).ToArray());
        try { transaction.Replace(desired); return transaction; }
        catch (Exception error)
        {
            try { transaction.Rollback(); }
            catch (Exception rollback) { throw new IOException("防火墙更新失败，安全规则回滚也未完成：" + rollback.Message, error); }
            throw new IOException("防火墙更新失败，已回滚到仍符合当前边界的原规则。", error);
        }
    }

    public void Rollback() => Replace(_rollback);

    private void Replace(ProductFirewallRule[] target)
    {
        var all = _adapter.ReadRules();
        if (all.Any(rule => !NetworkAccessPolicy.IsOwned(_root, rule) && target.Any(wanted => wanted.Name == rule.Name)))
            throw new IOException("防火墙规则名称被其他程序使用，未修改该程序的规则。");
        var current = all.Where(rule => NetworkAccessPolicy.IsOwned(_root, rule)).ToArray();
        if (current.Any(rule => all.Any(other => other.Name == rule.Name && !NetworkAccessPolicy.IsOwned(_root, other))))
            throw new IOException("存在同名的其他程序防火墙规则，已停止自动修改。");
        // Revoke broad/stale rules before adding replacements. Never retain a removed VPN
        // interface just because COM failed while installing its successor.
        foreach (var old in current.Where(old => current.Count(r => r.Name == old.Name) != 1 ||
            !target.Any(rule => NetworkAccessPolicy.Equivalent(old, rule)))) _adapter.Remove(old.Name);
        var retained = _adapter.ReadRules().Where(rule => NetworkAccessPolicy.IsOwned(_root, rule)).ToArray();
        foreach (var rule in target.Where(rule => !retained.Any(old => NetworkAccessPolicy.Equivalent(old, rule)))) _adapter.Add(rule);
        var applied = _adapter.ReadRules().Where(rule => NetworkAccessPolicy.IsOwned(_root, rule)).ToArray();
        if (applied.Length != target.Length || target.Any(rule => !applied.Any(old => NetworkAccessPolicy.Equivalent(old, rule))))
            throw new IOException("防火墙规则回读未保留完整的接口、地址或配置文件边界。");
    }
}

/// <summary>Serialized by the service helper. Uses injected storage/COM adapters for isolated tests.</summary>
public sealed class NetworkAccessManager(string root, INetworkAccessStore store, IProductFirewallAdapter firewall)
{
    private NetworkAccessState _state = NetworkAccessState.Pending;
    public NetworkAccessState Snapshot => _state.Copy();

    public NetworkAccessState Refresh(FirewallTunnel[] tunnels, bool allowLegacyMigration = false)
    {
        NetworkAccessConfiguration? stored = null;
        string? readError = null;
        try { stored = store.Read(); }
        catch (Exception ex) { readError = "网络访问配置无法验证，公共网络兼容已关闭：" + ex.Message; }
        var configuration = stored ?? new();
        bool missing = stored is null && readError is null;
        try
        {
            if (missing && allowLegacyMigration && NetworkAccessPolicy.HasLegacyPublicLan(root, firewall.ReadRules()))
                configuration = new(true, true, true);
            var previous = stored ?? new();
            var transaction = ProductFirewallTransaction.Apply(firewall, root, NetworkAccessPolicy.Build(root, configuration, tunnels),
                readError is null ? NetworkAccessPolicy.Build(root, previous, tunnels) : []);
            if (missing)
            {
                try { store.Write(configuration); }
                catch (Exception save)
                {
                    try { transaction.Rollback(); }
                    catch (Exception rollback) { throw new IOException("配置保存失败且安全规则回滚未完成：" + rollback.Message, save); }
                    throw new IOException("配置保存失败，已撤销本次规则变更。", save);
                }
            }
            return Publish(configuration, readError is null, true, tunnels, readError);
        }
        catch (Exception error)
        {
            return Publish(stored ?? new(), stored is not null && readError is null, false, [],
                readError is null ? error.Message : readError + " " + error.Message);
        }
    }

    public NetworkAccessState Save(bool allowPublicLan, FirewallTunnel[] tunnels)
    {
        NetworkAccessConfiguration? previous = null;
        bool previousValid = false;
        try { previous = store.Read(); previousValid = previous is not null; } catch { /* Explicit local save may repair a bad document. */ }
        var candidate = new NetworkAccessConfiguration(allowPublicLan, previous?.LegacyPublicLanPreserved == true, false);
        try
        {
            var transaction = ProductFirewallTransaction.Apply(firewall, root, NetworkAccessPolicy.Build(root, candidate, tunnels),
                previousValid ? NetworkAccessPolicy.Build(root, previous!, tunnels) : []);
            try { store.Write(candidate); }
            catch (Exception save)
            {
                try { transaction.Rollback(); }
                catch (Exception rollback) { throw new IOException("配置保存失败且安全规则回滚未完成：" + rollback.Message, save); }
                throw new IOException("配置保存失败，原配置和安全规则已保留。", save);
            }
            return Publish(candidate, true, true, tunnels, null);
        }
        catch (Exception error)
        {
            // Report failure; do not claim that the requested exposure has taken effect.
            return Publish(previous ?? new(), previousValid, false, [], error.Message);
        }
    }

    private NetworkAccessState Publish(NetworkAccessConfiguration configuration, bool valid, bool applied,
        FirewallTunnel[] tunnels, string? error)
    {
        string message = !valid ? "网络访问配置需要修复；未自动恢复公共网络兼容。" :
            configuration.MigrationNoticePending ? "升级保留了旧版公共网络局域网访问。请在电脑设置确认或关闭；公共 Wi-Fi 同网段设备也能访问服务端口，仍需绑定认证。" :
            configuration.AllowPublicLan ? "已允许公共网络上的局域网访问；设备仍需绑定认证。" : "局域网访问仅限专用和域网络。";
        if (!applied) message = "防火墙规则未完整应用，请检查错误后重试。 " + message;
        _state = new(configuration.AllowPublicLan, configuration.LegacyPublicLanPreserved, configuration.MigrationNoticePending,
            valid, applied, applied && tunnels.Length > 0, tunnels.Select(t => t.InterfaceName).Distinct().ToArray(),
            tunnels.Select(t => t.LocalAddress).Distinct().ToArray(), message, error);
        return Snapshot;
    }
}
