using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed partial class HostRuntime
{
    private void PublishNetworkStatus() => Volatile.Write(ref _networkSnapshot,
        new(_network?.Running == true, _networkDesired, _network?.Running == true ? _network.Process.Id : null, _networkMessage));

    private async Task SuperviseNetworkAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _networkGate.WaitAsync(cancellationToken);
            try
            {
                if (_network?.Running == true && NetworkHandover.IsLegacyRunning())
                {
                    await StopNetworkProcessAsync(cancellationToken);
                    _networkDesired = false;
                    await SaveDesiredAsync(cancellationToken);
                    _networkMessage = "检测到原组网服务正在运行，已暂停内置组网以避免冲突。";
                }
                if (_networkDesired && _network?.Running != true && DateTimeOffset.UtcNow >= _networkRetryAt)
                {
                    try { await StartNetworkProcessAsync(cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _networkMessage = ex.Message;
                        _networkRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                        Log("组网启动失败：" + ex.Message);
                    }
                }
            }
            finally { PublishNetworkStatus(); _networkGate.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task<RuntimeResponse> HandleNetworkAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        await _networkGate.WaitAsync(cancellationToken);
        try
        {
            var command = request.Command?.ToLowerInvariant();
            string message;
            string? payload = null;
            switch (command)
            {
                case "network-config":
                    return new(true, "连接通道设置已读取。", PayloadJson: JsonSerializer.Serialize(_transportSettings.Load(), RuntimeProtocol.JsonOptions));
                case "network-status":
                    var observed = await _networkClient.GetStatusAsync(cancellationToken, _transportSettings.Load());
                    if (observed.Available) _networkMessage = observed.StateText;
                    payload = JsonSerializer.Serialize(observed, RuntimeProtocol.JsonOptions);
                    message = _networkMessage;
                    break;
                case "network-diagnose":
                    payload = JsonSerializer.Serialize(await _networkClient.DiagnoseAsync(cancellationToken, _transportSettings.Load()), RuntimeProtocol.JsonOptions);
                    message = "异地连接诊断已完成。";
                    break;
                case "network-takeover":
                    if (!_transportSettings.Load().Transport.UsesAccount) throw new InvalidOperationException("当前通道无需启用异地连接。");
                    await _handover.TakeOverAsync(StartNetworkProcessAsync, StopNetworkProcessAsync, cancellationToken);
                    _networkDesired = true;
                    await SaveDesiredAsync(cancellationToken);
                    message = "内置异地连接已接管，请登录账号。";
                    break;
                case "network-start":
                    await StartNetworkProcessAsync(cancellationToken);
                    _networkDesired = true;
                    await SaveDesiredAsync(cancellationToken);
                    message = "内置异地连接已启用。";
                    break;
                case "network-stop":
                    _networkDesired = false;
                    await SaveDesiredAsync(cancellationToken);
                    await StopNetworkProcessAsync(cancellationToken);
                    message = "异地连接已暂停，登录状态已保留。";
                    break;
                case "network-login":
                    payload = JsonSerializer.Serialize(await _networkClient.BeginLoginAsync(cancellationToken, _transportSettings.Load()), RuntimeProtocol.JsonOptions);
                    message = "请在浏览器完成账号授权。";
                    break;
                case "network-logout":
                    await _networkClient.LogoutAsync(cancellationToken);
                    message = "内置账号已退出。";
                    break;
                case "network-restore":
                case "network-rollback":
                    _networkDesired = false;
                    await SaveDesiredAsync(cancellationToken);
                    await _handover.RollbackAsync(StopNetworkProcessAsync, cancellationToken);
                    message = "已恢复接管前的组网服务。";
                    break;
                default: throw new ArgumentException("无法识别此组网操作。");
            }
            return new(true, message, PayloadJson: payload);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _networkMessage = error.Message;
            return new(false, error.Message);
        }
        finally { PublishNetworkStatus(); _networkGate.Release(); }
    }

    private async Task StartNetworkProcessAsync(CancellationToken cancellationToken)
    {
        if (NetworkHandover.IsLegacyRunning()) throw new InvalidOperationException("原组网服务正在运行。请使用“接管异地连接”，以便保存并恢复原状态。");
        var settings = _transportSettings.Load();
        if (!settings.Transport.UsesAccount) throw new InvalidOperationException("当前通道无需启用异地连接。");
        if (_network?.Running == true)
        {
            await _networkClient.ResumeAsync(settings, _ownerSid?.Value, cancellationToken);
            return;
        }
        _network?.Dispose();
        _network = null;
        var start = _networkConfiguration.CreateStartInfo();
        var process = Process.Start(start) ?? throw new InvalidOperationException("未能启动内置组网组件。");
        _network = SessionLauncher.OwnLocalProcess(process);
        // Keep draining for the process lifetime, not the short-lived IPC request.
        // Do not retain output: it can grow indefinitely and contain authorization URLs.
        _networkDrains = [DiscardOutputAsync(process.StandardOutput), DiscardOutputAsync(process.StandardError)];
        var callerToken = cancellationToken;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(20));
        cancellationToken = startup.Token;
        try
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                if (!_network.Running) throw new InvalidOperationException("内置组网组件提前退出，请修复安装或恢复原组网。");
                NetworkStatus status;
                try
                {
                    await _networkClient.ResumeAsync(settings, _ownerSid?.Value, cancellationToken);
                    status = await _networkClient.GetStatusAsync(cancellationToken, settings);
                }
                catch (Exception error) when (error is HttpRequestException or IOException) { await Task.Delay(500, cancellationToken); continue; }
                if (status.Available)
                {
                    _networkMessage = status.LoggedIn ? status.StateText : "组网已启动，等待账号登录；已保存账号不会被清除";
                    Log("内置组网组件已启动。");
                    return;
                }
                await Task.Delay(500, cancellationToken);
            }
            throw new InvalidOperationException("内置组网启动后没有响应，请重试或恢复原组网。");
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            await StopNetworkProcessAsync(CancellationToken.None);
            throw new TimeoutException("内置组网启动超时，账号记录已保留，可稍后重试。");
        }
        catch
        {
            await StopNetworkProcessAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task StopNetworkProcessAsync(CancellationToken cancellationToken)
    {
        var owned = _network;
        _network = null;
        var drains = _networkDrains;
        _networkDrains = [];
        if (owned is null) return;
        try
        {
            if (owned.Running)
            {
                using var pause = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _networkClient.SetRunningAsync(false, pause.Token); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { }
                if (owned.Running) owned.Process.Kill(entireProcessTree: true);
                using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await owned.Process.WaitForExitAsync(exited.Token); } catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Log("组网组件停止提示：" + ex.Message); }
        finally
        {
            owned.Dispose();
            try { await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            _networkMessage = "异地连接已停止，授权状态已保留";
        }
    }

    private static async Task DiscardOutputAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory()) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

}
