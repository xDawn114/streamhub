using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using StreamHub.Updates;

namespace StreamHub.Host;

public partial class MainWindow
{
    private UpdateClient? _updates;
    private UpdateTrust? _updateTrust;
    private UpdateManifest? _availableUpdate;
    private string? _downloadedUpdate;
    private UpdatePreferences _updatePreferences = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CancellationTokenSource? _updateOperation;
    private bool _loadingUpdatePreferences = true, _updateRunning;
    private int _updateNotifiedCode;
    private Task? _installerWork;
    private bool _installerInFlight, _installerHasProcess;
    private static string InstallTraceDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamHub", "logs");
    private string UpdatePreferencesPath => Path.Combine(Path.GetDirectoryName(UiPreferencesPath)!, "update-preferences.json");
    private string UpdateCacheRoot => Path.Combine(Path.GetDirectoryName(UiPreferencesPath)!, "updates");
    private string UpdateReceiptPath => Path.Combine(UpdateCacheRoot, "downloaded-update.json");
    private string UpdateAttemptPath => Path.Combine(UpdateCacheRoot, "install-attempt.json");
    private string CurrentUpdateVersion => $"当前 {ProductVersion.Name} / {ProductVersion.Code} · " + (ProductVersion.IsInternal ? "内测渠道" : "正式渠道");
    private sealed record DownloadReceipt(int Code, string Sha256, string Path);

