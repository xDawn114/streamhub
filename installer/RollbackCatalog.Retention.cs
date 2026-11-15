using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamHub.Installer;

internal sealed partial class RollbackCatalog
{
    private const int RetainedBackups = 2;
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const string MarkerName = ".streamhub-install.json";
    private static readonly Regex NamedTransaction = new(
        @"^(?<version>[0-9]+\.[0-9]+(?:\.[0-9]+){0,2}|unknown)-(?<time>[0-9]{8}T[0-9]{9}Z)(?:-[1-9][0-9]{0,3})?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string CreateTransaction(string incomingVersion, DateTimeOffset? now = null)
    {
        var version = incomingVersion;
        var marker = Path.Combine(_installRoot, MarkerName);
        NoLinks(_installRoot);
        if (File.Exists(marker))
        {
            using var document = ReadMarker(_installRoot);
            var data = document.RootElement;
            version = data.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
                && Version.TryParse(value.GetString(), out var parsed) ? parsed.ToString() : "unknown";
            if (version != "unknown" && Version.Parse(version).Revision < 0
                && data.TryGetProperty("versionCode", out var code) && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var number) && number >= 0)
                version += "." + number.ToString(CultureInfo.InvariantCulture);
        }
        if (version != "unknown" && !Version.TryParse(version, out _)) throw new IOException("备份版本无效。");
        var prefix = version + "-" + (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        NoLinks(_catalogRoot);
        Directory.CreateDirectory(_catalogRoot);
        // Production callers hold Global\StreamHub.InstallTransaction throughout installation.
        for (var suffix = 0; suffix < 10000; suffix++)
        {
            var name = prefix + (suffix == 0 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture));
            var path = TransactionPath(name);
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return name;
        }
        throw new IOException("无法建立独立的安装备份目录。");
    }

    /// <summary>Run only after the installation or rollback has committed successfully.</summary>
    public void Prune(Action<string> log)
    {
        void Notice(string message)
        {
            try { log(message); }
            catch (Exception error) when (CleanupError(error)) { /* Cleanup logging must not undo a committed installation. */ }
        }
        try
        {
            NoLinks(_catalogRoot);
            NoLinks(Pointer);
            if (!File.Exists(Pointer)) return; // A first installation has no previous version to retain.
            if (new FileInfo(Pointer).Length > 4 * 1024 * 1024)
                throw new IOException("没有有效回滚指针，已保留现有备份。");
            var current = JsonSerializer.Deserialize<RollbackRecord>(File.ReadAllText(Pointer));
            if (current is null || current.Product != Product || !SameInstall(current.InstallRoot)
                || current.Files is not { Length: > 0 and <= 10000 })
                throw new IOException("回滚指针无效，已保留现有备份。");
            var protectedBackup = BackupPath(current.Transaction);
            using (var marker = ReadMarker(protectedBackup))
                if (!MarkerMatchesInstall(marker)) throw new IOException("回滚备份位置不匹配，已保留现有备份。");

            var candidates = new List<(string Name, DateTime CreatedUtc)>();
            foreach (var directory in Directory.EnumerateDirectories(_catalogRoot))
            {
                var name = Path.GetFileName(directory);
                if (!IsTransactionName(name)) continue;
                try
                {
                    var backup = BackupPath(name);
                    using var marker = ReadMarker(backup);
                    if (MarkerMatchesInstall(marker))
                        candidates.Add((name, TransactionTime(name) ?? Directory.GetCreationTimeUtc(directory)));
                }
                catch (Exception error) when (CleanupError(error))
                { Notice("保留无法确认的备份目录 " + name + "：" + error.Message); }
            }

            var keep = candidates.Where(item => !item.Name.Equals(current.Transaction, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(RetainedBackups - 1).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            keep.Add(current.Transaction);
            foreach (var item in candidates.Where(item => !keep.Contains(item.Name)))
            {
                try
                {
                    var backup = BackupPath(item.Name);
                    using (var marker = ReadMarker(backup))
                        if (!MarkerMatchesInstall(marker)) throw new IOException("备份归属发生变化。");
                    // Preflight every descendant; never follow junctions or delete the installer root.
                    var files = Enumerate(backup).ToArray();
                    var markerPath = Path.Combine(backup, MarkerName);
                    // Keep the ownership marker until other files are gone so a busy backup can be retried.
                    foreach (var file in files.Where(file => !file.Equals(markerPath, StringComparison.OrdinalIgnoreCase)))
                        File.Delete(file);
                    Directory.Delete(backup, true);
                    var transaction = TransactionPath(item.Name);
                    if (!Directory.EnumerateFileSystemEntries(transaction).Any()) Directory.Delete(transaction);
                    Notice("已清理较早的程序备份：" + item.Name);
                }
                catch (Exception error) when (CleanupError(error))
                { Notice("安装已完成，旧备份暂未清理 " + item.Name + "：" + error.Message); }
            }
        }
        catch (Exception error) when (CleanupError(error))
        { Notice("安装已完成，备份清理已跳过：" + error.Message); }
    }

    private string TransactionPath(string transaction)
    {
        if (!IsTransactionName(transaction)) throw new IOException("回滚事务路径无效。");
        var path = Path.GetFullPath(Path.Combine(_catalogRoot, transaction));
        if (!string.Equals(Path.GetDirectoryName(path), _catalogRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份路径超出安装器目录。");
        NoLinks(_catalogRoot);
        NoLinks(path);
        return path;
    }

    private static bool IsTransactionName(string transaction) => transaction is { Length: > 0 and <= 80 }
        && (Guid.TryParseExact(transaction, "N", out _) || TransactionTime(transaction) is not null);

    private static DateTime? TransactionTime(string transaction)
    {
        if (transaction.Length > 80) return null;
        var match = NamedTransaction.Match(transaction);
        if (!match.Success || (match.Groups["version"].Value != "unknown" && !Version.TryParse(match.Groups["version"].Value, out _))) return null;
        return DateTime.TryParseExact(match.Groups["time"].Value, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;
    }

    private static JsonDocument ReadMarker(string backup)
    {
        NoLinks(backup);
        var marker = Path.Combine(backup, MarkerName);
        NoLinks(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 16 * 1024) throw new IOException("备份缺少有效产品标记。");
        var document = JsonDocument.Parse(File.ReadAllText(marker));
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("productId", out var product)
            && product.ValueKind == JsonValueKind.String && product.GetString() == Product) return document;
        document.Dispose();
        throw new IOException("备份不属于我控制。");
    }

    private bool MarkerMatchesInstall(JsonDocument marker) => marker.RootElement.TryGetProperty("root", out var root)
        && root.ValueKind == JsonValueKind.String && SameInstall(root.GetString());

    private bool SameInstall(string? root) => string.Equals(root, _installRoot, StringComparison.OrdinalIgnoreCase);
    private static bool CleanupError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException;
}
