using System.IO;
using System.Text.Json;

namespace StreamHub.Updates;

public sealed record UpdateInstallAttempt(int PreviousCode, int TargetCode, string Channel, DateTimeOffset StartedAt,
    string? AttemptId = null, string Phase = "requested", int? ProcessId = null, int? ExitCode = null)
{
    public static void Save(string path, UpdateInstallAttempt attempt)
    {
        if (attempt.PreviousCode < 1 || attempt.TargetCode <= attempt.PreviousCode || attempt.Channel is not ("internal" or "stable") ||
            attempt.AttemptId is not null && !InstallTrace.ValidId(attempt.AttemptId))
            throw new InvalidOperationException("更新安装记录无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(attempt));
        File.Move(temporary, path, true);
    }

    public static string Describe(string path, int currentCode, string channel, DateTimeOffset now, string? traceDirectory = null)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return "";
            var attempt = JsonSerializer.Deserialize<UpdateInstallAttempt>(File.ReadAllText(path));
            if (attempt is null || attempt.PreviousCode < 1 || attempt.TargetCode <= attempt.PreviousCode || attempt.Channel != channel ||
                attempt.StartedAt > now.AddMinutes(5) || attempt.StartedAt < now.AddDays(-30)) return "";
            if (currentCode == attempt.TargetCode) return "上次更新已完成，绑定和设置保留。";
            if (currentCode == attempt.PreviousCode)
            {
                if (attempt.Phase == "cancelled") return "上次已取消 Windows 安装授权，可稍后重试。";
                if (attempt.ExitCode == 16) return "上次安装遇到另一项安装或恢复，请等待它完成后重试。";
                if (InstallTrace.ValidId(attempt.AttemptId) && traceDirectory is not null)
                {
                    var trace = InstallTrace.Read(Path.Combine(traceDirectory, InstallTrace.FileName(attempt.AttemptId!)), attempt.AttemptId!, attempt.TargetCode, now);
                    if (trace is not null) return "上次安装尚未确认完成。" + trace.Describe();
                }
                if (attempt.Phase == "elevation-pending") return "上次 Windows 启动授权未返回结果，安装尚未确认完成。";
                return "上次安装尚未确认完成，当前版本仍可使用；可重新检查更新并重试。";
            }
            return "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return ""; }
    }
}
