using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

public sealed record TransferDevice(string ClientId, string Name, string KeyHash, bool Allowed = false,
    bool UploadAllowed = true, bool DeleteAllowed = false);
public sealed record TransferItem(string Id, string ClientId, string Direction, string Name, long Size,
    string Sha256, long Offset = 0, string State = "receiving", string? PublishedPath = null, string? DestinationTemp = null,
    bool SharedSource = false, string? BrowseRootId = null);
public sealed record BrowseRoot(string Id, string Name, string Path, string OwnerSid);
public sealed record TransferSettings(bool Enabled = false, string ReceiveDirectory = "", bool BrowseEnabled = false,
    BrowseRoot[]? BrowseRoots = null);
public sealed record TransferSnapshot(TransferSettings Settings, TransferDevice[] Devices, TransferItem[] Items);
public sealed class TransferError(int status, string message, Exception? inner = null) : IOException(message, inner) { public int Status { get; } = status; }

/// <summary>Private spool, explicit per-device grant, durable acknowledged offsets. No remote paths.</summary>
public sealed partial class FileTransferVault
{
    public const int ChunkBytes = 256 * 1024;
    public const int StreamingBytesPerSecond = 4 * 1024 * 1024;
    public const int IdleBytesPerSecond = 16 * 1024 * 1024;
    public const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long QuotaBytes = 4L * 1024 * 1024 * 1024, ReserveBytes = 64L * 1024 * 1024;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Func<bool> _videoActive;
    private TransferSettings _settings = new();
    private readonly Dictionary<string, TransferDevice> _devices = new();
    private readonly Dictionary<string, TransferItem> _items = new();
    private readonly bool _throttle;

    public static bool IsSizeAllowed(long size) => size >= 0 && size <= MaximumFileBytes;

