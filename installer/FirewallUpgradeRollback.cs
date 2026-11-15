using StreamHub.Host.Networking;

namespace StreamHub.Installer;

/// <summary>
/// Process-local checkpoint for a failed upgrade. An old service cannot reconstruct new VPN
/// rules. Restoring its original permissions is permitted only while the legacy authorization
/// is still unreviewed; an explicit/current policy always takes precedence over the checkpoint.
/// </summary>
internal sealed class FirewallUpgradeRollback
{
    private readonly string _root;
    private readonly ProductFirewallRule[] _rules;
    private readonly NetworkAccessConfiguration? _configuration;
    private readonly bool _configurationReadable;
    private bool _configurationApplied;

    private FirewallUpgradeRollback(string root, IProductFirewallAdapter firewall, INetworkAccessStore store)
    {
        _root = root;
        _rules = firewall.ReadRules().Where(rule => NetworkAccessPolicy.IsOwned(root, rule))
            .Select(rule => rule with { Interfaces = rule.Interfaces?.ToArray() }).ToArray();
        try { _configuration = store.Read(); _configurationReadable = true; }
        catch { _configurationReadable = false; }
    }

    internal static FirewallUpgradeRollback Capture(string root, IProductFirewallAdapter firewall, INetworkAccessStore store)
        => new(root, firewall, store);

    // A missing document after successful policy initialization is no longer evidence of a
    // pre-policy installation. Do not treat deletion/corruption as consent to restore broad rules.
    internal void PolicyApplied() => _configurationApplied = true;

    internal string Restore(IProductFirewallAdapter firewall, INetworkAccessStore store)
    {
        NetworkAccessConfiguration? current = null;
        bool readable;
        try { current = store.Read(); readable = true; }
        catch { readable = false; }
        bool originalWasLegacy = _configurationReadable && (_configuration is null || UnreviewedLegacy(_configuration)) &&
            NetworkAccessPolicy.HasLegacyPublicLan(_root, _rules);
        bool legacyStillAuthorized = readable && (current is not null && UnreviewedLegacy(current) ||
            current is null && _configuration is null && !_configurationApplied);
        bool restoreOriginal = originalWasLegacy && legacyStillAuthorized;
        var desired = restoreOriginal ? _rules : NarrowToCurrentPolicy(current, readable);
        // The generic transaction still protects ownership/name collisions/readback. Its failure
        // rollback cannot reintroduce any permissions excluded by the current policy.
        ProductFirewallTransaction.Apply(firewall, _root, desired, desired);
        return restoreOriginal ? "升级未完成，已恢复旧版本原有的产品防火墙权限；公共网络兼容仍待电脑端确认。" :
            "升级未完成，已按当前网络访问策略恢复产品规则，未恢复旧版宽范围隧道权限。";
    }

    private static bool UnreviewedLegacy(NetworkAccessConfiguration configuration) =>
        configuration.AllowPublicLan && configuration.LegacyPublicLanPreserved && configuration.MigrationNoticePending;

    private ProductFirewallRule[] NarrowToCurrentPolicy(NetworkAccessConfiguration? current, bool readable)
    {
        var result = new List<ProductFirewallRule>();
        foreach (var rule in _rules)
        {
            if (!rule.Enabled || rule.Direction != 1 || rule.Action != 1)
            { result.Add(rule); continue; } // Retain existing disabled/deny rules, never enable a formerly disabled permission.
            if (rule.Protocol == 17 && rule.LocalPorts == "41641" && rule.ApplicationName.Equals(
                Path.Combine(_root, "components", "network", "tailscaled.exe"), StringComparison.OrdinalIgnoreCase))
            { result.Add(rule); continue; }
            if (readable && current is not null && IsScopedVpn(rule))
            { result.Add(rule); continue; }
            // A legacy mixed LocalSubnet+CGNAT rule can retain LAN access, but must lose
            // unscoped CGNAT permissions and Public when the owner chose the strict policy.
            if (!rule.RemoteAddresses.Split(',').Any(scope => scope.Trim().Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase) || scope.Trim() == "*"))
                continue;
            int profiles = rule.Profiles & (readable && current?.AllowPublicLan == true ? 7 : 3);
            if (profiles != 0) result.Add(rule with { RemoteAddresses = "LocalSubnet", Profiles = profiles });
        }
        return result.ToArray();
    }

    private bool IsScopedVpn(ProductFirewallRule rule)
    {
        if (!rule.Name.StartsWith("StreamHub-Access-VPN-", StringComparison.Ordinal) || rule.Interfaces is not [string alias]) return false;
        try
        {
            // Reuse the pure builder to validate every boundary; the original verified interface
            // ID is deliberately not reconstructed from a display name or from the 100.x range.
            var approved = NetworkAccessPolicy.Build(_root, new(), [new("checkpoint", alias, rule.LocalAddresses)]);
            return approved.Where(candidate => candidate.Name.Contains("-VPN-", StringComparison.Ordinal)).Any(candidate =>
                NetworkAccessPolicy.Equivalent(rule, candidate with { Name = rule.Name, Description = rule.Description }));
        }
        catch (ArgumentException) { return false; }
    }
}
