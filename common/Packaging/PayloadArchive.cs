using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamHub.Packaging;

// The archive may omit identical distribution files; the installed layout always
// contains separate files. Sunshine's writable display state must stay isolated.
public sealed class PayloadArchive
{
    public const string ManifestName = "组件清单.json";
    public const string MapName = ".streamhub-payload-map.json";
    private const string Remote = "components/streaming/remote/";
    private const string Tablet = "components/streaming/tablet/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Alias(string Target, string Source);
    private sealed record Map(int Format, Alias[] Aliases);
    private sealed record FileRecord(string Path, long Size, string Sha256);
    private readonly Dictionary<string, ZipArchiveEntry> _physical;
    private readonly Dictionary<string, FileRecord> _records;
    public IReadOnlyDictionary<string, ZipArchiveEntry> Files { get; }
    public int AliasCount { get; }

    private PayloadArchive(Dictionary<string, ZipArchiveEntry> physical,
        Dictionary<string, FileRecord> records, Dictionary<string, ZipArchiveEntry> files, int aliases)
    { _physical = physical; _records = records; Files = files; AliasCount = aliases; }

    public static string ValidatePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.Length > 1024 || normalized.Split('/').Any(part =>
            part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(c => c < 32 || ":*?\"<>|".Contains(c)) || IsDeviceName(part)))
            throw new InvalidDataException("Invalid payload path: " + path);
        return normalized;
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            (name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && name[3] is >= '1' and <= '9');
    }

    private static bool CanAlias(string target, string source)
    {
        if (!target.StartsWith(Tablet, StringComparison.Ordinal) || source != Remote + target[Tablet.Length..]) return false;
        var relative = target[Tablet.Length..];
        if (relative is "sunshine.exe" or "zlib1.dll") return true;
        // Only immutable distribution trees, never profile configuration/state.
        if (!(relative.StartsWith("assets/") || relative.StartsWith("tools/") || relative.StartsWith("scripts/"))) return false;
        return !relative.Split('/').Any(p => p.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("logs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".state", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".pem", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, FileRecord> ReadManifest(Stream input)
    {
        using var document = JsonDocument.Parse(input);
        var records = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = ValidatePath(item.GetProperty("path").GetString() ?? "");
            var size = item.GetProperty("size").GetInt64();
            var hash = item.GetProperty("sha256").GetString() ?? "";
            if (name is ManifestName or MapName || size < 0 || hash.Length != 64 || !hash.All(Uri.IsHexDigit) ||
                records.Count >= 100000 || !records.TryAdd(name, new(name, size, hash)))
                throw new InvalidDataException("Invalid or duplicate manifest file: " + name);
        }
        foreach (var name in records.Keys)
        {
            for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                if (records.ContainsKey(name[..slash])) throw new InvalidDataException("Payload file/directory collision.");
        }
        return records;
    }

    public static PayloadArchive Open(ZipArchive archive)
    {
        var physical = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var name = ValidatePath(directory ? entry.FullName[..^1] : entry.FullName);
            if (!paths.Add(name) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Duplicate or linked payload entry: " + name);
            if (!directory) physical.Add(name, entry);
        }
        if (!physical.TryGetValue(ManifestName, out var manifest) || manifest.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Component manifest missing or too large.");
        Dictionary<string, FileRecord> records;
        using (var input = manifest.Open()) records = ReadManifest(input);
        var files = new Dictionary<string, ZipArchiveEntry>(physical, StringComparer.OrdinalIgnoreCase);
        files.Remove(ManifestName); files.Remove(MapName);
        int aliases = 0;
        if (physical.TryGetValue(MapName, out var mapping))
        {
            if (mapping.Length > 1024 * 1024) throw new InvalidDataException("Payload map too large.");
            Map map;
            using (var input = mapping.Open()) map = JsonSerializer.Deserialize<Map>(input, Json) ?? throw new InvalidDataException("Missing payload map.");
            if (map.Format != 1 || map.Aliases is null || map.Aliases.Length > 10000) throw new InvalidDataException("Unsupported payload map.");
            foreach (var alias in map.Aliases)
            {
                if (alias is null || alias.Target is null || alias.Source is null) throw new InvalidDataException("Invalid payload alias.");
                var target = ValidatePath(alias.Target); var source = ValidatePath(alias.Source);
                if (!CanAlias(target, source) || paths.Contains(target) ||
                    !physical.TryGetValue(source, out var original) ||
                    !records.TryGetValue(target, out var to) || !records.TryGetValue(source, out var from) ||
                    to.Size != from.Size || !to.Sha256.Equals(from.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !files.TryAdd(target, original)) throw new InvalidDataException("Invalid payload alias: " + target);
                aliases++;
            }
        }
        if (files.Count != records.Count || files.Any(p => !records.TryGetValue(p.Key, out var record) || p.Value.Length != record.Size))
            throw new InvalidDataException("Payload file list or size differs from its manifest.");
        return new(physical, records, files, aliases);
    }

    public void VerifyFiles()
    {
        foreach (var group in Files.GroupBy(p => p.Value))
        {
            using var input = group.Key.Open();
            var hash = Convert.ToHexString(SHA256.HashData(input));
            if (group.Any(p => !_records[p.Key].Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Payload checksum differs: " + group.Key.FullName);
        }
    }

    public void Extract(string destination)
    {
        var root = Path.GetFullPath(destination);
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Payload destination contains a link.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Payload destination must be empty.");
        Directory.CreateDirectory(root);
        foreach (var pair in _physical.Where(p => p.Key != MapName))
        {
            var target = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            pair.Value.ExtractToFile(target, false);
        }
        foreach (var pair in Files.Where(p => !_physical.ContainsKey(p.Key)))
        {
            var target = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            var source = Path.Combine(root, ValidatePath(pair.Value.FullName).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, false); // deliberately independent files, not hard links
        }
    }

    public static (int Aliases, long OmittedBytes) Create(string stage, string archivePath, bool deduplicate = true)
    {
        var root = Path.GetFullPath(stage);
        Dictionary<string, FileRecord> records;
        using (var input = File.OpenRead(Path.Combine(root, ManifestName))) records = ReadManifest(input);
        var directories = new Stack<string>(); directories.Push(root); int actualFiles = 0;
        while (directories.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Payload source contains a link.");
                if (attributes.HasFlag(FileAttributes.Directory)) { directories.Push(entry); continue; }
                var relative = ValidatePath(Path.GetRelativePath(root, entry));
                if (relative != ManifestName && !records.ContainsKey(relative)) throw new InvalidDataException("Unlisted staged file: " + relative);
                actualFiles++;
            }
        if (actualFiles != records.Count + 1) throw new InvalidDataException("Staged file list differs from manifest.");
        var aliases = new List<Alias>(); long omitted = 0;
        // Verify both originals before eliding either: a stale manifest must not
        // silently turn two different engines into identical reconstructed ones.
        foreach (var file in records.Values)
        {
            var path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            for (var info = new FileInfo(path) as FileSystemInfo; info is not null; info = (info as FileInfo)?.Directory ?? (info as DirectoryInfo)?.Parent)
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Payload source contains a link.");
            using var input = File.OpenRead(path);
            if (input.Length != file.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged file differs from manifest: " + file.Path);
        }
        if (deduplicate)
            foreach (var file in records.Values.Where(f => f.Path.StartsWith(Tablet, StringComparison.Ordinal)))
            {
                var source = Remote + file.Path[Tablet.Length..];
                if (CanAlias(file.Path, source) && records.TryGetValue(source, out var original) && file.Size == original.Size &&
                    file.Sha256.Equals(original.Sha256, StringComparison.OrdinalIgnoreCase))
                { aliases.Add(new(file.Path, source)); omitted += file.Size; }
            }
        var targets = aliases.Select(a => a.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var name in records.Keys.Where(n => !targets.Contains(n)).Append(ManifestName).Order(StringComparer.Ordinal))
            zip.CreateEntryFromFile(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)), name, CompressionLevel.Optimal);
        if (aliases.Count > 0)
        {
            using var mapping = zip.CreateEntry(MapName, CompressionLevel.Optimal).Open();
            JsonSerializer.Serialize(mapping, new Map(1, aliases.ToArray()), Json);
        }
        return (aliases.Count, omitted);
    }
}
