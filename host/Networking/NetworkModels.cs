using System.Diagnostics;
using System.IO;

namespace StreamHub.Host.Networking;

public sealed record NetworkPeer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string[] Addresses { get; init; } = [];
    public bool Online { get; init; }
    public bool Active { get; init; }
    public string RouteText { get; init; } = "尚未连接";
    public string RelayRegion { get; init; } = "";
    public long ReceivedBytes { get; init; }
    public long SentBytes { get; init; }
}

public sealed record NetworkStatus
{
    public TransportSettings Transport { get; init; } = new();
    public bool Available { get; init; }
    public bool LoggedIn { get; init; }
    public string State { get; init; } = "Unavailable";
    public string StateText { get; init; } = "异地连接尚未启用";
    public string Version { get; init; } = "";
    public string UserName { get; init; } = "";
    public string LoginName { get; init; } = "";
    public string HostName { get; init; } = "";
    public string NetworkName { get; init; } = "";
    public string[] Addresses { get; init; } = [];
    public NetworkPeer[] Peers { get; init; } = [];
    public string BrowseUrl { get; init; } = "";
    public string[] Health { get; init; } = [];
    public string Error { get; init; } = "";
}

public sealed record NetworkDiagnostics
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public NetworkStatus Status { get; init; } = new();
    public string[] Checks { get; init; } = [];
}

/// <summary>Only paths inside this product are used. An external installation is never a dependency.</summary>
public sealed class NetworkConfiguration
{
    public const string DefaultSocketPath = @"\\.\pipe\ProtectedPrefix\Administrators\StreamHub\tailscaled";

    public string ExecutablePath { get; }
    public string StateDirectory { get; }
    public string SocketPath { get; }

    public NetworkConfiguration(string installationDirectory, string dataDirectory,
        string socketPath = DefaultSocketPath)
    {
        ExecutablePath = Path.Combine(Path.GetFullPath(installationDirectory), "components", "network", "tailscaled.exe");
        StateDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "network");
        SocketPath = socketPath;
        if (!socketPath.StartsWith(@"\\.\pipe\ProtectedPrefix\Administrators\StreamHub\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("组网控制管道必须位于本产品的系统保护路径。", nameof(socketPath));
    }

    public ProcessStartInfo CreateStartInfo()
    {
        if (!File.Exists(ExecutablePath))
            throw new FileNotFoundException("内置组网组件缺失，请修复安装。", ExecutablePath);
        Directory.CreateDirectory(StateDirectory);
        var start = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // The daemon is a child of StreamHubHost, rather than a renamed Tailscale SCM service.
        foreach (var argument in new[]
        {
            "--socket=" + SocketPath,
            "--state=" + Path.Combine(StateDirectory, "tailscaled.state"),
            "--statedir=" + StateDirectory,
            "--tun=StreamHub",
            "--port=41641",
            "--syspolicy-file=",
            "--no-logs-no-support"
        }) start.ArgumentList.Add(argument);
        start.Environment["TS_LOGS_DIR"] = Path.Combine(StateDirectory, "logs");
        return start;
    }
}
