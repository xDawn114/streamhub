using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed partial class HostRuntime
{
    private readonly SemaphoreSlim _firewallGate = new(1, 1);
    private NetworkAccessState _networkAccessState = NetworkAccessState.Pending;
    private bool _firewallInitialized;

    // Local owner UI only. No filesystem, COM or LocalAPI calls in the status/heartbeat path.
    private NetworkAccessState ReadNetworkAccessState() => Volatile.Read(ref _networkAccessState).Copy();

    // Call before starting the IPC/supervisor tasks. The installer normally writes migration state first.
    private async Task InitializeNetworkAccessAsync(CancellationToken cancellationToken)
    {
        await _firewallGate.WaitAsync(cancellationToken);
        try { await ApplyNetworkAccessAsync(null, true, cancellationToken); }
        finally { _firewallGate.Release(); }
    }

    private async Task<RuntimeResponse> RefreshNetworkAccessAsync(CancellationToken cancellationToken)
    {
        await _firewallGate.WaitAsync(cancellationToken);
        try { return await ApplyNetworkAccessAsync(null, !_firewallInitialized, cancellationToken); }
        finally { _firewallGate.Release(); }
    }

    // Dispatch exclusively after the existing local pipe owner/SYSTEM/admin identity check.
    // Never expose this command via the bound-device HTTP API.
    private async Task<RuntimeResponse> SaveNetworkAccessAsync(bool allowPublicLan, CancellationToken cancellationToken)
    {
        await _firewallGate.WaitAsync(cancellationToken);
        try { return await ApplyNetworkAccessAsync(allowPublicLan, false, cancellationToken); }
        finally { _firewallGate.Release(); }
    }

    private async Task<RuntimeResponse> ApplyNetworkAccessAsync(bool? allowPublicLan, bool allowLegacyMigration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_interactive) throw new IOException("交互预览不会修改系统防火墙；请通过已安装的后台服务管理网络访问。");
            var tunnels = await ReadVerifiedFirewallTunnelsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var adapter = new WindowsProductFirewallAdapter();
            var manager = new NetworkAccessManager(_root, new ProtectedNetworkAccessStore(_data), adapter);
            var state = allowPublicLan.HasValue ? manager.Save(allowPublicLan.Value, tunnels) : manager.Refresh(tunnels, allowLegacyMigration);
            _firewallInitialized = true;
            var previous = Volatile.Read(ref _networkAccessState);
            Volatile.Write(ref _networkAccessState, state);
            if (state.LastError != previous.LastError && state.LastError is not null) Log("网络访问策略：" + state.LastError);
            return new(state.ConfigurationValid && state.FirewallApplied && state.LastError is null, state.Message,
                PayloadJson: JsonSerializer.Serialize(state, RuntimeProtocol.JsonOptions));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var state = ReadNetworkAccessState() with
            {
                FirewallApplied = false, VpnReady = false, VpnInterfaces = [], VpnLocalAddresses = [],
                Message = "网络访问策略未完成，请检查错误后重试。", LastError = error.Message
            };
            Volatile.Write(ref _networkAccessState, state);
            return new(false, state.Message, PayloadJson: JsonSerializer.Serialize(state, RuntimeProtocol.JsonOptions));
        }
    }

    private async Task<FirewallTunnel[]> ReadVerifiedFirewallTunnelsAsync(CancellationToken cancellationToken)
    {
        // A stopped/replaced daemon is never evidence for a trusted interface. An external Tailscale
        // installation is not used as a fallback and none of its firewall rules are inspected/changed.
        var owned = _network;
        if (owned?.Running != true) return [];
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            var status = await _networkClient.GetStatusAsync(budget.Token, _transportSettings.Load());
            if (!ReferenceEquals(_network, owned) || !owned.Running || !status.Available || !status.LoggedIn || status.State != "Running") return [];
            return NetworkAccessPolicy.IdentifyTunnels(true, status.Addresses, FirewallInterfaceInventory.Read());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return []; } // Remove old VPN permissions on an unverifiable status/interface inventory.
    }

    // Include this task in the same cancellation/WhenAny/WhenAll lifetime as the existing supervisors.
    // Link changes wake it immediately; polling also detects LocalAPI IP/login changes without a NIC event.
    // A disappeared/renamed interface is revoked before its replacement is admitted.
    private async Task SuperviseFirewallAsync(CancellationToken cancellationToken)
    {
        using var changed = new SemaphoreSlim(0, 1);
        void Wake()
        {
            try { changed.Release(); }
            catch (Exception error) when (error is SemaphoreFullException or ObjectDisposedException) { }
        }
        NetworkAddressChangedEventHandler addresses = (_, _) => Wake();
        NetworkAvailabilityChangedEventHandler availability = (_, _) => Wake();
        NetworkChange.NetworkAddressChanged += addresses;
        NetworkChange.NetworkAvailabilityChanged += availability;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshNetworkAccessAsync(cancellationToken);
                await changed.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= addresses;
            NetworkChange.NetworkAvailabilityChanged -= availability;
        }
    }
}
