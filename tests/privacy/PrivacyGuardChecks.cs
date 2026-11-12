using System.Diagnostics;
using StreamHub.Host.Runtime;
using StreamHub.Service;

internal static class PrivacyGuardChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        static Process Fixture()
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/q");
            return Process.Start(start)!;
        }
        static void Stop(Process p) { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } }
        var now = DateTimeOffset.UtcNow;
        foreach (var kind in new[] { "legacy", "fast", "bounded" })
        {
            var state = new LiveSessionState(); state.Prepare("lease", "device", now);
            var generation = state.Heartbeat("lease", "device", true, now, kind != "legacy", kind == "bounded");
            var expected = kind == "bounded" ? 6000 : kind == "fast" ? 12000 : 25000;
            Check(state.PrivacyTimeoutMs == expected && !state.PrivacyHeartbeatExpired(now.AddMilliseconds(expected - 1)) &&
                state.PrivacyHeartbeatExpired(now.AddMilliseconds(expected)), "Negotiated privacy budget or boundary is incorrect.");
            if (kind == "bounded")
            {
                try { state.Heartbeat("lease", "device", true, now.AddMilliseconds(expected), true, true, true); throw new Exception("Expired guard revived."); }
                catch (InvalidOperationException) { }
                state.Heartbeat("lease", "device", true, now.AddSeconds(7), true, true); // No overlay: retain normal 25s lease.
                Check(state.IsStreaming(now.AddSeconds(7)), "Normal media lease was shortened when privacy was off.");
            }
            state.End("lease", "device");
            Check(state.Generation != generation && !state.HasLease(now), "A retired generation remained valid.");
        }
        foreach (var scenario in new[] { "deadline", "late-pulse", "owner-exit", "session-change", "engine-exit" })
        {
            using var engine = Fixture(); using var owner = Fixture();
            try
            {
                var path = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
                var created = engine.StartTime.ToFileTimeUtc();
                var session = (uint)engine.SessionId;
                try
                {
                    using var invalid = PrivacyScreenGuard.Open(engine.Id, created + 1, owner.Id, owner.StartTime.ToFileTimeUtc(),
                        6000, Environment.TickCount64 + 250, (uint)engine.SessionId, path);
                    throw new Exception("Reused PID was accepted.");
                }
                catch (InvalidOperationException) { }
                using var guard = PrivacyScreenGuard.Open(engine.Id, created, owner.Id, owner.StartTime.ToFileTimeUtc(),
                    6000, Environment.TickCount64 + 250, (uint)engine.SessionId, path);
                Check(!guard.NeedsRecovery(false, (uint)engine.SessionId), "A healthy guard recovered prematurely.");
                if (scenario is "deadline" or "late-pulse") await Task.Delay(300);
                if (scenario == "owner-exit") Stop(owner);
                if (scenario == "engine-exit") Stop(engine);
                Check(guard.NeedsRecovery(scenario == "late-pulse", scenario == "session-change" ? session + 1 : session),
                    "Fault did not initiate independent recovery: " + scenario);
                var watch = Stopwatch.StartNew();
                Check(guard.EndRemoteBeforeReveal() && engine.WaitForExit(1000), "Overlay could reveal before engine exit.");
                Check(watch.ElapsedMilliseconds <= 1500, "Engine termination exceeded the recovery budget.");
                Console.WriteLine($"Independent guard {scenario}: engine exited before reveal ({watch.ElapsedMilliseconds}ms).");
            }
            finally { Stop(engine); Stop(owner); }
        }
        Console.WriteLine("Privacy deadline boundaries, legacy compatibility, generation isolation and five guard faults passed.");
    }
}
