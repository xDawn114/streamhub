using StreamHub.Host.Runtime;
using StreamHub.Service;

if (args.Length == 1 && args[0] == "--guard-faults")
{
    await PrivacyGuardChecks.RunAsync();
    return;
}

if (args.Length == 2 && args[0] == "--session-controls")
{
    await SessionControlChecks.RunAsync(Path.GetFullPath(args[1]));
    return;
}

if (args.Length == 2 && args[0] == "--job-owner-crash")
{
    // This child deliberately exits without disposing OwnedProcess. The OS
    // must close the job handle and terminate the protected helper with it.
    var child = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
    child.ArgumentList.Add("/d"); child.ArgumentList.Add("/q");
    using var protectedProcess = System.Diagnostics.Process.Start(child)
        ?? throw new InvalidOperationException("无法启动作业对象清理测试进程。");
    File.WriteAllText(Path.GetFullPath(args[1]), protectedProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    var owned = SessionLauncher.OwnLocalProcess(protectedProcess);
    System.Diagnostics.Process.GetCurrentProcess().Kill();
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check(!PrivacyScreenPolicy.ShouldShow(false, true, true), "关闭设置时不应显示隐私黑屏。");
Check(!PrivacyScreenPolicy.ShouldShow(true, false, true), "没有 ready 心跳时不应显示隐私黑屏。");
Check(!PrivacyScreenPolicy.ShouldShow(true, true, false), "主屏引擎已退出时不应显示隐私黑屏。");
Check(PrivacyScreenPolicy.ShouldShow(true, true, true), "开启设置、认证串流和主屏引擎同时满足时应显示隐私黑屏。");
Check(PrivacyScreenPolicy.RequiresSessionRestart(false, 7, 8), "服务模式切换登录会话后必须重启黑屏助手。");
Check(!PrivacyScreenPolicy.RequiresSessionRestart(false, 7, 7) && !PrivacyScreenPolicy.RequiresSessionRestart(true, 7, 8), "同一会话或交互模式不应误重启黑屏助手。");
Check(PrivacyScreenPolicy.ShouldRetry(0) && PrivacyScreenPolicy.ShouldRetry(2), "隐私黑屏失败次数未达上限时应继续重试。");
Check(!PrivacyScreenPolicy.ShouldRetry(PrivacyScreenPolicy.MaxConsecutiveFailures), "隐私黑屏达到失败上限后不应继续自动重试。");
Check(PrivacyScreenPolicy.RetryLimitMessage().Contains("停止自动重试"), "达到隐私黑屏失败上限时应给出恢复提示。");
Check(PrivacyScreenPolicy.Message(true, false, false, true).Contains("等待"), "未出画时应显示等待状态。");
Check(PrivacyScreenPolicy.Message(true, true, true, true).Contains("黑屏"), "激活时应显示黑屏状态。");
Console.WriteLine("PrivacyScreenPolicy checks passed.");

using (var first = PrivacyScreenLifetime.Create())
using (var second = PrivacyScreenLifetime.Create())
using (var firstReader = PrivacyScreenLifetime.Open(first.Name))
using (var secondReader = PrivacyScreenLifetime.Open(second.Name))
using (var firstReady = PrivacyScreenLifetime.OpenReady(first.Name))
{
    Check(first.Name != second.Name && PrivacyScreenLifetime.IsValidName(first.Name), "每个黑屏助手必须使用独立有效的退出事件。");
    Check(!firstReader.WaitOne(0) && !secondReader.WaitOne(0), "创建退出事件不能提前关闭黑屏助手。");
    Check(!first.IsReady && !second.IsReady, "就绪事件不能在助手启动前置位。");
    firstReady.Set();
    Check(first.IsReady && !second.IsReady, "只有对应助手的就绪事件能确认启动。");
    var waiter = Task.Run(() => firstReader.WaitOne(TimeSpan.FromSeconds(2)));
    first.RequestStop();
    Check(waiter.GetAwaiter().GetResult(), "退出请求必须唤醒已经等待的助手。");
    Check(firstReader.WaitOne(0), "退出请求必须保持有效，覆盖启动和退出交错。");
    Check(!secondReader.WaitOne(0), "一个助手的退出不能影响另一个助手。");
}
foreach (var invalid in new[] { "", "StreamHub.PrivacyStop.test", @"Global\StreamHub.PrivacyStop.bad", @"Global\Other.0123456789abcdef0123456789abcdef" })
{
    Check(!PrivacyScreenLifetime.IsValidName(invalid), "不能接受任意命名事件。");
    try { PrivacyScreenLifetime.Open(invalid).Dispose(); throw new InvalidOperationException("非法事件不应打开。"); }
    catch (ArgumentException) { }
    try { PrivacyScreenLifetime.OpenReady(invalid).Dispose(); throw new InvalidOperationException("非法就绪事件不应打开。"); }
    catch (ArgumentException) { }
}
Console.WriteLine("PrivacyScreenLifetime wakeup and isolation checks passed.");

if (args.Length == 1 && args[0] == "--job-object-recovery")
{
    var evidence = Path.Combine(AppContext.BaseDirectory, "streamhub-privacy-job-" + Guid.NewGuid().ToString("N") + ".pid");
    try
    {
        var self = Path.Combine(AppContext.BaseDirectory, "PrivacyScreenChecks.exe");
        if (!File.Exists(self)) self = Environment.ProcessPath!;
        var ownerStart = new System.Diagnostics.ProcessStartInfo(self)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        ownerStart.ArgumentList.Add("--job-owner-crash"); ownerStart.ArgumentList.Add(evidence);
        using var owner = System.Diagnostics.Process.Start(ownerStart)
            ?? throw new InvalidOperationException("无法启动作业对象所有者测试进程。");
        Check(owner.WaitForExit(5000), "作业对象所有者没有在测试期限内退出。");
        var error = await owner.StandardError.ReadToEndAsync();
        Check(File.Exists(evidence), "作业对象清理测试没有返回受保护进程标识：" + error.Trim());
        var pid = int.Parse(File.ReadAllText(evidence), System.Globalization.CultureInfo.InvariantCulture);
        var stopped = false;
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var protectedProcess = System.Diagnostics.Process.GetProcessById(pid);
                if (protectedProcess.HasExited) { stopped = true; break; }
            }
            catch (ArgumentException) { stopped = true; break; }
            await Task.Delay(100);
        }
        Check(stopped, "服务所有者异常退出后，受保护进程仍未被作业对象回收。");
        Console.WriteLine("Job object kill-on-close recovery passed.");
    }
    finally { try { if (File.Exists(evidence)) File.Delete(evidence); } catch { } }
    return;
}

