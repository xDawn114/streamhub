using System.IO;
using System.Text.Json;

namespace StreamHub.Updates;

public sealed record UpdateInstallAttempt(int PreviousCode, int TargetCode, string Channel, DateTimeOffset StartedAt)
{
    public static void Save(string path, UpdateInstallAttempt attempt)
    {
        if (attempt.PreviousCode < 1 || attempt.TargetCode <= attempt.PreviousCode || attempt.Channel is not ("internal" or "stable"))
            throw new InvalidOperationException("更新安装记录无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(attempt));
        File.Move(temporary, path, true);
    }

    public static string Describe(string path, int currentCode, string channel, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return "";
            var attempt = JsonSerializer.Deserialize<UpdateInstallAttempt>(File.ReadAllText(path));
            if (attempt is null || attempt.PreviousCode < 1 || attempt.TargetCode <= attempt.PreviousCode || attempt.Channel != channel ||
                attempt.StartedAt > now.AddMinutes(5) || attempt.StartedAt < now.AddDays(-30)) return "";
            if (currentCode == attempt.TargetCode) return "上次更新已完成，绑定和设置保留。";
            if (currentCode == attempt.PreviousCode) return "上次安装尚未确认完成，当前版本仍可使用；可重新检查更新并重试。";
            return "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return ""; }
    }
}
