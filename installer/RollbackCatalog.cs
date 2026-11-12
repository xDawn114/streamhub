using System.Security.Cryptography;
using System.Text.Json;

namespace StreamHub.Installer;

internal sealed record BackupFile(string Path, long Size, string Sha256);
internal sealed record RollbackRecord(string Product, string Transaction, string InstallRoot, BackupFile[] Files);

/// <summary>Only a verified snapshot inside the protected installer directory can become a rollback source.</summary>
internal sealed class RollbackCatalog(string installerRoot, string installRoot)
{
    private const string Product = "StreamHub.PC.0b1e5f94";
    private readonly string _catalogRoot = System.IO.Path.GetFullPath(installerRoot);
    private readonly string _installRoot = System.IO.Path.GetFullPath(installRoot);
    private string Pointer => System.IO.Path.Combine(_catalogRoot, "latest-rollback.json");
    public RollbackRecord Capture(string transaction)
    {
        var source = BackupPath(transaction); EnsureOwned(source);
        var files = Enumerate(source).Select(path => new BackupFile(System.IO.Path.GetRelativePath(source,path),
            new FileInfo(path).Length, Hash(path))).ToArray();
        if (files.Length == 0) throw new IOException("上版备份为空，无法登记回滚。");
        return new(Product, transaction, _installRoot, files);
    }
    public void Commit(RollbackRecord record)
    {
        _ = Verify(record);
        Directory.CreateDirectory(_catalogRoot); NoLinks(_catalogRoot);
        var temp = System.IO.Path.Combine(_catalogRoot, "rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(record)); File.Move(temp, Pointer, true);
    }
    public RollbackRecord Read()
    {
        NoLinks(_catalogRoot);
        if (!File.Exists(Pointer) || new FileInfo(Pointer).Length > 4 * 1024 * 1024) throw new IOException("没有可用的上版备份。");
        NoLinks(Pointer);
        var record = JsonSerializer.Deserialize<RollbackRecord>(File.ReadAllText(Pointer)) ?? throw new IOException("回滚记录无效。");
        _ = Verify(record); return record;
    }
    public string Verify(RollbackRecord record)
    {
        if (record.Product != Product || !string.Equals(record.InstallRoot, _installRoot, StringComparison.OrdinalIgnoreCase) ||
            record.Files is not { Length: > 0 and <= 10000 }) throw new IOException("回滚记录不属于此安装目录。");
        var source = BackupPath(record.Transaction); EnsureOwned(source);
        var actual = Enumerate(source).Select(p => System.IO.Path.GetRelativePath(source,p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actual.Count != record.Files.Length) throw new IOException("上版备份文件数量发生变化。");
        foreach (var item in record.Files)
        {
            if (!actual.Remove(item.Path)) throw new IOException("回滚清单路径无效或重复。");
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(source,item.Path));
            if (!path.StartsWith(source + System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(path).Length != item.Size || !Hash(path).Equals(item.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new IOException("上版备份校验失败：" + item.Path);
        }
        return source;
    }
    private string BackupPath(string transaction)
    {
        if (!Guid.TryParseExact(transaction,"N",out _)) throw new IOException("回滚事务路径无效。");
        var path=System.IO.Path.Combine(_catalogRoot,transaction,"backup"); NoLinks(_catalogRoot); NoLinks(System.IO.Path.GetDirectoryName(path)!); NoLinks(path); return path;
    }
    private static void EnsureOwned(string source)
    {
        using var marker=JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(source,".streamhub-install.json")));
        if (marker.RootElement.GetProperty("productId").GetString()!=Product) throw new IOException("备份不属于我控制。");
    }
    private static IEnumerable<string> Enumerate(string directory)
    {
        NoLinks(directory);
        foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            NoLinks(entry);
            if(Directory.Exists(entry)) { foreach(var child in Enumerate(entry)) yield return child; }
            else yield return entry;
        }
    }
    private static void NoLinks(string path)
    { if ((Directory.Exists(path)||File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("回滚路径包含链接，操作已停止。"); }
    private static string Hash(string path) { using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)); }
}
