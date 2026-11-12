using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class RuntimeRecoveryChecks
{
    public static async Task CheckExpiryAsync(string fixtureRoot, string serviceAssembly)
    {
        var data = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        var assembly = Assembly.LoadFrom(serviceAssembly);
        var runtimeType = assembly.GetType("StreamHub.Service.HostRuntime", true)!;
        var engineType = assembly.GetType("StreamHub.Service.Engine", true)!;
        var ownedType = assembly.GetType("StreamHub.Service.OwnedProcess", true)!;
        var sessionType = assembly.GetType("StreamHub.Host.Runtime.LiveSessionState", true)!;
        var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
        void Set(string name, object value) => runtimeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);
        var engine = Activator.CreateInstance(engineType, ["remote", true])!;
        var state = Activator.CreateInstance(sessionType)!;
        var now = DateTimeOffset.UtcNow;
        sessionType.GetMethod("Prepare")!.Invoke(state, ["lease", "client", now.AddSeconds(-15)]);
        sessionType.GetMethod("Heartbeat")!.Invoke(state, ["lease", "client", true, now.AddSeconds(-7), true, true, false]);
        Set("_data", data);
        Set("_remote", engine);
        Set("_remoteSession", state);

        static Process StartFixture()
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/q");
            return Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离测试进程。");
        }
        using var backend = StartFixture();
        using var overlay = StartFixture();
        var backendOwned = Activator.CreateInstance(ownedType, [Process.GetProcessById(backend.Id), (uint)0, IntPtr.Zero])!;
        var overlayOwned = Activator.CreateInstance(ownedType, [Process.GetProcessById(overlay.Id), (uint)0, IntPtr.Zero])!;
        engineType.GetProperty("Process")!.SetValue(engine, backendOwned);
        Set("_privacyScreen", overlayOwned);
        try
        {
            var stop = runtimeType.GetMethod("StopExpiredPrivacyStreamAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stopped = await (Task<bool>)stop.Invoke(runtime, [CancellationToken.None])!;
            if (!stopped || !backend.WaitForExit(3000) || overlay.HasExited)
                throw new InvalidOperationException("失联时必须先结束引擎，且此步骤不得关闭隐私遮罩。");
            if (engineType.GetProperty("Process")!.GetValue(engine) is not null ||
                (bool)sessionType.GetMethod("HasLease")!.Invoke(state, [DateTimeOffset.UtcNow])!)
                throw new InvalidOperationException("引擎结束后必须释放旧会话。");
            Console.WriteLine("Production privacy expiry stopped backend before releasing overlay.");
        }
        finally
        {
            if (!backend.HasExited) { backend.Kill(true); await backend.WaitForExitAsync(); ((IDisposable)backendOwned).Dispose(); }
            if (!overlay.HasExited) { overlay.Kill(true); await overlay.WaitForExitAsync(); }
            ((IDisposable)overlayOwned).Dispose();
        }
    }

    public static async Task RunAsync(string fixtureRoot, string serviceAssembly)
    {
        fixtureRoot = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        var assembly = Assembly.LoadFrom(serviceAssembly);
        var runtimeType = assembly.GetType("StreamHub.Service.HostRuntime", true)!;
        var engineType = assembly.GetType("StreamHub.Service.Engine", true)!;
        var ownedType = assembly.GetType("StreamHub.Service.OwnedProcess", true)!;
        var recover = runtimeType.GetMethod("HandlePrivacyScreenExitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var engineConstructor = engineType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();

        foreach (var code in new[] { 8, 9, 12, 3, -1 })
        {
            var data = Path.Combine(fixtureRoot, "exit-" + code);
            Directory.CreateDirectory(data);
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            using var desiredGate = new SemaphoreSlim(1, 1);
            void Set(string name, object value) => runtimeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);
            object? Get(string name) => runtimeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime);
            Set("_data", data);
            Set("_desiredGate", desiredGate);
            Set("_remote", engineConstructor.Invoke(["remote", true]));
            Set("_tablet", engineConstructor.Invoke(["tablet", false]));
            Set("_remoteSession", Activator.CreateInstance(assembly.GetType("StreamHub.Host.Runtime.LiveSessionState", true)!)!);
            Set("_privacyScreenEnabled", true);
            Set("_privacyScreenFailures", 2);
            Set("_privacyScreenRetryAt", DateTimeOffset.UtcNow.AddMinutes(1));
            Set("_privacyScreenMessage", "original");

            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/q");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法创建隔离的退出码测试进程。");
            var observed = Process.GetProcessById(process.Id);
            // Give OwnedProcess its own native handle before allowing the
            // child to exit, just like the service's CreateProcessAsUser path.
            var nativeHandle = code == -1 ? IntPtr.Zero : OpenProcess(0x1000, false, (uint)process.Id);
            if (code != -1 && nativeHandle == IntPtr.Zero)
                throw new InvalidOperationException("无法保留隔离进程句柄：" + Marshal.GetLastWin32Error());
            var owned = Activator.CreateInstance(ownedType, [observed, (uint)0, IntPtr.Zero, nativeHandle])!;
            await process.StandardInput.WriteLineAsync("exit " + (code == -1 ? 3 : code));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            process.Dispose();
            bool result;
            try { result = await (Task<bool>)recover.Invoke(runtime, [owned])!; }
            finally { ((IDisposable)owned).Dispose(); }
            var desired = Path.Combine(data, "desired.json");
            if (code is 8 or 9 or 12 or -1)
            {
                if (!result || (bool)Get("_privacyScreenEnabled")! || (int)Get("_privacyScreenFailures")! != 0 ||
                    (DateTimeOffset)Get("_privacyScreenRetryAt")! != default)
                    throw new InvalidOperationException("热键恢复或注册失败必须禁用隐私屏并清除重试状态。");
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(desired));
                if (document.RootElement.GetProperty("PrivacyScreen").GetBoolean())
                    throw new InvalidOperationException("紧急恢复必须将关闭状态持久化。");
                var message = (string)Get("_privacyScreenMessage")!;
                if ((code is 8 or 9 && !message.Contains("Ctrl+Alt+F12")) || (code == 12 && !message.Contains("安全结束远控")) ||
                    (code == 8 && !message.Contains("无法注册")) || (code == -1 && !message.Contains("无法确认原因")))
                    throw new InvalidOperationException("恢复与热键失败必须提供对应的状态说明。");
            }
            else if (result || !(bool)Get("_privacyScreenEnabled")! || File.Exists(desired))
                throw new InvalidOperationException("其他退出码不能冒充主动恢复或热键注册失败。");
        }
        Console.WriteLine("Production privacy recovery and persisted-disable checks passed.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
}
