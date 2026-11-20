using System.Security.Cryptography;
using System.Text.Json;

namespace StreamHub.Installer;

// Replace program files in place so an open Explorer window and unrelated files
// do not require deleting the installation directory itself.
internal sealed class InstallationFiles(string target, string source, string backup)
{
    private HashSet<string> _originalFiles = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _originalDirectories = new(StringComparer.OrdinalIgnoreCase);
    private string[] _incoming = [];
    private string[] _obsolete = [];
    private bool _snapshot;
    private bool _existed;
    public bool CanRollback { get; private set; }

    public void Snapshot()
    {
        CheckTree(source);
        if (Directory.Exists(backup)) throw new IOException("安装备份目录已存在。");
        _existed = Directory.Exists(target);
        CheckTree(target);
        _incoming = Files(source).Append(InstallationLocation.Marker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_existed)
        {
            _originalFiles = Files(target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _originalDirectories = Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(target, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Copy(target, backup);
            CanRollback = HasCompleteSnapshot();
            _obsolete = FindObsolete();
        }
        _snapshot = true;
    }

    public void Apply()
    {
        if (!_snapshot) throw new InvalidOperationException("安装文件尚未备份。");
        CheckTree(target);
        Copy(source, target);
        foreach (var relative in _obsolete) Retry(() => File.Delete(Inside(target, relative)));
    }

    public void Restore()
    {
        if (!_snapshot) return;
        CheckTree(target);
        foreach (var relative in _incoming.Where(path => !_originalFiles.Contains(path)))
        {
            var path = Inside(target, relative);
            if (File.Exists(path)) Retry(() => File.Delete(path));
        }
        if (_existed) Copy(backup, target);
        if (!Directory.Exists(target)) return;
        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!_originalDirectories.Contains(Path.GetRelativePath(target, directory)) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        if (!_existed && !Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
    }

    private string[] FindObsolete()
    {
        var path = Path.Combine(backup, "组件清单.json");
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];
            var incoming = _incoming.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var obsolete = new List<string>();
            foreach (var item in files.EnumerateArray())
            {
                if (!item.TryGetProperty("path", out var name) || name.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String) continue;
                var relative = Path.GetRelativePath(backup, Inside(backup, name.GetString()!));
                if (incoming.Contains(relative) || !_originalFiles.Contains(relative)) continue;
                using var content = File.OpenRead(Inside(backup, relative));
                if (Convert.ToHexString(SHA256.HashData(content)).Equals(hash.GetString(), StringComparison.OrdinalIgnoreCase))
                    obsolete.Add(relative);
            }
            return obsolete.ToArray();
        }
        catch (Exception error) when (error is JsonException or IOException or ArgumentException or InvalidOperationException)
        {
            // An incomplete old manifest cannot authorize removing any old files.
            return [];
        }
    }

    private bool HasCompleteSnapshot()
    {
        if (!InstallationLocation.HasProductMarker(backup)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(backup, "组件清单.json")));
            var files = document.RootElement.GetProperty("files");
            if (files.GetArrayLength() == 0) return false;
            foreach (var item in files.EnumerateArray())
            {
                var path = Inside(backup, item.GetProperty("path").GetString()!);
                if (!File.Exists(path)) return false;
                using var content = File.OpenRead(path);
                if (content.Length != item.GetProperty("size").GetInt64() ||
                    !Convert.ToHexString(SHA256.HashData(content)).Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    private static string[] Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path)).ToArray();

    private static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathFullyQualified(relative) || relative.Contains(':') ||
            !full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("安装文件路径无效。");
        return full;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            Retry(() => File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true));
        foreach (var directory in Directory.EnumerateDirectories(from)) Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException error) when (attempt < 20 && (error.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(100); }
        }
    }

    private static void CheckTree(string root)
    {
        if (!Directory.Exists(root)) return;
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录包含链接。");
        foreach (var item in Directory.EnumerateFileSystemEntries(root))
        {
            if (File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录包含链接。");
            if (Directory.Exists(item)) CheckTree(item);
        }
    }
}