if (args.Length == 3 && args[0] == "--runtime-recovery")
    await RuntimeRecoveryChecks.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));

if (args.Length == 3 && args[0] == "--privacy-expiry")
    await RuntimeRecoveryChecks.CheckExpiryAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));

if (args.Length == 2 && args[0] == "--hotkey-conflict")
    NativePrivacyChecks.CheckHotkeyConflict(Path.GetFullPath(args[1]));

if (args.Length == 2 && args[0] == "--native-recovery")
    NativePrivacyChecks.CheckHitTestAndRecovery(Path.GetFullPath(args[1]));

if (args.Length == 2 && args[0] == "--actual-hotkey")
    NativePrivacyChecks.CheckActualHotkey(Path.GetFullPath(args[1]));

if (args.Length == 2 && args[0] == "--helper-executable")
{
    using var lifetime = PrivacyScreenLifetime.Create();
    var start = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(args[1]))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    if (Environment.GetEnvironmentVariable("STREAMHUB_PRIVACY_TEST_ROOT") is { Length: > 0 } extractRoot)
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.GetFullPath(extractRoot);
    start.ArgumentList.Add("--privacy-screen");
    start.ArgumentList.Add(lifetime.Name);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("无法启动黑屏助手。");
    try
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!lifetime.IsReady && !process.HasExited && started.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25);
        if (process.HasExited) throw new InvalidOperationException($"黑屏助手提前退出（{process.ExitCode}）：{await process.StandardError.ReadToEndAsync()}");
        Check(lifetime.IsReady, "只有成功注册恢复热键并显示遮罩后才能报告就绪。");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        lifetime.RequestStop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await process.WaitForExitAsync(timeout.Token);
        Check(process.ExitCode == 0, "黑屏助手必须通过消息循环正常退出。");
        Check(watch.ElapsedMilliseconds < 1000, "黑屏助手正常退出不得等待一秒以上。");
        Console.WriteLine($"Native privacy helper exited normally in {watch.ElapsedMilliseconds} ms.");
    }
    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
}
