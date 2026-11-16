using StreamHub.Updates;

namespace StreamHub.Installer;

internal sealed partial class Deployment
{
    internal void Stage(string phase, int? exitCode = null)
    {
        EnsureProtectedData();
        var trace = new InstallTrace(_options.AttemptId, ProductVersion.Code, Environment.ProcessId, phase,
            _options.StartedAt, DateTimeOffset.UtcNow, _options.Elapsed.ElapsedMilliseconds, exitCode);
        InstallTrace.Save(Path.Combine(_dataRoot, "logs", InstallTrace.FileName(_options.AttemptId)), trace);
        Log($"[install {_options.AttemptId}] phase={phase} elapsed_ms={trace.ElapsedMilliseconds}" +
            (exitCode.HasValue ? $" exit_code={exitCode}" : ""));
    }
}