    private void InitializeUpdates()
    {
        _updatePreferences = UpdatePreferencesFile.Read(UpdatePreferencesPath);
        AutoCheckUpdateCheck.IsChecked = _updatePreferences.AutoCheck;
        AutoDownloadUpdateCheck.IsChecked = _updatePreferences.AutoDownload;
        _loadingUpdatePreferences = false;
        UpdateStateText.Text = UpdateInstallAttempt.Describe(UpdateAttemptPath, ProductVersion.Code,
            ProductVersion.IsInternal ? "internal" : "stable", DateTimeOffset.UtcNow, InstallTraceDirectory);
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("StreamHub.UpdateTrust.json") ?? throw new IOException();
            _updateTrust = JsonSerializer.Deserialize<UpdateTrust>(resource, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new IOException();
            if (UpdatePolicy.QueryConfigured(_updateTrust))
            {
                var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
                Closed += (_, _) => http.Dispose();
                _updates = new UpdateClient(_updateTrust, http, AuthenticodeVerifier.VerifyAsync);
            }
        }
        catch { UpdateStateText.Text = "暂时无法检查更新，请稍后重试。"; }
        UpdateVersionText.Text = CurrentUpdateVersion;
        _updateTimer.Tick += async (_, _) => await MaybeCheckUpdateAsync();
        Loaded += async (_, _) =>
        {
            _updateTimer.Start();
            try { await Task.Delay(TimeSpan.FromSeconds(15), _closing.Token); await MaybeCheckUpdateAsync(); }
            catch (OperationCanceledException) { }
        };
        Closed += (_, _) => { _updateTimer.Stop(); _updateOperation?.Cancel(); };
        RenderUpdateActions();
    }
    private void UpdatePreferences_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUpdatePreferences) return;
        _updatePreferences = _updatePreferences with { AutoCheck = AutoCheckUpdateCheck.IsChecked == true, AutoDownload = AutoDownloadUpdateCheck.IsChecked == true };
        SaveUpdatePreferences();
    }
    private void SaveUpdatePreferences()
    {
        try { UpdatePreferencesFile.Write(UpdatePreferencesPath, _updatePreferences); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { UpdateStateText.Text = "更新偏好未能保存，请检查本机存储后重试。"; }
    }
    private async Task<bool> UpdateIdleAsync()
    {
        if (_busy) return false;
        var response = await _runtime.GetStatusAsync(_closing.Token);
        var state = response.Success ? response.Status : null;
        if (state is null || state.Remote.Streaming || state.Tablet.Streaming || state.Remote.BackendBusy || state.Tablet.BackendBusy || state.PrivacyScreenActive) return false;
        var files = await _runtime.SendAsync(new("files-status"), _closing.Token);
        if (!files.Success || files.PayloadJson is null) return false;
        var snapshot = JsonSerializer.Deserialize<StreamHub.Host.Runtime.TransferSnapshot>(files.PayloadJson, StreamHub.Host.Runtime.RuntimeProtocol.JsonOptions);
        return snapshot is not null && !snapshot.Items.Any(x => x.State is not ("completed" or "failed" or "cancelled"));
    }
    private async Task MaybeCheckUpdateAsync()
    {
        if (_updates is null || _updateRunning || _installerInFlight || _closing.IsCancellationRequested || !_updatePreferences.Due(DateTimeOffset.UtcNow)) return;
        try { if (await UpdateIdleAsync()) await CheckForUpdateAsync(false); }
        catch (OperationCanceledException) { }
        catch { /* A background failure never opens a dialog or disrupts a session. */ }
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(true);
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updates is null || _updateRunning || _installerInFlight) return;
        BeginUpdateOperation();
        var token = _updateOperation!.Token;
        _availableUpdate = null; _downloadedUpdate = null; RenderUpdateActions();
        UpdateNotesText.Visibility = Visibility.Collapsed; UpdateVersionText.Text = CurrentUpdateVersion;
        try
        {
            UpdateStateText.Text = "正在检查并验证新版本…";
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            _availableUpdate = await _updates.CheckAsync(ProductVersion.IsInternal ? "internal" : "stable", ProductVersion.Name, ProductVersion.Code, checkTimeout.Token);
            _updatePreferences = _updatePreferences.Succeeded(DateTimeOffset.UtcNow); SaveUpdatePreferences();
            UpdateNotesText.Text = _availableUpdate.Notes; UpdateNotesText.Visibility = Visibility.Visible;
            UpdateVersionText.Text = CurrentUpdateVersion + $"\n最新 {_availableUpdate.Version} / {_availableUpdate.VersionCode}";
            await RestoreUpdateDownloadAsync(token);
            if (_downloadedUpdate is null && _updatePreferences.AutoDownload && _updateTrust is not null && UpdatePolicy.Configured(_updateTrust) &&
                UpdateNetworkCost.Unmetered() && await UpdateIdleAsync()) await DownloadUpdateCoreAsync(token);
            RenderAvailableUpdateState();
            if (!manual && _updateNotifiedCode != _availableUpdate.VersionCode && System.Windows.Application.Current is App app)
            {
                _updateNotifiedCode = _availableUpdate.VersionCode;
                app.ShowSessionNotification("发现我控制新版本", $"{_availableUpdate.Version} / {_availableUpdate.VersionCode}，请在设置 → 版本与更新查看。");
            }
        }
        catch (NoNewUpdateException)
        {
            _availableUpdate = null; _downloadedUpdate = null;
            _updatePreferences = _updatePreferences.Succeeded(DateTimeOffset.UtcNow); SaveUpdatePreferences();
            UpdateVersionText.Text = CurrentUpdateVersion;
            UpdateNotesText.Visibility = Visibility.Collapsed; UpdateStateText.Text = "已经是当前渠道的最新版本。";
        }
        catch (OperationCanceledException)
        {
            _updatePreferences = _updatePreferences.Failed(DateTimeOffset.UtcNow); SaveUpdatePreferences();
            UpdateStateText.Text = token.IsCancellationRequested ? "更新操作已取消，可重新检查或下载。" : "检查更新超时，可重试；后台稍后自动再试。";
        }
        catch (Exception error)
        {
            _updatePreferences = _updatePreferences.Failed(DateTimeOffset.UtcNow); SaveUpdatePreferences();
            Trace.WriteLine("Update check failed: " + error);
            UpdateStateText.Text = "暂时无法检查更新，请稍后重试。";
        }
        finally { EndUpdateOperation(); }
    }
    private void RenderAvailableUpdateState()
    {
        if (_availableUpdate is null) return;
        UpdateStateText.Text = _downloadedUpdate is not null ? "更新已下载并验证。" :
            _updateTrust is null || !UpdatePolicy.Configured(_updateTrust) ? "发现新版本；此版本暂需手动安装。" :
            "发现新版本，可下载。";
    }
    private void RenderUpdateActions()
    {
        CheckUpdateButton.IsEnabled = !_updateRunning && !_installerInFlight && _updates is not null;
        DownloadUpdateButton.Visibility = _availableUpdate is not null && _downloadedUpdate is null ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = !_updateRunning && !_installerInFlight && _updateTrust is not null && UpdatePolicy.Configured(_updateTrust);
        InstallUpdateButton.Visibility = _downloadedUpdate is not null ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = !_updateRunning && !_installerInFlight;
        CancelUpdateButton.Visibility = _updateRunning && !_installerInFlight ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = _updateRunning || _installerInFlight ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BeginUpdateOperation()
    {
        _updateRunning = true; _updateOperation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        UpdateProgress.IsIndeterminate = true; RenderUpdateActions();
    }
    private void EndUpdateOperation()
    {
        _updateRunning = false; _updateOperation?.Dispose(); _updateOperation = null; RenderUpdateActions();
    }
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => _updateOperation?.Cancel();
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateRunning || _installerInFlight || _availableUpdate is null) return;
        BeginUpdateOperation();
        try { await DownloadUpdateCoreAsync(_updateOperation!.Token); RenderAvailableUpdateState(); }
        catch (OperationCanceledException) { UpdateStateText.Text = "已取消下载，可重新下载。"; }
        catch (Exception error) { _downloadedUpdate = null; Trace.WriteLine("Update download failed: " + error); UpdateStateText.Text = "暂时无法下载更新，请稍后重试。"; }
        finally { EndUpdateOperation(); }
    }
    private async Task DownloadUpdateCoreAsync(CancellationToken token)
    {
        if (_updates is null || _availableUpdate is null) return;
        _downloadedUpdate = await _updates.DownloadAsync(_availableUpdate, UpdateCacheRoot, new Progress<int>(p =>
        { UpdateProgress.IsIndeterminate = false; UpdateProgress.Value = p; UpdateStateText.Text = $"正在下载并验证更新 {p}%"; }), token);
        Directory.CreateDirectory(UpdateCacheRoot);
        var temporary = UpdateReceiptPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new DownloadReceipt(_availableUpdate.VersionCode, _availableUpdate.Sha256, _downloadedUpdate)));
        File.Move(temporary, UpdateReceiptPath, true);
    }
    private async Task RestoreUpdateDownloadAsync(CancellationToken token)
    {
        if (_updates is null || _availableUpdate is null || !File.Exists(UpdateReceiptPath)) return;
        try
        {
            var receipt = JsonSerializer.Deserialize<DownloadReceipt>(File.ReadAllText(UpdateReceiptPath));
            if (receipt is null || receipt.Code != _availableUpdate.VersionCode || receipt.Sha256 != _availableUpdate.Sha256 ||
                !Path.GetFullPath(receipt.Path).StartsWith(Path.GetFullPath(UpdateCacheRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            using var verified = await _updates.OpenVerifiedAsync(_availableUpdate, receipt.Path, token);
            _downloadedUpdate = receipt.Path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }
    private async Task RequireUpdateIdleAsync()
    {
        if (!await UpdateIdleAsync()) throw new InvalidOperationException("请先结束串流并完成或取消文件传输任务，再安装更新。");
        var id = Guid.NewGuid().ToString("N");
        var response = await _runtime.SendAsync(new("maintenance-begin", RequestId: id), _closing.Token);
        if (!response.Success) throw new InvalidOperationException(response.Message);
        await _runtime.SendAsync(new("maintenance-end", RequestId: id), _closing.Token);
        // The installer takes its own authoritative lease before stopping services.
    }
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateRunning || _installerInFlight || _updates is null || _availableUpdate is null || _downloadedUpdate is null) return;
        if (MessageBox.Show(this, $"安装 {_availableUpdate.Version} / {_availableUpdate.VersionCode}？\n绑定和设置保留；Windows 将要求管理员确认。", "安装更新", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        BeginUpdateOperation();
        try
        {
            await RequireUpdateIdleAsync();
            var verified = await _updates.OpenVerifiedAsync(_availableUpdate, _downloadedUpdate, _updateOperation!.Token);
            var attempt = new UpdateInstallAttempt(ProductVersion.Code, _availableUpdate.VersionCode,
                ProductVersion.IsInternal ? "internal" : "stable", DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), "elevation-pending");
            UpdateLaunchOperation<Process> launch;
            try
            {
                UpdateInstallAttempt.Save(UpdateAttemptPath, attempt);
                _updateOperation.Token.ThrowIfCancellationRequested();
                launch = LaunchUpdate(verified.Path, ["--quiet", "--skip-driver", "--require-idle", "--update-attempt", attempt.AttemptId!], verified);
            }
            catch { verified.Dispose(); throw; }
            _installerInFlight = true; _installerHasProcess = false; RenderUpdateActions();
            UpdateStateText.Text = "正在请求 Windows 安装授权…";
            _installerWork = TrackInstallerAsync(launch, attempt);
            try { await _installerWork.WaitAsync(TimeSpan.FromSeconds(45), _closing.Token); }
            catch (TimeoutException)
            {
                if (!_installerHasProcess) UpdateStateText.Text = "Windows 尚未返回启动结果，请查看授权框；当前请求仍在等待。";
                // Do not resubmit or release the verified file while ShellExecute
                // may still return a live installer. TrackInstallerAsync owns it.
            }
        }
        catch (Exception error)
        {
            if (!_closing.IsCancellationRequested) UpdateStateText.Text = "未开始安装：" + error.Message;
        }
        finally { EndUpdateOperation(); }
    }
    private async void RollbackUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateRunning || _installerInFlight) return;
        try
        {
            var pointer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamHub", "installer", "latest-rollback.json");
            if (!File.Exists(pointer)) throw new IOException("尚无可用的升级备份。首次安装不能恢复上版。");
            if (MessageBox.Show(this, "恢复上次升级前的程序？\n绑定和个人设置保留。请先结束串流和当前文件操作。", "恢复上个版本", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var launch = LaunchUpdate(Path.Combine(AppContext.BaseDirectory, "卸载串流中心.exe"), ["--rollback", "--require-idle"]);
            _installerInFlight = true; _installerHasProcess = false; RenderUpdateActions();
            _installerWork = TrackInstallerAsync(launch, null);
            try { await _installerWork.WaitAsync(TimeSpan.FromSeconds(45), _closing.Token); }
            catch (TimeoutException) { if (!_installerHasProcess) UpdateStateText.Text = "Windows 尚未返回恢复程序的启动结果，请查看授权框。"; }
        }
        catch (Exception error) { ShowFeedback("未启动恢复：" + error.Message, error: true); }
    }
    private async Task TrackInstallerAsync(UpdateLaunchOperation<Process> launch, UpdateInstallAttempt? attempt)
    {
        void Remember(string phase, int? pid = null, int? exitCode = null)
        {
            if (attempt is null) return;
            attempt = attempt with { Phase = phase, ProcessId = pid, ExitCode = exitCode };
            try { UpdateInstallAttempt.Save(UpdateAttemptPath, attempt); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            using var installer = await launch.Started;
            _installerHasProcess = true;
            Remember("process-started", installer.Id);
            UpdateStateText.Text = "安装进程已创建，正在等待安装器进入。";
            var waiting = Stopwatch.StartNew();
            var exited = installer.WaitForExitAsync(_closing.Token);
            while (!exited.IsCompleted)
            {
                if (attempt?.AttemptId is { } id)
                {
                    var trace = InstallTrace.Read(Path.Combine(InstallTraceDirectory, InstallTrace.FileName(id)), id, attempt.TargetCode, DateTimeOffset.UtcNow);
                    if (trace is not null) UpdateStateText.Text = trace.Describe();
                    else if (waiting.Elapsed > TimeSpan.FromSeconds(30)) UpdateStateText.Text = "进程已创建，但尚无安装器入口记录；仍在等待，请勿重复安装。";
                }
                if (waiting.Elapsed > TimeSpan.FromMinutes(5)) UpdateStateText.Text = "安装等待较久，进程仍在运行；请查看安装窗口或安装记录。";
                await Task.WhenAny(exited, Task.Delay(1000, _closing.Token));
                _closing.Token.ThrowIfCancellationRequested();
            }
            await exited;
            Remember("exited", installer.Id, installer.ExitCode);
            UpdateStateText.Text = installer.ExitCode switch
            {
                0 => attempt is null ? "恢复向导已退出，请重新打开我控制确认版本。" : "安装器已退出，请重新打开我控制确认新版本。",
                16 => "另一项安装或恢复正在进行，请等待它完成。",
                17 => "恢复程序等待上个进程退出超时，可稍后重试。",
                _ => "安装未完成，请查看安装记录后重试。"
            };
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        { Remember("cancelled"); if (!_closing.IsCancellationRequested) UpdateStateText.Text = "已取消 Windows 安装授权，可稍后重新安装。"; }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception error)
        { Remember("launch-failed"); if (!_closing.IsCancellationRequested) UpdateStateText.Text = "未完成安装启动：" + error.Message; }
        finally { _installerInFlight = false; if (!_closing.IsCancellationRequested) RenderUpdateActions(); }
    }
    private UpdateLaunchOperation<Process> LaunchUpdate(string executable, string[] arguments, IDisposable? packageLock = null)
    {
        // Capture the real window owner before scheduling the STA shell call.
        var start = UpdateShellStartInfo.Create(executable, arguments, AppContext.BaseDirectory,
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var tracePath = Path.Combine(UpdateCacheRoot, "launch-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var elapsed = Stopwatch.StartNew();
        void Observe(string phase, Exception? error)
        {
            Directory.CreateDirectory(UpdateCacheRoot);
            File.AppendAllText(tracePath, JsonSerializer.Serialize(new {
                phase, elapsedMs = elapsed.ElapsedMilliseconds, utc = DateTimeOffset.UtcNow,
                productCode = ProductVersion.Code, ownerPresent = start.ErrorDialogParentHandle != IntPtr.Zero,
                errorType = error?.GetType().Name,
                nativeErrorCode = (error as System.ComponentModel.Win32Exception)?.NativeErrorCode
            }) + Environment.NewLine);
        }
        return new(() => Process.Start(start) ?? throw new IOException("Windows 未返回安装进程。"), packageLock, action =>
        {
            var worker = new Thread(() => action()) { IsBackground = true, Name = "iControl installer launch" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }, Observe);
    }
}
