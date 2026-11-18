using System.IO;
using System.Text.Json;

namespace StreamHub.Updates;

public sealed record InstallTrace(string AttemptId, int TargetCode, int ProcessId, string Phase,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, long ElapsedMilliseconds, int? ExitCode = null)
{
    public static bool ValidId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string FileName(string id) => ValidId(id) ? "install-" + id + ".json" : throw new ArgumentException("Invalid install attempt ID.");
    public string Describe() => Phase switch
    {
        "entry" => "安装器已进入，正在取得安装锁。",
        "locked" => "正在验证安装资源。",
        "payload-ready" => "安装资源已验证。",
        "stopping" => "正在停止后台服务。",
        "backup" => "正在备份当前版本。",
        "replacing" => "正在替换程序文件。",
        "starting" => "正在启动后台服务。",
        "encoder-prewarm-wait" => "正在准备首次连接。",
        "encoder-prewarm-ready" => "首次连接准备完成。",
        "encoder-prewarm-skipped-paused" => "已保留暂停或关闭状态，未预热。",
        "encoder-prewarm-skipped-configuration" => "当前配置无需预热。",
        "encoder-prewarm-deferred-active-session" => "正在连接或收尾，首次连接准备已延后。",
        "encoder-prewarm-deferred-cache-unavailable" => "缓存暂不可用，连接时会自动检查。",
        "encoder-prewarm-deferred-unsupported-engine" => "引擎未提供预热状态，连接时会自动检查。",
        "encoder-prewarm-deferred-timeout" => "首次连接准备已延后，连接时会自动检查。",
        "encoder-prewarm-deferred-error" => "首次连接准备遇到问题，连接时会自动检查。",
        "complete" => "安装器已完成，请重新打开我控制确认版本。",
        "busy" => "另一项安装或恢复正在进行，请等待它完成。",
        "parent-timeout" => "恢复程序等待上个进程退出超时。",
        "failed" => "安装未完成，请查看安装记录后重试。",
        _ => ""
    };
    private bool Valid() => ValidId(AttemptId) && TargetCode > 0 && ProcessId > 0 && ElapsedMilliseconds >= 0 &&
        UpdatedAt >= StartedAt && Describe().Length > 0;
    public static void Save(string path, InstallTrace value)
    {
        if (!value.Valid()) throw new InvalidOperationException("Invalid installation trace.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static InstallTrace? Read(string path, string id, int targetCode, DateTimeOffset now)
    {
        try
        {
            if (!ValidId(id) || !File.Exists(path) || new FileInfo(path).Length > 8192) return null;
            var value = JsonSerializer.Deserialize<InstallTrace>(File.ReadAllText(path));
            return value is not null && value.Valid() && value.AttemptId == id && value.TargetCode == targetCode &&
                value.UpdatedAt <= now.AddMinutes(5) && value.StartedAt >= now.AddDays(-30) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
}
