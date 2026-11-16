using System.ComponentModel;
using System.Text.Json;
using StreamHub.Updates;
using StreamHub.Installer;

var root = Path.GetFullPath(args.Single()); Directory.CreateDirectory(root); int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
Action? scheduled = null; int launches = 0; var guard = new Guard(); var fakeProcess = new object();
var launch = new UpdateLaunchOperation<object>(() => { launches++; return fakeProcess; }, guard, a => scheduled = a);
try { await launch.WaitForStartAsync(TimeSpan.FromMilliseconds(10)); throw new Exception("Expected timeout"); }
catch (TimeoutException) { Check(!guard.Disposed && launches == 0 && !launch.Started.IsCompleted, "slow elevation times out only the observer, preserving the package lock and original request"); }
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await launch.WaitForStartAsync(TimeSpan.FromSeconds(1), cancelled.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(!guard.Disposed && !launch.Started.IsCompleted, "cancelled wait cannot abandon a pending shell elevation request"); }
}
scheduled!();
Check(ReferenceEquals(await launch.Started, fakeProcess) && launches == 1 && guard.Disposed, "late process result is retained, launches once and releases the verification lock");
Check(ReferenceEquals(await launch.WaitForStartAsync(TimeSpan.FromSeconds(1)), fakeProcess) && launches == 1, "subsequent observer reuses the completed request");
var deniedGuard = new Guard();
var denied = new UpdateLaunchOperation<object>(() => throw new Win32Exception(1223), deniedGuard, a => a());
try { await denied.Started; throw new Exception("Expected UAC denial"); }
catch (Win32Exception e) { Check(e.NativeErrorCode == 1223 && deniedGuard.Disposed, "UAC cancellation remains distinguishable and releases the file lock"); }
var failedGuard = new Guard();
var failed = new UpdateLaunchOperation<object>(() => new object(), failedGuard, _ => throw new InvalidOperationException("scheduler failed"));
try { await failed.Started; throw new Exception("Expected scheduling failure"); }
catch (InvalidOperationException) { Check(failedGuard.Disposed, "thread-start failure releases verification ownership"); }
var phases = new List<string>();
Exception? observedError = null;
var observed = new UpdateLaunchOperation<object>(() => throw new Win32Exception(5), null, a => a(),
    (phase, error) => { phases.Add(phase); observedError = error; });
try { await observed.Started; throw new Exception("Expected launch error"); }
catch (Win32Exception) { Check(phases.SequenceEqual(new[] { "queued", "shell-entered", "shell-failed" }) &&
    observedError is Win32Exception { NativeErrorCode: 5 }, "shell trace separates scheduling, shell entry and native failure"); }
var reportingFailure = new UpdateLaunchOperation<object>(() => fakeProcess, null, a => a(), (_, _) => throw new IOException("disk full"));
Check(ReferenceEquals(await reportingFailure.Started, fakeProcess), "trace write failure cannot prevent a real launch");
var startInfo = UpdateShellStartInfo.Create(Path.Combine(root, "package with spaces.exe"),
    ["--quiet", "--skip-driver", "--require-idle"], Path.Combine(root, "install with spaces"), new IntPtr(123));
Check(startInfo.UseShellExecute && startInfo.Verb == "runas" && startInfo.ErrorDialog &&
    startInfo.ErrorDialogParentHandle == new IntPtr(123) && startInfo.WindowStyle == System.Diagnostics.ProcessWindowStyle.Normal,
    "shell request preserves elevation and attaches visible errors to its owner");
Check(startInfo.ArgumentList.SequenceEqual(new[] { "--quiet", "--skip-driver", "--require-idle", "--install-dir", Path.Combine(root, "install with spaces") }),
    "idle requirement and space-containing install path remain separate arguments");
