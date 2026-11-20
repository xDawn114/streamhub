using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace StreamHub.Installer;

internal sealed record InstallationLocation(string? Root, string? Error, bool Incomplete = false)
{
    internal const string ProductId = "StreamHub.PC.0b1e5f94";
    internal const string Marker = ".streamhub-install.json";
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\StreamHub";
    internal const string ServiceName = "StreamHubHost";

    internal bool CanRepair => Root is not null && Error is null;

    internal static InstallationLocation Read()
    {
        try
        {
            using var registered = Registry.LocalMachine.OpenSubKey(UninstallKey);
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            return Resolve(registered?.GetValue("InstallLocation") as string,
                service is null ? null : service.GetValue("ImagePath") as string ?? "", HasProductMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(null, "无法读取现有安装位置，请重新打开安装程序。");
        }
    }

    internal static InstallationLocation Resolve(string? registeredRoot, string? serviceCommand, Func<string, bool> hasMarker)
    {
        var registered = Normalize(registeredRoot);
        var service = ServiceRoot(serviceCommand);
        if (serviceCommand is not null && service is null)
            return new(registered, "无法识别现有后台服务的位置，请先修复原安装。");
        if (registeredRoot is not null && registered is null)
            return new(service, "原安装位置记录无效，请先修复原安装。");
        if (service is not null && registered is not null && !Same(service, registered))
            return new(service, $"安装位置记录不一致。\r\n后台服务：{service}\r\n安装目录：{registered}");
        var root = service ?? registered;
        return new(root, null, root is not null && !hasMarker(root));
    }

    internal string SelectRoot(string requested, bool explicitlySelected) =>
        explicitlySelected ? requested : Root ?? requested;

    internal void ValidateDestination(string root)
    {
        if (Error is not null) throw new IOException(Error);
        if (Root is not null && !Same(root, Root))
            throw new IOException("我控制已安装在：" + Root + "\r\n请使用原位置更新或修复。");
        if (File.Exists(Path.Combine(root, Marker)))
        {
            if (!HasProductMarker(root)) throw new IOException("此文件夹不属于我控制：" + root);
        }
        else if (File.Exists(root) || (Root is null && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
            throw new IOException("此文件夹不是空文件夹：" + root + "\r\n请选择空文件夹安装。");
    }

    internal static bool HasProductMarker(string root)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, Marker)));
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("productId", out var product) &&
                product.ValueKind == JsonValueKind.String && product.GetString() == ProductId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal static bool Same(string left, string right) =>
        Normalize(left) is { } full && string.Equals(full, Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    internal static string? ServiceRoot(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        // Use Windows' argument parser so spaces and quoting match service startup.
        var arguments = CommandLineToArgvW(Environment.ExpandEnvironmentVariables(command), out var count);
        if (arguments == IntPtr.Zero) return null;
        try
        {
            var args = Enumerable.Range(0, count).Select(i =>
                Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * IntPtr.Size)) ?? "").ToArray();
            if (args.Length == 0 || Normalize(args[0]) is not { } executable ||
                !string.Equals(Path.GetFileName(executable), "串流中心后台.exe", StringComparison.OrdinalIgnoreCase)) return null;
            var directory = Path.GetDirectoryName(executable);
            if (!string.Equals(Path.GetFileName(directory), "service", StringComparison.OrdinalIgnoreCase)) return null;
            var root = Path.GetDirectoryName(directory);
            if (root is null) return null;
            var rootArgumentSeen = false;
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].Equals("--root", StringComparison.OrdinalIgnoreCase)) continue;
                if (rootArgumentSeen || ++i >= args.Length || !Same(root, args[i])) return null;
                rootArgumentSeen = true;
            }
            return root;
        }
        finally { _ = LocalFree(arguments); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
