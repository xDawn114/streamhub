using System.ServiceProcess;
using StreamHub.Host.Runtime;
using StreamHub.Host.Display;

namespace StreamHub.Service;

internal static class Program
{
    private static string GetArgument(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : fallback;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
    public static async Task Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--desktop-input")
        {
            if (!DesktopInputProbe.ValidPipe(args[1]) || !int.TryParse(args[2], out int serverId) || serverId <= 0)
            { Environment.ExitCode = 2; return; }
            try { await DesktopInputProbe.RunHelperAsync(args[1], serverId, args[3]); }
            catch { Environment.ExitCode = 1; } // Never put credential/helper exceptions in service logs.
            return;
        }
        if(args.Length==3 && args[0]=="--recycle-file") {
            if(System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value!=args[2]) {Environment.ExitCode=2;return;}
            try {RecycleBin.Move(args[1]);} catch(Exception) {Environment.ExitCode=1;}
            return;
        }
        if(args.Length==1 && args[0]=="--lock-desktop")
        {
            Environment.ExitCode=LockWorkStation()?0:1;return;
        }
        if (args.Length == 7 && args[0] == "--tablet-display")
        {
            // Numeric capabilities and a process-authenticated pipe only. This
            // helper configures the owned MTT output in the user's desktop.
            if (!DisplayModeProbe.IsValidTabletPipeName(args[1]) || !int.TryParse(args[2], out var serverId) || serverId <= 0 ||
                !int.TryParse(args[3], out var width) || !int.TryParse(args[4], out var height) ||
                !int.TryParse(args[5], out var refreshRate) || !int.TryParse(args[6], out var orientation))
            { Environment.ExitCode = 2; return; }
            try { await DisplayModeProbe.WriteTabletAsync(args[1], serverId, new(width, height, refreshRate, orientation)); }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 3 && args[0] == "--display-mode")
        {
            if (!DisplayModeProbe.IsValidPipeName(args[1]) || !int.TryParse(args[2], out var serverId) || serverId <= 0)
            { Environment.ExitCode = 2; return; }
            try { await DisplayModeProbe.WriteAsync(args[1], serverId); }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 3 && args[0] == "--display-helper")
        {
            // Fixed operations only: no command/path supplied by the network client
            // is executed. The service launches this in winsta0\default.
            if (!int.TryParse(args[1], out var orientation) || orientation is not (0 or 90 or 180 or 270) ||
                args[2] is not ("prepare" or "verify" or "detach")) { Environment.ExitCode = 2; return; }
            try
            {
                if (args[2] == "detach") DisplayController.DisableExtended();
                else
                {
                    if (args[2] == "prepare") DisplayController.EnsureExtended();
                    DisplayController.Configure(orientation, args[2] == "verify");
                }
            }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 1 && args[0] == "--display-disable")
        {
            // Detach only the product's MTT target from the interactive desktop.
            // The service launches this fixed helper in winsta0\default; no caller
            // supplied path or display identifier is accepted here.
            try { DisplayController.DisableExtended(); }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        if (args.Length is 2 or 8 && args[0] == "--privacy-screen")
        {
            // Fixed helper entry point. The service starts it only after an
            // authenticated local control request and a ready remote heartbeat.
            if (!PrivacyScreenLifetime.IsValidName(args[1])) { Environment.ExitCode = 2; return; }
            try
            {
                using var guard = args.Length == 8 ? PrivacyScreenGuard.Open(
                    int.Parse(args[2]), long.Parse(args[3]), int.Parse(args[5]), long.Parse(args[6]),
                    int.Parse(args[4]), long.Parse(args[7]), (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId,
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "components", "streaming", "remote", "sunshine.exe"))) : null;
                Environment.ExitCode = PrivacyScreenWindow.Run(args[1], guard);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or
                System.ComponentModel.Win32Exception or InvalidOperationException)
            { Environment.ExitCode = 13; }
            return;
        }
        if (args.Length == 2 && args[0] == "--signal" && int.TryParse(args[1], out var processId))
        {
            Environment.ExitCode = SessionLauncher.SignalCtrlC(processId) ? 0 : 1;
            return;
        }
        // The service is installed below <product>\\service; bundled engines live beside it.
        // An explicit --root is used by the installer so portable/debug layouts remain unambiguous.
        var defaultRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName
            ?? AppContext.BaseDirectory;
        var root = GetArgument(args, "--root", defaultRoot);
        var data = GetArgument(args, "--data", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamHub"));
        var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
        if (console)
        {
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            await using var runtime = new HostRuntime(root, data, interactive: true);
            await runtime.RunAsync(cancellation.Token);
        }
        else
        {
            ServiceBase.Run(new HostWindowsService(root, data));
        }
    }
}

internal sealed class HostWindowsService : ServiceBase
{
    private readonly string _root, _data;
    private CancellationTokenSource? _cancellation;
    private HostRuntime? _runtime;
    private Task? _task;

    public HostWindowsService(string root, string data)
    {
        _root = root;
        _data = data;
        ServiceName = RuntimeProtocol.ServiceName;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _cancellation = new CancellationTokenSource();
        _runtime = new HostRuntime(_root, _data, interactive: false);
        _task = Task.Run(() => _runtime.RunAsync(_cancellation.Token));
        _task.ContinueWith(t =>
        {
            Directory.CreateDirectory(Path.Combine(_data, "logs"));
            File.AppendAllText(Path.Combine(_data, "logs", "service-fatal.log"), $"{DateTimeOffset.Now:O} {t.Exception}\n");
            Environment.Exit(1);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    protected override void OnStop()
    {
        try { RequestAdditionalTime(30000); } catch (InvalidOperationException) { /* Shutdown is not always stop-pending. */ }
        _cancellation?.Cancel();
        var completed = _task is null;
        try { completed = _task?.Wait(TimeSpan.FromSeconds(30)) ?? true; } catch (AggregateException) { completed = true; }
        if (!completed)
        {
            // Closing process-owned job handles also stops the engines. Do not dispose
            // semaphores and HTTP clients while an overdue cleanup is still using them.
            Directory.CreateDirectory(Path.Combine(_data, "logs"));
            File.AppendAllText(Path.Combine(_data, "logs", "service-fatal.log"), $"{DateTimeOffset.Now:O} 后台清理超过 30 秒，退出进程以回收内置组件。\n");
            Environment.Exit(1);
            return;
        }
        _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _cancellation?.Dispose();
    }

    protected override void OnShutdown() => OnStop();
}