var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid().ToString("N"); var receipt = Path.Combine(root, "attempt.json");
var attempt = new UpdateInstallAttempt(168, 169, "internal", now, id, "elevation-pending");
UpdateInstallAttempt.Save(receipt, attempt);
Check(UpdateInstallAttempt.Describe(receipt, 168, "internal", now).Contains("授权未返回"), "restart distinguishes pending elevation from created installer process");
var log = Path.Combine(root, InstallTrace.FileName(id));
InstallTrace.Save(log, new(id, 169, 1234, "replacing", now, now, 1024));
Check(InstallTrace.Read(log, id, 169, now)?.Phase == "replacing", "structured installer stage can be read after the UI exits");
Check(UpdateInstallAttempt.Describe(receipt, 168, "internal", now, root).Contains("替换程序"), "restart reports last installer stage for the matching attempt");
Check(InstallTrace.Read(log, Guid.NewGuid().ToString("N"), 169, now) is null && InstallTrace.Read(log, id, 170, now) is null, "another attempt or package version cannot supply current progress");
InstallTrace.Save(log, new(id, 169, 1234, "complete", now, now, 2048, 0));
Check(UpdateInstallAttempt.Describe(receipt, 168, "internal", now, root).StartsWith("上次安装尚未确认完成"), "exit zero and completion trace cannot substitute for installed-version verification");
Check(UpdateInstallAttempt.Describe(receipt, 169, "internal", now, root).Contains("上次更新已完成"), "actual installed target version confirms completion");
UpdateInstallAttempt.Save(receipt, attempt with { Phase = "exited", ExitCode = 16 });
Check(UpdateInstallAttempt.Describe(receipt, 168, "internal", now, root).Contains("另一项安装"), "installer mutex contention has its own outcome");
UpdateInstallAttempt.Save(receipt, attempt with { Phase = "cancelled" });
Check(UpdateInstallAttempt.Describe(receipt, 168, "internal", now, root).Contains("已取消"), "cancelled UAC receipt is retained for restart feedback");
foreach (var invalid in new[] { "../outside", new string('a', 31), new string('g', 32), new string('A', 32) })
    Check(!InstallTrace.ValidId(invalid), "invalid attempt ID rejected");
var trace = new InstallTrace(id, 169, 1234, "entry", now, now, 0);
File.WriteAllText(log, JsonSerializer.Serialize(trace with { Phase = "untrusted arbitrary message" }));
Check(InstallTrace.Read(log, id, 169, now) is null, "unrecognized status cannot inject progress text");
File.WriteAllText(log, "broken json"); Check(InstallTrace.Read(log, id, 169, now) is null, "interrupted trace does not break the update page");
var mutexName = "Local\\iControl-install-test-" + Guid.NewGuid().ToString("N");
using (var existing = new Mutex(false, mutexName))
{
    using (var acquired = InstallTransactionLock.TryAcquire(mutexName))
    {
        Check(acquired is not null, "an existing but unowned mutex does not falsely block an upgrade");
        bool blocked = false; Exception? workerError = null;
        // Task's synchronous wait may inline work on the owning pool thread,
        // which would test recursive mutex acquisition rather than contention.
        var contender = new Thread(() =>
        {
            try { using var other = InstallTransactionLock.TryAcquire(mutexName); blocked = other is null; }
            catch (Exception error) { workerError = error; }
        }) { IsBackground = true };
        contender.Start();
        if (!contender.Join(TimeSpan.FromSeconds(5)) || workerError is not null) throw new Exception("Mutex contender failed", workerError);
        Check(blocked, "a live installation excludes another thread without blocking");
    }
    Check(existing.WaitOne(0), "installer explicitly releases its transaction lock"); existing.ReleaseMutex();
    var abandoned = new Thread(() => { var owned = new Mutex(false, mutexName); owned.WaitOne(); });
    abandoned.Start(); abandoned.Join();
    using var recovered = InstallTransactionLock.TryAcquire(mutexName);
    Check(recovered is not null, "abandoned installation lock can be acquired safely");
}
Console.WriteLine($"PASS {checks} update launch checks; no installer executed");

sealed class Guard : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
