using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace StreamHub.Host.Networking;

public sealed record NetworkHandoverState
{
    public bool LegacyInstalled { get; init; }
    public bool LegacyWasRunning { get; init; }
    public ServiceStartMode? LegacyStartMode { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Service-side, reversible handover. Existing credentials and binaries are untouched;
/// the original service start mode is saved, then set to manual until rollback.
/// </summary>
public sealed class NetworkHandover
{
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NetworkHandover(string productDataDirectory) =>
        _statePath = Path.Combine(Path.GetFullPath(productDataDirectory), "network", "handover.json");

    public bool IsHandedOver => File.Exists(_statePath);

    public async Task TakeOverAsync(Func<CancellationToken, Task> startEmbedded,
        Func<CancellationToken, Task> stopEmbedded,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_statePath))
            {
                if (IsLegacyRunning()) throw new InvalidOperationException("原组网服务已重新启动，请先恢复原组网后再接管，避免两个网络实例同时运行。");
                try { await startEmbedded(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    // A retry must not leave a partially started kernel alive. Keep the
                    // existing handover record so the previous state can still be restored.
                    await stopEmbedded(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                return;
            }
            using var service = FindLegacyService();
            var state = new NetworkHandoverState
            {
                LegacyInstalled = service is not null,
                LegacyWasRunning = service is not null && service.Status != ServiceControllerStatus.Stopped,
                LegacyStartMode = service?.StartType
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".new";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _statePath, false);
            try
            {
                if (state.LegacyInstalled && state.LegacyStartMode == ServiceStartMode.Automatic)
                    SetLegacyStartMode(ServiceStartMode.Manual);
                if (state.LegacyWasRunning && service is not null)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25)),
                        CancellationToken.None).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await startEmbedded(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Keep the recovery marker if restoration fails; the UI can retry rollback.
                await stopEmbedded(CancellationToken.None).ConfigureAwait(false);
                if (state.LegacyInstalled) await RestoreLegacyAsync(state, CancellationToken.None).ConfigureAwait(false);
                File.Delete(_statePath);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RollbackAsync(Func<CancellationToken, Task> stopEmbedded,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stopEmbedded(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<NetworkHandoverState>(
                await File.ReadAllTextAsync(_statePath, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException("无法读取原组网恢复记录，请保留文件并修复安装。");
            if (state.LegacyInstalled) await RestoreLegacyAsync(state, cancellationToken).ConfigureAwait(false);
            File.Delete(_statePath);
        }
        finally { _gate.Release(); }
    }

    public static bool IsLegacyRunning()
    {
        using var service = FindLegacyService();
        return service is not null && service.Status != ServiceControllerStatus.Stopped;
    }

    private static ServiceController? FindLegacyService()
    {
        ServiceController? found = null;
        foreach (var service in ServiceController.GetServices())
        {
            if (service.ServiceName.Equals("Tailscale", StringComparison.OrdinalIgnoreCase)) found = service;
            else service.Dispose();
        }
        return found;
    }

    private static async Task RestoreLegacyAsync(NetworkHandoverState state, CancellationToken cancellationToken)
    {
        using var service = FindLegacyService();
        if (service is null) throw new InvalidOperationException("原组网服务已不在系统中，无法自动恢复。内置登录状态仍被保留。");
        service.Refresh();
        try
        {
            if (!state.LegacyWasRunning && service.Status != ServiceControllerStatus.Stopped)
            {
                // Another app may have restarted the legacy service while the product
                // was in charge. Rollback restores its original stopped state too.
                if (service.Status == ServiceControllerStatus.StartPending)
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25)),
                        cancellationToken).ConfigureAwait(false);
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25)),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            if (state.LegacyWasRunning && service.Status != ServiceControllerStatus.Running)
            {
                if (service.Status == ServiceControllerStatus.StopPending)
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25)),
                        cancellationToken).ConfigureAwait(false);
                service.Refresh();
                // A previous recovery may have restored Disabled before the process finished starting.
                if (service.StartType == ServiceStartMode.Disabled) SetLegacyStartMode(ServiceStartMode.Manual);
                if (service.Status != ServiceControllerStatus.StartPending) service.Start();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (state.LegacyStartMode is { } originalMode) SetLegacyStartMode(originalMode);
        }
    }

    private static void SetLegacyStartMode(ServiceStartMode mode)
    {
        using var manager = OpenSCManager(null, null, 0x0001);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开系统服务管理器以保存组网启动方式。");
        using var service = OpenService(manager, "Tailscale", 0x0002);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法修改原组网服务启动方式，请通过后台服务操作。");
        if (!ChangeServiceConfig(service, uint.MaxValue, (uint)mode, uint.MaxValue,
                null, null, IntPtr.Zero, null, null, null, null))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "修改原组网服务启动方式失败，恢复记录已保留。");
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(ServiceHandle service, uint serviceType, uint startType,
        uint errorControl, string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
