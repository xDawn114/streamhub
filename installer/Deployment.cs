using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using StreamHub.Host.Networking;

namespace StreamHub.Installer;

internal sealed partial class Deployment
{
    private const string ProductId = "StreamHub.PC.0b1e5f94";
    private const string ServiceName = "StreamHubHost";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\StreamHub";
    private const string Marker = ".streamhub-install.json";
    private readonly InstallOptions _options;
    private readonly Action<string> _progress;
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamHub");
    private readonly string _root;
    private readonly string _ownerSid = InstallationSecurity.InteractiveOwnerSid();
    private bool _dataProtected;

    internal Deployment(InstallOptions options, Action<string> progress)
    {
        _options = options;
        _progress = progress;
        _root = ValidateRoot(options.InstallRoot);
    }

    public void Install()
    {
        if (_root.Any(character => character > 127))
            throw new IOException("内置串流引擎需要英文安装路径，请使用 C:\\Program Files\\StreamHub 或其他英文文件夹。软件入口和界面仍为中文。");
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("StreamHub.Payload.zip")
            ?? throw new InvalidOperationException("此文件为卸载程序，不包含安装资源。请运行“我控制-电脑端安装.exe”。");
        var existing = File.Exists(Path.Combine(_root, Marker));
        ValidateExisting(existing);
        ValidateServiceOwnership();
        EnsureProtectedData();
        var transaction = Guid.NewGuid().ToString("N");
        var work = Path.Combine(_dataRoot, "installer", transaction);
        var stage = Path.Combine(work, "stage");
        var backup = Path.Combine(work, "backup");
        Directory.CreateDirectory(stage);
        Log("正在验证和展开安装资源……");
        Extract(payload, stage);
        VerifyManifest(stage);
        using var maintenance = _options.RequireIdle ? AcquireMaintenance() : null;
        var serviceWasRunning = ServiceRunning();
        var committed = false;
        var filesTouched = false;
        RollbackRecord? rollbackRecord = null;
        var catalog = new RollbackCatalog(Path.Combine(_dataRoot, "installer"), _root);
        try
        {
            Log("正在更新我控制程序……");
            StopOwnedProcesses();
            Directory.CreateDirectory(_root);
            if (existing) { CopyTree(_root, backup); rollbackRecord = catalog.Capture(transaction); }
            filesTouched = true;
            if (existing) DeleteOwnedTree(_root);
            CopyTree(stage, _root);
            File.WriteAllText(Path.Combine(_root, Marker), JsonSerializer.Serialize(new { productId = ProductId, version = ProductVersion.Name, versionCode = ProductVersion.Code, installedUtc = DateTimeOffset.UtcNow, root = _root }), Encoding.UTF8);
            RegisterService();
            ConfigureFirewall(true);
            ConfigureStartup(_options.AutoStart);
            RegisterShortcuts();
            RegisterUninstall();
            if (_options.InstallDriver) EnsureDriver();
            Log("正在启动我控制后台……");
            using (var service = new ServiceController(ServiceName))
            {
                if (service.Status != ServiceControllerStatus.Running) service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(35));
            }
            if (rollbackRecord is not null) catalog.Commit(rollbackRecord);
            committed = true;
            Log("安装完成：" + _root);
            if (existing) Log("上个版本备份：" + backup);
        }
        catch
        {
            Log("安装未完成，正在恢复原状态……");
            try
            {
                StopOwnedProcesses();
                if (existing)
                {
                    if (filesTouched && Directory.Exists(backup))
                    {
                        DeleteOwnedTree(_root);
                        Directory.CreateDirectory(_root);
                        CopyTree(backup, _root);
                        RegisterService();
                    }
                    if (serviceWasRunning && !ServiceRunning()) using (var service = new ServiceController(ServiceName)) service.Start();
                }
                else
                {
                    Run("sc.exe", ["delete", ServiceName], [0, 1060, 1072]);
                    ConfigureFirewall(false);
                    ConfigureStartup(false);
                    Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
                    DeleteShortcuts();
                    if (filesTouched) DeleteOwnedTree(_root);
                }
            }
            catch (Exception rollback) { Log("自动恢复需要检查：" + rollback.Message); }
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (committed && !existing && Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    public void Uninstall()
    {
        ValidateExisting(true);
        using (var registered = Registry.LocalMachine.OpenSubKey(UninstallKey))
            if (!string.Equals(registered?.GetValue("InstallLocation") as string, _root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("此位置不是已登记的我控制安装目录，卸载已停止。");
        ValidateServiceOwnership();
        Log("正在停止我控制后台……");
        StopOwnedProcesses();
        RestoreLegacyNetwork();
        Run("sc.exe", ["delete", ServiceName], [0, 1060, 1072]);
        ConfigureFirewall(false);
        ConfigureStartup(false);
        DeleteShortcuts();
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得卸载程序路径。");
        if (Path.GetFullPath(executable).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var cleanupRoot = Path.Combine(Path.GetTempPath(), "StreamHubUninstall", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cleanupRoot);
            var cleanup = Path.Combine(cleanupRoot, "卸载清理.exe");
            File.Copy(executable, cleanup);
            var start = new ProcessStartInfo(cleanup) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--quiet", "--cleanup-parent", Environment.ProcessId.ToString(), "--install-dir", _root }) start.ArgumentList.Add(arg);
            Process.Start(start);
        }
        else DeleteOwnedTree(_root);
        Log("程序已卸载；共享显示驱动、设备绑定和设置保留在原位置。");
    }

    internal void VerifyPackage()
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("StreamHub.Payload.zip")
            ?? throw new InvalidOperationException("此程序不包含安装资源。");
        var stage = Path.Combine(Path.GetTempPath(), "StreamHubVerify", Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(stage); Extract(payload, stage); VerifyManifest(stage); }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private void ValidateExisting(bool mustExist)
    {
        var marker = Path.Combine(_root, Marker);
        if (File.Exists(marker))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("productId").GetString() != ProductId) throw new IOException("此文件夹不属于我控制，操作已停止。");
        }
        else if (mustExist || (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any()))
            throw new IOException("请选择一个空文件夹，或现有我控制的安装文件夹。");
    }

    internal static string ValidateRoot(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var forbidden = new[] { Path.GetPathRoot(full), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
        if (forbidden.Any(p => string.Equals(p?.TrimEnd(Path.DirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase))) throw new IOException("安装位置必须是专用子文件夹，不能选择磁盘或系统文件夹本身。");
        for (var p = new DirectoryInfo(full); p is not null; p = p.Parent)
            if (p.Exists && p.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装位置不能经过目录链接。");
        return full;
    }

    private void ValidateServiceOwnership()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        if (key?.GetValue("ImagePath") is string command && !command.Contains(Path.Combine(_root, "service", "串流中心后台.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("同名后台服务属于另一安装位置，请先检查现有安装位置，或选择原安装位置。现有服务未修改。");
    }

    private void RegisterService()
    {
        var exe = Path.Combine(_root, "service", "串流中心后台.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("安装资源缺少后台服务。", exe);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        var command = "\"" + exe + "\" --root \"" + _root + "\" --data \"" + _dataRoot + "\"";
        Run("sc.exe", [key is null ? "create" : "config", ServiceName, "binPath=", command, "start=", "auto", "DisplayName=", "我控制后台"]);
        Run("sc.exe", ["description", ServiceName, "管理内置串流、远程连接与平板副屏；关闭界面后继续待机。"]);
        Run("sc.exe", ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"]);
    }

    private bool ServiceRunning()
    {
        try { using var service = new ServiceController(ServiceName); return service.Status == ServiceControllerStatus.Running; }
        catch { return false; }
    }

    private void StopOwnedProcesses()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(35));
            }
        }
        catch (InvalidOperationException) { }
        var ownedExecutables = new[]
        {
            Path.Combine(_root, "我控制.exe"),
            Path.Combine(_root, "梨子控制.exe"),
            Path.Combine(_root, "串流中心.exe"),
            // Keep recognizing the old layout while upgrading so a stale worker
            // cannot survive the switch to isolated per-profile directories.
            Path.Combine(_root, "components", "streaming", "sunshine.exe"),
            Path.Combine(_root, "components", "streaming", "remote", "sunshine.exe"),
            Path.Combine(_root, "components", "streaming", "tablet", "sunshine.exe"),
            Path.Combine(_root, "components", "network", "tailscaled.exe")
        };
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && ownedExecutables.Contains(process.MainModule?.FileName, StringComparer.OrdinalIgnoreCase))
                    { process.Kill(true); process.WaitForExit(10000); }
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private void ConfigureStartup(bool enabled)
    {
        using (var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
        {
            machine?.DeleteValue("我控制", false);
            machine?.DeleteValue("梨子控制", false);
            machine?.DeleteValue("串流中心", false);
        }
        using var registered = Registry.LocalMachine.OpenSubKey(UninstallKey);
        var previousSid = registered?.GetValue("StartupUserSid") as string;
        foreach (var sid in new[] { _ownerSid, previousSid }.Where(s => !string.IsNullOrEmpty(s)).Distinct())
        {
            using var run = Registry.Users.OpenSubKey(sid + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("我控制", false);
            run?.DeleteValue("梨子控制", false);
            run?.DeleteValue("串流中心", false);
            run?.DeleteValue("StreamHub", false);
        }
        if (enabled)
        {
            using var key = Registry.Users.CreateSubKey(_ownerSid + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue("我控制", "\"" + Path.Combine(_root, "我控制.exe") + "\" --background");
        }
    }

    private void ConfigureFirewall(bool enabled)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        foreach (var obsolete in new[] { "StreamHub-Streaming-TCP", "StreamHub-Streaming-UDP", "我控制 · 画面与控制", "我控制 · 音视频" })
            try { policy.Rules.Remove(obsolete); } catch (System.Runtime.InteropServices.COMException) { }
        var definitions = new[]
        {
            (Name:"StreamHub-Remote-TCP", Title:"我控制 · 远程主屏控制", Program:Path.Combine(_root,"components","streaming","remote","sunshine.exe"), Protocol:6, Ports:"48984,48989,49010", Scope:"LocalSubnet,100.64.0.0/10"),
            (Name:"StreamHub-Remote-UDP", Title:"我控制 · 远程主屏音视频", Program:Path.Combine(_root,"components","streaming","remote","sunshine.exe"), Protocol:17, Ports:"48998-49000,49002,49010", Scope:"LocalSubnet,100.64.0.0/10"),
            (Name:"StreamHub-Tablet-TCP", Title:"我控制 · 平板副屏控制", Program:Path.Combine(_root,"components","streaming","tablet","sunshine.exe"), Protocol:6, Ports:"49084,49089,49110", Scope:"LocalSubnet,100.64.0.0/10"),
            (Name:"StreamHub-Tablet-UDP", Title:"我控制 · 平板副屏音视频", Program:Path.Combine(_root,"components","streaming","tablet","sunshine.exe"), Protocol:17, Ports:"49098-49100,49102,49110", Scope:"LocalSubnet,100.64.0.0/10"),
            (Name:"StreamHub-Control-TCP", Title:"我控制 · 可信设备连接", Program:Path.Combine(_root,"service","串流中心后台.exe"), Protocol:6, Ports:"48980", Scope:"LocalSubnet,100.64.0.0/10"),
            (Name:"StreamHub-Product-Control-TCP", Title:"我控制 · 设备绑定与副屏请求", Program:Path.Combine(_root,"service","串流中心后台.exe"), Protocol:6, Ports:"49199", Scope:"LocalSubnet,100.64.0.0/10"),
            (Name:"StreamHub-Discovery-UDP", Title:"我控制 · 附近电脑发现", Program:Path.Combine(_root,"service","串流中心后台.exe"), Protocol:17, Ports:"5353", Scope:"LocalSubnet"),
            (Name:"StreamHub-Network-UDP", Title:"我控制 · 异地直连", Program:Path.Combine(_root,"components","network","tailscaled.exe"), Protocol:17, Ports:"41641", Scope:"*")
        };
        foreach (var item in definitions)
        {
            // Rule names are owned by this product; never modify an upstream rule.
            try { policy.Rules.Remove(item.Name); } catch (System.Runtime.InteropServices.COMException) { }
            try { policy.Rules.Remove(item.Title); } catch (System.Runtime.InteropServices.COMException) { }
            if (!enabled) continue;
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            rule.Name = item.Title;
            rule.Description = item.Title;
            rule.Grouping = "我控制";
            rule.ApplicationName = item.Program;
            rule.Protocol = item.Protocol;
            rule.LocalPorts = item.Ports;
            rule.RemoteAddresses = item.Scope;
            rule.Direction = 1;
            rule.Action = 1;
            rule.Profiles = 7;
            rule.Enabled = true;
            policy.Rules.Add(rule);
        }
    }

    private void RegisterShortcuts()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "我控制");
        Directory.CreateDirectory(folder);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(folder, "我控制.lnk"));
        shortcut.TargetPath = Path.Combine(_root, "我控制.exe");
        shortcut.WorkingDirectory = _root;
        shortcut.Description = "远程控制、串流与平板副屏";
        shortcut.Save();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var oldName in new[] { "串流中心.lnk", "梨子控制.lnk" })
        {
            var oldPath = Path.Combine(desktop, oldName);
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        dynamic desktopShortcut = shell.CreateShortcut(Path.Combine(desktop, "我控制.lnk"));
        desktopShortcut.TargetPath = Path.Combine(_root, "我控制.exe");
        desktopShortcut.WorkingDirectory = _root;
        desktopShortcut.IconLocation = Path.Combine(_root, "我控制.exe") + ",0";
        desktopShortcut.Description = "远程控制、串流与平板副屏";
        desktopShortcut.Save();
        dynamic uninstall = shell.CreateShortcut(Path.Combine(folder, "卸载我控制.lnk"));
        uninstall.TargetPath = Path.Combine(_root, "卸载串流中心.exe");
        uninstall.Arguments = "--uninstall --install-dir \"" + _root + "\"";
        uninstall.Save();
    }

    private static void DeleteShortcuts()
    {
        foreach (var folderName in new[] { "我控制", "串流中心" })
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), folderName);
            foreach (var file in new[] { "我控制.lnk", "卸载我控制.lnk", "串流中心.lnk", "卸载串流中心.lnk" })
            { var path = Path.Combine(folder, file); if (File.Exists(path)) File.Delete(path); }
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var file in new[] { "我控制.lnk", "梨子控制.lnk", "串流中心.lnk" })
        {
            var path = Path.Combine(desktop, file);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void RegisterUninstall()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "我控制");
        using var installed = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, Marker)));
        key.SetValue("DisplayVersion", installed.RootElement.GetProperty("version").GetString() ?? ProductVersion.Name);
        key.SetValue("Publisher", "我控制");
        key.SetValue("InstallLocation", _root);
        key.SetValue("StartupUserSid", _ownerSid);
        key.SetValue("DisplayIcon", Path.Combine(_root, "我控制.exe"));
        key.SetValue("UninstallString", "\"" + Path.Combine(_root, "卸载串流中心.exe") + "\" --uninstall --install-dir \"" + _root + "\"");
        key.SetValue("QuietUninstallString", "\"" + Path.Combine(_root, "卸载串流中心.exe") + "\" --quiet --uninstall --install-dir \"" + _root + "\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024), RegistryValueKind.DWord);
    }

    private void EnsureDriver()
    {
        var devices = Run("pnputil.exe", ["/enum-devices", "/class", "Display", "/ids"]);
        if (devices.Contains(@"ROOT\MTTVDD", StringComparison.OrdinalIgnoreCase))
        { Log("已存在虚拟显示驱动，保留当前驱动和显示配置。"); return; }
        var driverRoot = Path.Combine(_root, "components", "display");
        var shared = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "VirtualDisplayDriver");
        if (!Directory.Exists(shared)) CopyTree(Path.Combine(driverRoot, "VDD"), shared);
        Log("正在安装原厂签名副屏驱动；请处理 Windows 出现的驱动确认。");
        if (DriverInstaller.Install(Path.Combine(driverRoot, "VDD", "MttVDD.inf"))) Log("显示驱动已部署；Windows 建议重启电脑后使用副屏。");
    }

    private static void Extract(Stream stream, string destination)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装资源含有无效路径。");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static void VerifyManifest(string stage)
    {
        var manifestPath = Path.Combine(stage, "组件清单.json");
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!json.RootElement.TryGetProperty("streamingLayout", out var layout) || layout.GetString() != "isolated-v2")
            throw new IOException("安装资源未使用独立主屏与副屏引擎布局。");
        if (File.Exists(Path.Combine(stage, "components", "streaming", "sunshine.exe")))
            throw new IOException("安装资源中不应保留共享串流引擎。");
        using var buildRecord = Assembly.GetExecutingAssembly().GetManifestResourceStream("StreamHub.Sunshine.build.json")
            ?? throw new IOException("安装器缺少固定的原生引擎构建记录。");
        var engineHash = StreamHub.Packaging.EngineIntegrity.ExpectedHash(buildRecord);
        foreach (var profile in new[] { "remote", "tablet" })
        {
            var directory = Path.Combine(stage, "components", "streaming", profile);
            using var engine = File.OpenRead(Path.Combine(directory, "sunshine.exe"));
            StreamHub.Packaging.EngineIntegrity.Verify(engine, engineHash, profile);
            if (!File.Exists(Path.Combine(directory, "zlib1.dll")) || !Directory.Exists(Path.Combine(directory, "assets")))
                throw new IOException("内置串流组件资源不完整：" + profile);
        }
        foreach (var item in json.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString()!;
            var file = Path.GetFullPath(Path.Combine(stage, relative));
            if (!file.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("清单包含无效路径。");
            using var content = File.OpenRead(file);
            var actual = Convert.ToHexString(SHA256.HashData(content));
            if (!actual.Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("安装资源校验失败：" + relative);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录包含文件链接，已停止操作。");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录包含目录链接，已停止操作。");
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void DeleteOwnedTree(string root)
    {
        if (!Directory.Exists(root)) return;
        VerifyNoLinks(root);
        Directory.Delete(root, true);
    }

    private static void VerifyNoLinks(string root)
    {
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录包含链接，自动清理已停止。");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录包含链接，自动清理已停止。");
            if (attributes.HasFlag(FileAttributes.Directory)) VerifyNoLinks(entry);
        }
    }

    private string Run(string file, string[] arguments, int[]? expected = null)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法运行安装步骤：" + file);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new System.TimeoutException("安装步骤等待超时：" + Path.GetFileName(file)); }
        var text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (!(expected ?? [0]).Contains(process.ExitCode)) throw new IOException(Path.GetFileName(file) + " 返回 " + process.ExitCode + "：" + text.Trim());
        return text;
    }

    private void Log(string message)
    {
        _progress(message);
        EnsureProtectedData();
        Directory.CreateDirectory(Path.Combine(_dataRoot, "logs"));
        File.AppendAllText(Path.Combine(_dataRoot, "logs", "安装.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine, Encoding.UTF8);
    }

    private void EnsureProtectedData()
    {
        if (_dataProtected) return;
        InstallationSecurity.ProtectData(_dataRoot, _ownerSid);
        _dataProtected = true;
    }

    private void RestoreLegacyNetwork()
    {
        var handover = new NetworkHandover(_dataRoot);
        if (!handover.IsHandedOver) return;
        using var original = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tailscale");
        if (original is null) { Log("原独立组网已被卸载，保留登录数据，不恢复已移除的服务。"); return; }
        Log("正在恢复接管前的原组网启动方式……");
        handover.RollbackAsync(_ => Task.CompletedTask).GetAwaiter().GetResult();
    }

    internal static void WriteFailure(InstallOptions options, Exception exception)
    {
        try { new Deployment(options, _ => { }).Log(exception.ToString()); } catch { }
    }

    internal static int Cleanup(InstallOptions options)
    {
        try
        {
            var deployment = new Deployment(options, _ => { });
            deployment.ValidateExisting(true);
            try { using var parent = Process.GetProcessById(options.CleanupParent); parent.WaitForExit(60000); } catch (ArgumentException) { }
            DeleteOwnedTree(deployment._root);
            return 0;
        }
        catch { return 1; }
    }
}