    public FileTransferVault(string root, Func<bool>? videoActive = null, bool throttle = true)
    {
        _root = Path.GetFullPath(root); _videoActive = videoActive ?? (() => false); _throttle = throttle;
        if (!Directory.Exists(_root)) return; // Disabled products need no new directory.
        if (File.Exists(Path.Combine(_root, "settings.json"))) _settings = Read<TransferSettings>("settings.json");
        if (File.Exists(Path.Combine(_root, "devices.json")))
            foreach (var device in Read<TransferDevice[]>("devices.json")) _devices[device.ClientId] = device;
        foreach (var path in Directory.EnumerateFiles(_root, "task-*.json"))
        {
            var item = JsonSerializer.Deserialize<TransferItem>(File.ReadAllText(path), RuntimeProtocol.JsonOptions)
                ?? throw new IOException("文件任务记录损坏，已停止文件功能以保护接收目录。");
            if (!ValidId(item.Id) || !ValidName(item.Name) || !IsSizeAllowed(item.Size) || item.Offset < 0 || item.Offset > item.Size)
                throw new IOException("文件任务记录无效。");
            if (item.State == "staging") item = item with { State = "failed" };
            if (item.State == "publishing")
                item = item.PublishedPath is not null && File.Exists(item.PublishedPath) && Hash(item.PublishedPath) == item.Sha256
                    ? item with { State = "completed", Offset = item.Size } : item with { State = "receiving", PublishedPath = null };
            if (item.State == "copying") item = item with { State = "receiving" };
            if (item.DestinationTemp is not null && Path.GetFileName(item.DestinationTemp) == ".streamhub-" + item.Id + ".part")
            { File.Delete(item.DestinationTemp); item = item with { DestinationTemp = null }; }
            if (item.Direction == "upload" && item.State == "receiving")
            {
                using var part = new FileStream(DataPath(item.Id), FileMode.OpenOrCreate, FileAccess.Write);
                if (part.Length < item.Offset) item = item with { State = "failed" };
                else part.SetLength(item.Offset); // Unacknowledged tail is never trusted after restart.
            }
            _items[item.Id] = item; SaveItem(item);
        }
    }
    private T Read<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(_root, name)), RuntimeProtocol.JsonOptions)
        ?? throw new IOException("文件功能设置损坏。");
    private void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, name); var temp = target + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value, RuntimeProtocol.JsonOptions); file.Flush(true); }
        File.Move(temp, target, true);
    }
    private void SaveItem(TransferItem item) => Save("task-" + item.Id + ".json", item);
    private string DataPath(string id) => Path.Combine(_root, id + ".part");
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static bool ValidId(string id) => id is { Length: >= 8 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool ValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]));
    }
    public TransferDevice Register(string clientId, string name, string key)
    {
        if (!ValidId(clientId) || string.IsNullOrEmpty(name) || name.Length > 80 || name.Any(char.IsControl)) throw new TransferError(400, "设备信息无效。");
        var hash = KeyHash(key);
        lock (_gate)
        {
            if (_devices.TryGetValue(clientId, out var old))
            {
                if (!Equal(old.KeyHash, hash)) throw new TransferError(403, "文件设备身份不一致，请在电脑端撤销旧授权后重新申请。");
                return old;
            }
            if (_devices.Count >= 16) throw new TransferError(409, "文件设备列表已满，请在电脑端撤销不再使用的设备。");
            var result = new TransferDevice(clientId, name, hash); _devices[clientId] = result;
            Save("devices.json", _devices.Values.ToArray()); return result;
        }
    }
    private static string KeyHash(string key)
    {
        if (key is not { Length: 64 } || !key.All(Uri.IsHexDigit)) throw new TransferError(403, "文件设备密钥无效。");
        return Convert.ToHexString(SHA256.HashData(Convert.FromHexString(key)));
    }
    private static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    public void Authorize(string clientId, string key) { lock (_gate) { AuthorizeLocked(clientId, KeyHash(key)); } }
    private void AuthorizeLocked(string clientId, string hash)
    {
        if (!_settings.Enabled || !_devices.TryGetValue(clientId, out var device) || !device.Allowed || !Equal(device.KeyHash, hash))
            throw new TransferError(403, "请先在电脑端开启文件传输并允许此设备。");
    }
    public void Configure(bool enabled, string directory, bool? browseEnabled = null)
    {
        if (enabled && (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))) throw new TransferError(400, "请选择存在的电脑接收文件夹。");
        lock (_gate)
        {
            var updated = new TransferSettings(enabled, enabled ? Path.GetFullPath(directory) : _settings.ReceiveDirectory,
                browseEnabled ?? _settings.BrowseEnabled, _settings.BrowseRoots);
            Save("settings.json", updated); _settings = updated;
        }
    }
    public void Grant(string clientId, bool allowed)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var device)) throw new TransferError(404, "请先在平板申请文件授权。");
            var updated = device with { Allowed = allowed }; Save("devices.json", _devices.Values.Select(x => x.ClientId == clientId ? updated : x).ToArray()); _devices[clientId] = updated;
        }
    }
    public void RemoveDevice(string clientId)
    { lock (_gate) { Save("devices.json", _devices.Values.Where(x => x.ClientId != clientId).ToArray()); _devices.Remove(clientId); } }
    public TransferSnapshot Snapshot() { lock (_gate) return new(_settings, _devices.Values.ToArray(), _items.Values.ToArray()); }
    public TransferItem[] List(string clientId, string key)
    { lock (_gate) { AuthorizeLocked(clientId, KeyHash(key)); return _items.Values.Where(x => x.ClientId == clientId).ToArray(); } }
    private TransferItem Find(string id, string clientId, string key)
    {
        AuthorizeLocked(clientId, KeyHash(key));
        if (!_items.TryGetValue(id, out var item) || item.ClientId != clientId) throw new TransferError(404, "文件任务不存在。");
        if (item.SharedSource && (!_settings.BrowseEnabled || !RootsLocked().Any(r=>r.Id==(item.BrowseRootId??"legacy")))) throw new TransferError(403, "电脑已撤销此文件的浏览范围。");
        return item;
    }
    private void CheckSpace(long size)
    {
        if (!IsSizeAllowed(size) || _items.Count >= 64 ||
            _items.Values.Where(x => x.State is not ("completed" or "cancelled" or "failed")).Sum(x => x.Size) + size > QuotaBytes)
            throw new TransferError(413, "单个文件最多2GiB，未完成队列最多4GiB或64项；请先清理任务。");
        Directory.CreateDirectory(_root);
        if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < size + ReserveBytes) throw new TransferError(507, "电脑暂存磁盘空间不足。");
    }
    public TransferItem CreateUpload(string clientId, string key, string name, long size, string sha256)
    {
        if (!ValidName(name) || sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit)) throw new TransferError(400, "文件名或校验值无效。");
        lock (_gate)
        {
            AuthorizeLocked(clientId, KeyHash(key));
            if (!_devices[clientId].UploadAllowed) throw new TransferError(403,"此设备仅允许浏览和下载，上传权限尚未开启。");
            CheckSpace(size);
            if (new DriveInfo(Path.GetPathRoot(_settings.ReceiveDirectory)!).AvailableFreeSpace < size + ReserveBytes) throw new TransferError(507, "电脑接收磁盘空间不足。");
            var item = new TransferItem(Guid.NewGuid().ToString("N"), clientId, "upload", name, size, sha256.ToUpperInvariant());
            using (File.Create(DataPath(item.Id))) { }
            SaveItem(item); _items[item.Id] = item; return item;
        }
    }
    public TransferItem Get(string id, string clientId, string key) { lock (_gate) return Find(id, clientId, key); }
    private async Task PaceAsync(long bytes, Stopwatch started, CancellationToken token)
    {
        if (!_throttle) return;
        var rate = _videoActive() ? StreamingBytesPerSecond : IdleBytesPerSecond;
        var remaining = TimeSpan.FromSeconds((double)bytes / rate) - started.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
    }
    public async Task<TransferItem> AppendAsync(string id, string clientId, string key, long offset, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length is < 1 or > ChunkBytes) throw new TransferError(413, "文件分块大小无效。");
        await _io.WaitAsync(token).ConfigureAwait(false); var started = Stopwatch.StartNew();
        try
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested(); var item = Find(id, clientId, key);
                if (!_devices[clientId].UploadAllowed) throw new TransferError(403, "电脑已撤销上传权限。");
                if (item.Direction != "upload" || item.State != "receiving" || offset != item.Offset || offset < 0 || bytes.LongLength > item.Size - offset)
                    throw new TransferError(409, "分块位置已改变，请刷新进度后继续。");
                using var file = new FileStream(DataPath(id), FileMode.Open, FileAccess.Write, FileShare.None);
                file.Position = offset;
                try
                {
                    file.Write(bytes); file.Flush(true); var updated = item with { Offset = offset + bytes.Length };
                    SaveItem(updated); _items[id] = updated;
                }
                catch { file.SetLength(offset); throw; }
            }
            await PaceAsync(bytes.Length, started, token).ConfigureAwait(false);
            lock (_gate) return Find(id, clientId, key);
        }
        finally { _io.Release(); }
    }
    public async Task<byte[]> ReadChunkAsync(string id, string clientId, string key, long offset, CancellationToken token)
    {
        await _io.WaitAsync(token).ConfigureAwait(false); var started = Stopwatch.StartNew();
        try
        {
            byte[] data;
            lock (_gate)
            {
                var item = Find(id, clientId, key);
                if (item.Direction != "download" || item.State != "ready" || offset < 0 || offset > item.Size) throw new TransferError(409, "文件尚未就绪或已结束。");
                data = new byte[(int)Math.Min(ChunkBytes, item.Size - offset)];
                using var file = File.OpenRead(DataPath(id)); file.Position = offset; file.ReadExactly(data);
            }
            await PaceAsync(data.Length, started, token).ConfigureAwait(false);
            lock (_gate) {
                var item = Find(id, clientId, key); if (item.State != "ready") throw new TransferError(409, "文件已取消。");
                // Progress is bytes offered, not proof of client persistence.
                item = item with { Offset = Math.Max(item.Offset, offset + data.Length) }; SaveItem(item); _items[id] = item;
            }
            return data;
        }
        finally { _io.Release(); }
    }
    public async Task<TransferItem> CompleteAsync(string id, string clientId, string key, CancellationToken token)
    {
        await _io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            TransferItem item; lock (_gate) item = Find(id, clientId, key);
            if (item.State == "completed") return item;
            if (item.Direction == "download")
            { lock (_gate) { item = Find(id, clientId, key); if (item.State != "ready") throw new TransferError(409, "文件未就绪或已取消。");
                item = item with { State = "completed", Offset = item.Size }; SaveItem(item); _items[id] = item; File.Delete(DataPath(id)); return item; } }
            lock (_gate) if (!_devices[clientId].UploadAllowed) throw new TransferError(403, "电脑已撤销上传权限。");
            if (item.State != "receiving" || item.Offset != item.Size) throw new TransferError(409, "文件尚未完整接收。");
            await using var input = new FileStream(DataPath(id), FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            string temporary;
            lock (_gate)
            {
                item = Find(id, clientId, key); token.ThrowIfCancellationRequested();
                if (!_devices[clientId].UploadAllowed) throw new TransferError(403, "电脑已撤销上传权限。");
                if (item.State != "receiving") throw new TransferError(409, "文件已取消或状态已改变。");
                if (hash != item.Sha256) throw new TransferError(422, "文件校验失败，未发布到接收文件夹。");
                temporary = Path.Combine(_settings.ReceiveDirectory, ".streamhub-" + id + ".part");
                item = item with { State = "copying", DestinationTemp = temporary }; SaveItem(item); _items[id] = item;
            }
            bool created = false;
            try
            {
                input.Position = 0;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, true))
                {
                    created = true; var buffer = new byte[ChunkBytes]; int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        var started = Stopwatch.StartNew(); lock (_gate) { if (Find(id, clientId, key).State != "copying") throw new TransferError(409, "保存已取消。");
                            if (!_devices[clientId].UploadAllowed) throw new TransferError(403,"电脑已撤销上传权限。"); }
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); await PaceAsync(read, started, token).ConfigureAwait(false);
                    }
                    output.Flush(true);
                }
                lock (_gate)
                {
                item = Find(id, clientId, key); token.ThrowIfCancellationRequested();
                if (!_devices[clientId].UploadAllowed) throw new TransferError(403,"电脑已撤销上传权限。");
                if (item.State != "copying") throw new TransferError(409, "保存已取消。");
                var publishDirectory = Path.GetDirectoryName(temporary)!;
                // Publish within the same selected directory. A cross-volume
                // spool move must never expose an incomplete final user file.
                var destination = Path.Combine(publishDirectory, item.Name);
                if (File.Exists(destination)) destination = Path.Combine(publishDirectory,
                    Path.GetFileNameWithoutExtension(item.Name) + " (" + id[..8] + ")" + Path.GetExtension(item.Name));
                item = item with { State = "publishing", PublishedPath = destination }; SaveItem(item); _items[id] = item;
                File.Move(temporary, destination, false);
                item = item with { State = "completed", DestinationTemp = null }; SaveItem(item); _items[id] = item;
                }
                await input.DisposeAsync().ConfigureAwait(false); File.Delete(DataPath(id)); return item;
            }
            catch
            {
                if (created) File.Delete(temporary);
                lock (_gate) { var current = _items[id];
                    if (current.State is "copying" or "publishing") { current = current with { State = "receiving", DestinationTemp = null, PublishedPath = null }; SaveItem(current); _items[id] = current; } }
                throw;
            }
        }
        finally { _io.Release(); }
    }
    public TransferItem QueueOutgoing(string clientId, string name, long size)
    {
        if (!ValidName(name)) throw new TransferError(400, "请选择有效的电脑文件。");
        lock (_gate)
        {
            if (!_settings.Enabled || !_devices.TryGetValue(clientId, out var device) || !device.Allowed) throw new TransferError(403, "此设备未获文件授权。");
            CheckSpace(size);
            var item = new TransferItem(Guid.NewGuid().ToString("N"), clientId, "download", name, size, "", State: "staging");
            SaveItem(item); _items[item.Id] = item; return item;
        }
    }
    public async Task StageOutgoingAsync(string id, Stream source, CancellationToken token)
    {
        await _io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            TransferItem item; lock (_gate) item = _items[id];
            await using (var output = new FileStream(DataPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, true))
            {
                var buffer = new byte[ChunkBytes]; long size = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    lock (_gate)
                    {
                        if (_items[id].State != "staging" || !_settings.Enabled || (item.SharedSource && !_settings.BrowseEnabled) ||
                            !_devices.TryGetValue(item.ClientId, out var device) || !device.Allowed)
                            throw new OperationCanceledException();
                    }
                    var started = Stopwatch.StartNew(); size += read;
                    if (size > item.Size) throw new IOException("源文件大小改变，请重新选择。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); hash.AppendData(buffer, 0, read);
                    await PaceAsync(read, started, token).ConfigureAwait(false);
                }
                if (size != item.Size) throw new IOException("源文件大小改变，请重新选择。");
                output.Flush(true);
                lock (_gate)
                {
                    if (_items[id].State != "staging") throw new OperationCanceledException();
                    item = item with { State = "ready", Sha256 = Convert.ToHexString(hash.GetHashAndReset()) }; SaveItem(item); _items[id] = item;
                }
            }
        }
        catch { lock (_gate) { var item = _items[id] with { State = _items[id].State == "cancelled" ? "cancelled" : "failed" }; SaveItem(item); _items[id] = item; File.Delete(DataPath(id)); } throw; }
        finally { _io.Release(); }
    }
    public async Task CancelAsync(string id, CancellationToken token)
    {
        lock (_gate) { if (!_items.TryGetValue(id, out var item) || item.State == "completed") return;
            item = item with { State = "cancelled" }; SaveItem(item); _items[id] = item; }
        await _io.WaitAsync(token).ConfigureAwait(false);
        try { File.Delete(DataPath(id)); }
        finally { _io.Release(); }
    }
    public async Task ClearFinishedAsync(CancellationToken token)
    {
        await _io.WaitAsync(token).ConfigureAwait(false);
        try { lock (_gate) foreach (var item in _items.Values.Where(x => x.State is "completed" or "cancelled" or "failed").ToArray())
            { File.Delete(DataPath(item.Id)); File.Delete(Path.Combine(_root, "task-" + item.Id + ".json")); _items.Remove(item.Id); } }
        finally { _io.Release(); }
    }
}
