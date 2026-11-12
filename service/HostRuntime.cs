using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed class Engine(string profile, bool desired)
{
    public string Profile { get; } = profile;
    public bool Desired { get; set; } = desired;
    public OwnedProcess? Process { get; set; }
    public string Message { get; set; } = "尚未启动";
    public int Failures { get; set; }
    public DateTimeOffset RetryAt { get; set; }
    public DateTimeOffset ProcessStartedAt { get; set; }
    public Stopwatch? ReadinessWatch;
}

internal sealed record DesiredState(bool Remote = true, bool Tablet = false, bool Network = false,
    bool PrivacyScreen = false);
internal sealed record TabletPreparationResult(TabletDisplayMode Mode, Dictionary<string, long> TimingsMs, TabletDisplayMode[] SupportedModes);

internal sealed partial class HostRuntime : IAsyncDisposable
{
    private readonly string _root, _data;
    private readonly bool _interactive;
    private readonly ProfileStore _profiles;
    private readonly BackendApi _backend;
    private readonly Engine _remote = new("remote", true), _tablet = new("tablet", false);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _networkGate = new(1, 1), _desiredGate = new(1, 1);
    private NetworkEngineStatus _networkSnapshot = new(false, false, null, "异地连接尚未启用");
    private readonly LiveSessionState _remoteSession = new();
    private readonly RemoteWarmupPolicy _remoteWarmup = new();
    private DateTimeOffset _remoteHealthNextCheck;
    private int _remoteWarmupFailures;
    private readonly FileTransferVault? _files;
    private readonly ClipboardBroker _clipboard = new();
    private readonly HashSet<string> _lockOnExit = new();
    private readonly HashSet<string> _loginInputUsed = new();
    private DesktopInputState _desktopInputState = new();
    private DateTimeOffset _desktopInputReadAt;
    private readonly MaintenanceWindow _maintenance = new();
    private readonly EmbeddedNetworkClient _networkClient = new();
    private readonly NetworkConfiguration _networkConfiguration;
    private readonly TransportSettingsStore _transportSettings;
    private readonly NetworkHandover _handover;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private OwnedProcess? _network;
    private OwnedProcess? _privacyScreen;
    private PrivacyScreenLifetime? _privacyScreenStop;
    private Task[] _networkDrains = [];
    private volatile bool _networkDesired;
    private bool _privacyScreenEnabled;
    private DateTimeOffset _privacyScreenRetryAt;
    private int _privacyScreenFailures;
    private string _privacyScreenMessage = "远控隐私保护已关闭";
    private static readonly TimeSpan PrivacyScreenGracefulStop = TimeSpan.FromSeconds(1);
    private bool _disposed;
    // BUSY alone is not a session lease: Sunshine may retain it after an
    // ungraceful receiver disconnect. Bound every new receiver session to a
    // heartbeat, while allowing enough grace for the initial RTSP handshake.
    private bool _tabletStreamingSeen;
    // A rotation briefly changes Sunshine from BUSY to READY while Android
    // recreates its video activity. Keep the healthy engine alive during that
    // gap; an actual dead receiver is still bounded by lease heartbeats.
    private DateTimeOffset _tabletStreamLostAt;
    private string? _tabletLeaseId, _tabletClientId;
    private DateTimeOffset _tabletPreparationExpiresAt, _tabletHeartbeatExpiresAt;
    private TabletDisplayMode? _tabletMode, _tabletRequestedMode;
    private TabletDisplayMode[] _tabletSupportedModes = [];
    private DateTimeOffset _tabletWarmExpiresAt;
    private static readonly TimeSpan TabletWarmGrace = TimeSpan.FromSeconds(20);
    private readonly Func<TabletDisplayRequest, CancellationToken, Task<TabletDisplayNegotiation>> _configureTabletMode;
    private readonly Dictionary<(string? ClientId, string LeaseId), DateTimeOffset> _releasedTabletLeases = new();
    private static readonly TimeSpan TabletPreparationGrace = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan TabletHeartbeatGrace = TimeSpan.FromSeconds(25);
    private DateTimeOffset _networkRetryAt;
    private string _networkMessage = "异地连接尚未启用";
    private string _deviceId = "";
    private SecurityIdentifier? _ownerSid;
    private ControlApiServer? _controlApi;
    private string? _controlApiError;
    private DateTimeOffset _controlApiRetryAt;
    private bool _displayCleanupDone;
    private DateTimeOffset _displayCleanupRetryAt;
    private string ControlIdentityDirectory => Path.Combine(_data, "control-api");
    private readonly HttpClient _probe = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(900) };

    public HostRuntime(string root, string data, bool interactive)
    {
        (_root, _data, _interactive) = (Path.GetFullPath(root), Path.GetFullPath(data), interactive);
        _profiles = new(_root, _data);
        _configureTabletMode = (request, token) => DisplayModeProbe.ConfigureTabletAsync(_root, _interactive, request, token);
        _backend = new(_profiles);
        _networkConfiguration = new(_root, _data);
        _transportSettings = new(_data);
        _handover = new(_data);
        try { _files = new(Path.Combine(_data, "file-transfer"), () => _remoteSession.IsStreaming(DateTimeOffset.UtcNow) ||
            _tabletLeaseId is not null && DateTimeOffset.UtcNow < _tabletHeartbeatExpiresAt);
            _files.UserFiles=new OwnerFileAccess(()=>_ownerSid?.Value,_interactive,_root); }
        catch { _files = null; } // A broken optional queue cannot stop remote control.
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(Path.Combine(_data, "logs"));
        _ownerSid = ReadOwnerSid();
        var identityPath = Path.Combine(_data, "device-id.txt");
        if (!File.Exists(identityPath)) await File.WriteAllTextAsync(identityPath, Guid.NewGuid().ToString("D"), cancellationToken);
        _deviceId = (await File.ReadAllTextAsync(identityPath, cancellationToken)).Trim();
        var desiredPath = Path.Combine(_data, "desired.json");
        if (File.Exists(desiredPath))
        {
            try
            {
                var desired = JsonSerializer.Deserialize<DesiredState>(await File.ReadAllTextAsync(desiredPath, cancellationToken));
                // A display session cannot survive a service restart. Restoring
                // Tablet=true would recreate an invisible desktop with no live
                // receiver, so only persistent remote/network intent is restored.
                if (desired is not null)
                {
                    (_remote.Desired, _networkDesired, _privacyScreenEnabled) =
                        (desired.Remote, desired.Network, desired.PrivacyScreen);
                    _privacyScreenFailures = 0;
                    _privacyScreenMessage = PrivacyScreenPolicy.Message(_privacyScreenEnabled, false, false, false);
                }
            }
            catch (JsonException) { Log("期望状态配置损坏，按默认安全设置启动。"); }
        }
        Log("后台服务已启动。");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var supervisor = SuperviseAsync(lifetime.Token);
        var listener = ListenAsync(lifetime.Token);
        var networkSupervisor = SuperviseNetworkAsync(lifetime.Token);
        try
        {
            // WhenAll alone would wait forever for the pipe listener after a supervisor
            // fault, leaving SCM reporting a running host that can never recover engines.
            var completed = await Task.WhenAny(supervisor, listener, networkSupervisor);
            await completed;
            if (!cancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("后台监督任务意外结束，服务将自动重新启动。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            // Cancel incoming API work before draining the engine/IPC supervisors.
            // API handlers use the same local IPC path as the owner UI, never a
            // nested call while this runtime holds _gate.
            if (_controlApi is not null)
            {
                await _controlApi.DisposeAsync();
                _controlApi = null;
            }
            try { await Task.WhenAll(supervisor, listener, networkSupervisor); }
            catch (Exception) when (lifetime.IsCancellationRequested) { /* Preserve the first failure. */ }
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                await StopEngineAsync(_tablet, CancellationToken.None);
                try { await DisableDisplayAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                { Log("服务退出时停用副屏提示：" + ex.Message); }
                await StopPrivacyScreenAsync(CancellationToken.None);
                await StopEngineAsync(_remote, CancellationToken.None);
            }
            finally { _gate.Release(); }
            await _networkGate.WaitAsync(CancellationToken.None);
            try { await StopNetworkProcessAsync(CancellationToken.None); }
            finally { PublishNetworkStatus(); _networkGate.Release(); }
            Log("后台服务已停止。");
        }
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await StopExpiredPrivacyStreamAsync(cancellationToken);
                await EnsureControlApiAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                if (_tablet.Desired && TabletLeaseExpired(now))
                    await EndTabletSessionAsync("副屏连接已超时，已回收桌面。", cancellationToken);
                foreach (var engine in new[] { _remote, _tablet })
                {
                    ObserveExitedEngine(engine);
                    if (!engine.Desired && engine.Process?.Running == true &&
                        (engine != _tablet || !TabletWarmActive(now)))
                        await StopEngineAsync(engine, cancellationToken);
                    if (engine.Process?.Running == true && now - engine.ProcessStartedAt >= TimeSpan.FromSeconds(30))
                        engine.Failures = 0;
                    if (engine.Process?.Running == true && !_interactive && engine.Process.SessionId != SessionLauncher.ActiveSession)
                    {
                        if (engine == _tablet)
                            await EndTabletSessionAsync("电脑登录会话已切换，请重新连接副屏。", cancellationToken);
                        else await StopEngineAsync(engine, cancellationToken);
                    }
                    if (engine == _tablet && _tabletStreamingSeen && engine.Process?.Running != true)
                        await EndTabletSessionAsync("副屏引擎已退出，已回收桌面。", cancellationToken);
                    if (engine.Desired && engine.Process?.Running != true && now >= engine.RetryAt)
                        await TryStartEngineAsync(engine, cancellationToken);
                }
                if (now >= _remoteHealthNextCheck)
                {
                    _remoteHealthNextCheck = now.AddSeconds(5);
                    var running = _remote.Process?.Running == true;
                    var healthy = running && (await GetEngineStatusAsync(_remote, cancellationToken)).Healthy;
                    if (healthy) _remoteWarmupFailures = 0;
                    if (_remoteWarmup.ShouldRestart(now, _remote.ProcessStartedAt, _remote.Desired, running,
                        healthy, _remoteSession.HasLease(now)))
                    {
                        Log("主屏引擎持续不健康且没有活动会话，正在恢复。");
                        await StopEngineAsync(_remote, cancellationToken);
                        _remoteWarmupFailures = Math.Min(_remoteWarmupFailures + 1, 5);
                        _remote.RetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, _remoteWarmupFailures)));
                        _remote.Message = "主屏引擎无响应，正在恢复";
                    }
                }
                var tabletStatus = await GetEngineStatusAsync(_tablet, cancellationToken);
                if (_tablet.Desired && tabletStatus.Healthy && tabletStatus.Streaming)
                {
                    if (!_tabletStreamingSeen)
                        _tabletHeartbeatExpiresAt = DateTimeOffset.UtcNow + TabletHeartbeatGrace;
                    _tabletStreamingSeen = true;
                    _tabletStreamLostAt = default;
                }
                else if (_tablet.Desired && tabletStatus.Healthy && !tabletStatus.Streaming && _tabletStreamingSeen)
                {
                    if (_tabletStreamLostAt == default) _tabletStreamLostAt = now;
                    // A rotation reconnect intentionally creates this BUSY to
                    // READY transition. Give it time to prepare the new mode
                    // while the Android lease heartbeat remains valid.
                    // A rotation can leave Sunshine READY briefly while the
                    // Android activity is recreated. Keep the display lease
                    // alive as long as its heartbeat is still valid; only a
                    // genuinely expired lease may trigger cleanup here.
                    if (now - _tabletStreamLostAt >= TimeSpan.FromSeconds(15) && TabletLeaseExpired(now))
                        await EndTabletSessionAsync("副屏连接已结束，已回收桌面。", cancellationToken);
                }
                await ReconcilePrivacyScreenAsync(cancellationToken);
                // Retry a failed detach even after stop/release has completed.
                // The helper only removes this product's MTT display path.
                // A successful detach is an observation, not a permanent state.
                // Windows/device recovery can reattach a saved MTT path later.
                // Reconcile idle desktops periodically through the same strictly
                // scoped helper, including after startup, resume and logon changes.
                if (!_tablet.Desired && DateTimeOffset.UtcNow >= _displayCleanupRetryAt)
                    await TryCleanupDisplayAsync(cancellationToken);

            }
            finally { _gate.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(_ownerSid ?? throw new InvalidOperationException("产品所有者尚未登记，请修复安装。"),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            var pipe = NamedPipeServerStreamAcl.Create(RuntimeProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 16384, 65536, security);
            try { await pipe.WaitForConnectionAsync(cancellationToken); }
            catch { pipe.Dispose(); throw; }
            _ = ServeClientAsync(pipe, cancellationToken);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(65));
            try
            {
                var authorized = false;
                pipe.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    authorized = identity.User is not null && (_ownerSid?.Equals(identity.User) == true ||
                        identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                        new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
                });
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var requestText = await ReadBoundedLineAsync(reader, ClipboardPolicy.MaximumEnvelopeBytes, timeout.Token);
                var request = JsonSerializer.Deserialize<RuntimeRequest>(requestText, RuntimeProtocol.JsonOptions);
                RuntimeResponse response;
                if (authorized && request?.Command == "files-stage")
                {
                    FileStream? selected = null;
                    IDisposable? operation = null;
                    try
                    {
                        operation = _maintenance.Enter();
                        if (_files is null || request.FilePath is not { Length: > 0 and <= 4096 } || request.ClientId is null) throw new IOException("请选择接收设备和文件。");
                        // Open with the authenticated caller's permissions. Never
                        // let a service path request read files as LocalSystem.
                        pipe.RunAsClient(() => selected = new FileStream(request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferVault.ChunkBytes, true));
                        var item = _files.QueueOutgoing(request.ClientId, Path.GetFileName(request.FilePath), selected!.Length);
                        var owned = selected; selected = null; var ownedOperation = operation; operation = null;
                        _ = Task.Run(async () => { using (ownedOperation) await using (owned) { try { await _files.StageOutgoingAsync(item.Id, owned, cancellationToken); } catch { Log("文件暂存未完成，请在文件页面重试。"); } } });
                        response = new(true, "文件已加入暂存队列，完成后平板可接收。");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { response = new(false, "文件未加入队列：" + ex.Message); }
                    finally { selected?.Dispose(); operation?.Dispose(); }
                }
                else if (authorized && request?.Command == "clipboard-poll")
                    response = new(true, "", PayloadJson: JsonSerializer.Serialize(_clipboard.Take(), ClipboardPolicy.WireJsonOptions));
                else if (authorized && request?.Command == "clipboard-take")
                    response = new(true, "", PayloadJson: JsonSerializer.Serialize(
                        ClipboardAgentIdValid(request.RequestId) ? _clipboard.Take(request.RequestId) : null, ClipboardPolicy.WireJsonOptions));
                else if (authorized && request?.Command == "clipboard-complete")
                {
                    var payload = request.ClipboardFormat is not null && request.ClipboardData is not null
                        ? new ClipboardPayload(request.ClipboardFormat, request.ClipboardData) : null;
                    _clipboard.Complete(request.RequestId, request.ClipboardSuccess == true, payload, request.ClipboardError);
                    response = new(true, "");
                }
                else if (authorized && request?.Command is "files-status" or "files-configure" or "files-grant" or "files-remove" or "files-cancel" or "files-clear" or "files-root-add" or "files-root-remove" or "files-permissions")
                    response = await HandleFilesAsync(request, timeout.Token);
                else response = !authorized ? new RuntimeResponse(false, "此 Windows 账号未获授权管理我控制。请使用安装者账号，或以管理员身份打开。") :
                    request is null ? new RuntimeResponse(false, "操作请求无效。") : await HandleAsync(request, timeout.Token);
                var options = request?.Command?.StartsWith("clipboard-", StringComparison.Ordinal) == true
                    ? ClipboardPolicy.WireJsonOptions : RuntimeProtocol.JsonOptions;
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, options).AsMemory(), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
            { /* A disconnected or malformed caller cannot stop the host. */ }
        }
    }

    private async Task<RuntimeResponse> HandleFilesAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        // File I/O never holds the runtime gate used by heartbeats, stop and
        // display reclamation. This remains independent even with large files.
        try
        {
            if (_files is null) throw new IOException("文件队列无法读取，文件功能已停用；远控不受影响。");
            if (request.Command == "files-configure") _files.Configure(request.TransferEnabled == true, request.FilePath ?? "", request.BrowseEnabled);
            if(request.Command=="files-root-add") _files.AddBrowseRoot(request.FilePath??"",_ownerSid?.Value??"");
            if(request.Command=="files-root-remove") _files.RemoveBrowseRoot(request.BrowseRootId??"");
            if(request.Command=="files-permissions") _files.SetDevicePermissions(request.ClientId??"",request.UploadAllowed==true,request.DeleteAllowed==true);
            if (request.Command == "files-grant") _files.Grant(request.ClientId ?? "", request.TransferEnabled == true);
            if (request.Command == "files-remove") _files.RemoveDevice(request.ClientId ?? "");
            if (request.Command == "files-cancel") await _files.CancelAsync(request.RequestId ?? "", cancellationToken);
            if (request.Command == "files-clear") await _files.ClearFinishedAsync(cancellationToken);
            return new(true, "文件传输状态已更新。", PayloadJson: JsonSerializer.Serialize(_files.Snapshot(), RuntimeProtocol.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, "文件操作未完成：" + ex.Message); }
    }

    private SecurityIdentifier ReadOwnerSid()
    {
        var path = Path.Combine(_data, "owner.sid");
        if (!File.Exists(path)) throw new InvalidOperationException("产品所有者尚未登记，请重新运行我控制安装程序修复。");
        try
        {
            return new SecurityIdentifier(File.ReadAllText(path).Trim());
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("产品所有者记录无效，请重新运行我控制安装程序修复。", ex);
        }
    }

    private async Task<RuntimeResponse> HandleAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        var networkCommand = request.Command?.ToLowerInvariant();
        if (networkCommand?.StartsWith("network-", StringComparison.Ordinal) == true && networkCommand != "network-provider")
            return await HandleNetworkAsync(request, cancellationToken);
        if (networkCommand == "remote-heartbeat")
        {
            // Independent from display preparation, health probes and UI status.
            // LiveSessionState serializes identity/expiry; only that generation
            // may refresh its private overlay. A retired event is never reused.
            try
            {
                if (string.IsNullOrWhiteSpace(request.LeaseId) || string.IsNullOrWhiteSpace(request.ClientId))
                    throw new InvalidOperationException("会话或接收设备标识缺失。");
                var lifetime = Volatile.Read(ref _privacyScreenStop);
                var generation = _remoteSession.Heartbeat(request.LeaseId, request.ClientId, request.StreamReady == true,
                    DateTimeOffset.UtcNow, request.FastPrivacyHeartbeat == true, request.BoundedPrivacyHeartbeat == true,
                    lifetime?.Generation == _remoteSession.Generation);
                if (request.StreamReady == true && lifetime?.Generation == generation) lifetime.Heartbeat();
                return new(true, "会话状态已更新。");
            }
            catch (InvalidOperationException ex) { return new(false, ex.Message, ErrorStatus: 409); }
        }
        var mediaIdentity = string.Equals(request.Command, "media-identity", StringComparison.OrdinalIgnoreCase);
        var providerChange = string.Equals(request.Command, "network-provider", StringComparison.OrdinalIgnoreCase);
        // Bound the complete synchronous preflight below status and heartbeat deadlines.
        // Absorb a short status/heartbeat probe without queuing behind a long
        // preparation. The wait still consumes the existing identity budget.
        using var mediaBudget = mediaIdentity || providerChange ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (mediaBudget is not null) mediaBudget.CancelAfter(BackendApi.MediaIdentityTimeout);
        var requestToken = mediaBudget?.Token ?? cancellationToken;
        if (mediaIdentity || providerChange)
        {
            if (!await _gate.WaitAsync(mediaIdentity ? 500 : 0, requestToken))
                return new(false, "电脑正在处理其他操作，请稍后重试。", ErrorStatus: 409);
        }
        else await _gate.WaitAsync(cancellationToken);
        try
        {
            var command = request.Command?.ToLowerInvariant();
            if (_maintenance.Active && command is "remote-prepare" or "tablet-prepare" or "start" or "restart" or "pair" or "tablet-request" or "remote-reconfigure" or "tablet-configure" or "configure")
                throw new InvalidOperationException("电脑正在安装更新，请稍后重试。");
            string message;
            string? payload = null;
            Dictionary<string, long>? timings = null;
            if (command == "status") message = "状态已更新。";
            else if (command == "lan-discovery-status")
                return new(true, "附近发现状态已读取。", PayloadJson: JsonSerializer.Serialize(_controlApi?.DiscoveryStatus, RuntimeProtocol.JsonOptions));
            else if (command == "maintenance-begin")
            {
                _maintenance.Begin(request.RequestId ?? "", _remoteSession.HasLease(DateTimeOffset.UtcNow) ||
                    _tabletLeaseId is not null ||
                    _remoteSession.IsStreaming(DateTimeOffset.UtcNow) || _privacyScreen is not null ||
                    _files?.Snapshot().Items.Any(item => item.State is not ("completed" or "failed" or "cancelled")) == true);
                message = "已暂停新连接，准备安装更新。";
            }
            else if (command == "maintenance-end") { _maintenance.End(request.RequestId ?? ""); message = "维护已结束。"; }
            else if (command is "control-info" or "control-import")
            {
                if (command == "control-import" && !File.Exists(ControlApiIdentity.PathFor(ControlIdentityDirectory)))
                {
                    if (request.ControlIdentity is not { Length: > 0 and <= 24000 })
                        throw new InvalidOperationException("设备绑定身份格式无效。");
                    byte[] plain;
                    try { plain = Convert.FromBase64String(request.ControlIdentity); }
                    catch (FormatException) { throw new InvalidOperationException("设备绑定身份格式无效。"); }
                    try
                    {
                        var identity = ControlApiIdentity.Decode(plain);
                        try { identity.WriteNew(ControlIdentityDirectory, DataProtectionScope.LocalMachine); }
                        finally { identity.Clear(); }
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                    _controlApiRetryAt = default;
                }
                await EnsureControlApiAsync(cancellationToken);
                var running = _controlApi?.IsRunning == true;
                payload = JsonSerializer.Serialize(new ControlApiStatus(running,
                    File.Exists(ControlApiIdentity.PathFor(ControlIdentityDirectory)),
                    running ? _controlApi!.GetInvitation() : null,
                    running ? null : _controlApiError ?? "请先用安装者账号打开一次我控制，完成设备绑定接口初始化。"), RuntimeProtocol.JsonOptions);
                message = running ? "后台设备接口已就绪，退出电脑界面后仍可连接。" : "设备绑定接口正在等待初始化。";
            }
            else if (command is "binding-start" or "binding-status" or "binding-approve" or "binding-reject" or "binding-cancel")
            {
                await EnsureControlApiAsync(cancellationToken);
                if (_controlApi?.IsRunning != true) throw new InvalidOperationException("设备绑定接口尚未启动。");
                payload = _controlApi.BindingCommand(request);
                message = "设备确认状态已更新。";
            }
            else if (command is "receiver-list" or "tablet-request" or "tablet-request-status" or "tablet-request-cancel")
            {
                // Receiver discovery/request state belongs to the API, but the
                // owner UI reaches it through this same authenticated local pipe.
                // Do not make an HTTP self-call while holding the runtime gate.
                await EnsureControlApiAsync(cancellationToken);
                if (_controlApi?.IsRunning != true)
                    throw new InvalidOperationException("设备连接接口尚未就绪，请稍后重试。");
                payload = _controlApi.ReceiverCommand(command, request);
                message = command == "tablet-request" ? "副屏邀请已发送，正在等待设备接受。" : "副屏设备状态已更新。";
            }
            else if (command is "remote-prepare" or "remote-release")
            {
                if (string.IsNullOrWhiteSpace(request.LeaseId) || string.IsNullOrWhiteSpace(request.ClientId))
                    throw new InvalidOperationException("会话或接收设备标识缺失。");
                if (command == "remote-prepare")
                {
                    if (!await StopExpiredPrivacyStreamAsync(cancellationToken))
                        throw new InvalidOperationException("正在安全结束上一条隐私保护连接，请稍后重试。");
                    _remoteSession.Prepare(request.LeaseId, request.ClientId, DateTimeOffset.UtcNow);
                    _lockOnExit.RemoveWhere(lease => lease != request.LeaseId);
                    _loginInputUsed.RemoveWhere(lease => lease != request.LeaseId);
                }
                else {
                    bool matching=_remoteSession.IsStreaming(request.LeaseId,request.ClientId,DateTimeOffset.UtcNow);
                    bool shouldLock=SessionExitPolicy.ShouldLock(matching,request.ExitReason,request.LockOnExit);
                    _remoteSession.End(request.LeaseId,request.ClientId);_lockOnExit.Remove(request.LeaseId);
                    await ReconcilePrivacyScreenAsync(cancellationToken);
                    if(shouldLock)await LockDesktopAsync(cancellationToken);
                }
                await ReconcilePrivacyScreenAsync(cancellationToken);
                message = command == "remote-release" ? "会话已停止，电脑保持待机。" : "会话状态已更新。";
            }
            else if (command is "clipboard-authorize" or "clipboard-claim")
            {
                var job = command == "clipboard-claim" ? _clipboard.Claim(request.RequestId) : null;
                if (command == "clipboard-claim" && job is null) throw new InvalidOperationException("剪贴板请求已结束或超时。");
                var profile = job?.Mode ?? request.Profile;
                var lease = job?.LeaseId ?? request.LeaseId;
                var client = job?.ClientId ?? request.ClientId;
                var now = DateTimeOffset.UtcNow;
                bool allowed = profile == "remote"
                    ? _remoteSession.IsStreaming(lease ?? "", client ?? "", now)
                    : profile == "tablet" && _tabletStreamingSeen && !TabletLeaseExpired(now) &&
                      _tabletLeaseId == lease && _tabletClientId == client && _tabletLeaseId is not null;
                if (!allowed)
                {
                    if (job is not null) _clipboard.Complete(job.Id, false, null, "当前会话已结束，无法访问剪贴板。");
                    throw new InvalidOperationException("当前会话已结束或尚未连接，无法访问剪贴板。");
                }
                message = "会话已核实。";
            }
            else if (command == "tablet-prepare")
            {
                var prepared = await PrepareTabletSessionAsync(request, cancellationToken);
                payload = JsonSerializer.Serialize(new { prepared.Mode.Width, prepared.Mode.Height, prepared.Mode.RefreshRate,
                    prepared.Mode.Orientation, prepared.Mode.Degraded, prepared.SupportedModes }, RuntimeProtocol.JsonOptions);
                timings = prepared.TimingsMs;
                message = prepared.Mode.Degraded ? "副屏已准备，已选择设备支持的显示模式。" : "副屏已准备，正在等待设备连接。";
            }
            else if (command == "tablet-heartbeat")
            {
                ValidateTabletLease(request);
                if (TabletLeaseExpired(DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("副屏连接租约已过期，请重新连接。");
                _tabletHeartbeatExpiresAt = DateTimeOffset.UtcNow + TabletHeartbeatGrace;
                message = "副屏连接已保持。";
            }
            else if (command == "tablet-release")
            {
                RememberTabletLeaseRelease(request);
                if (_tabletLeaseId is not null)
                {
                    ValidateTabletLease(request);
                    await EndTabletSessionAsync("副屏连接已结束。", cancellationToken, allowWarm: true);
                }
                message = _displayCleanupDone ? "副屏已断开，桌面已回收。" : "副屏已断开，正在回收桌面。";
            }
            else if (command == "media-identity")
            {
                _ = MediaIdentityPolicy.ParseCertificate(request.Profile, request.ClientCertificate);
                var engine = GetEngine(request.Profile);
                if (engine.Process?.Running != true)
                    throw new MediaIdentityException(409, "请先准备所选串流模式，再检查媒体身份。");
                var result = await _backend.GetMediaIdentityAsync(engine.Profile, request.ClientCertificate,
                    token => CanDeduplicateMediaIdentityAsync(engine, token), requestToken);
                requestToken.ThrowIfCancellationRequested();
                // No unrelated profile probe or lifecycle/state update is needed.
                return new(true, "媒体身份预检已完成。", PayloadJson: JsonSerializer.Serialize(result, RuntimeProtocol.JsonOptions));
            }
            else if (command is "start" or "stop" or "restart" or "pair")
            {
                var engine = GetEngine(request.Profile);
                if (command == "pair")
                {
                    if (engine.Process?.Running != true) throw new InvalidOperationException("请先启动所选模式，再配对设备。");
                    await _backend.PairAsync(engine.Profile, request.Pin, request.Name, cancellationToken);
                    message = "已确认配对，请在安卓设备上等待连接完成。";
                }
                else
                {
                    if (engine == _tablet && command == "stop")
                    {
                        await EndTabletSessionAsync("副屏已停止。", cancellationToken);
                        return new(true, _displayCleanupDone ? "副屏已停止，桌面已回收。" : "副屏已停止，正在回收桌面。",
                            await SnapshotAsync(cancellationToken));
                    }
                    engine.Desired = command != "stop";
                    if (engine == _remote && command is "stop" or "restart") _remoteSession.Reset();
                    if (engine == _remote && command is "stop" or "restart") await ReconcilePrivacyScreenAsync(cancellationToken);
                    if (engine == _tablet && engine.Desired)
                    {
                        _displayCleanupDone = false;
                        // Preserve the preparation lease supplied by the new
                        // receiver. Older clients have only the handshake grace
                        // and the existing BUSY/IDLE disconnect check.
                        if (_tabletLeaseId is null && !_tabletStreamingSeen)
                            _tabletPreparationExpiresAt = DateTimeOffset.UtcNow + TabletPreparationGrace;
                    }
                    await SaveDesiredAsync(cancellationToken);
                    if (command is "stop" or "restart") await StopEngineAsync(engine, cancellationToken);
                    if (engine.Desired)
                    {
                        await TryStartEngineAsync(engine, cancellationToken);
                        if (engine.Process?.Running != true) throw new InvalidOperationException(engine.Message);
                    }
                    message = command == "stop" ? "所选串流模式已停止。" : "串流模式已启动，正在检查就绪状态。";
                }
            }
            else if (command == "remote-reconfigure")
            {
                if (request.Profile != "remote" || !_remoteSession.IsStreaming(request.LeaseId ?? "", request.ClientId ?? "", DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("原远控会话已结束，请重新连接。");
                message = "当前设备可以重连调整画面参数。";
            }
            else if(command=="remote-desktop")
            {
                if(request.Profile != "remote" || !_remoteSession.IsStreaming(request.LeaseId??"",request.ClientId??"",DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("当前远控会话已结束。");
                if(request.ClipboardAction=="configure") {
                    if(request.LockOnExit==true)_lockOnExit.Add(request.LeaseId!);else _lockOnExit.Remove(request.LeaseId!);
                    message=request.LockOnExit==true?"主动退出本次远控后将锁定电脑；内部重连不锁屏。":"已关闭本次退出锁屏。";
                } else if(request.ClipboardAction=="lock") {
                    await LockDesktopAsync(cancellationToken);message="已请求锁定电脑。";
                } else if(request.ClipboardAction is "status" or "login-fill") {
                    bool fill=request.ClipboardAction=="login-fill";
                    if(fill) {
                        DesktopInputPolicy.ValidatePassword(request.LoginSecret);
                        if(!_loginInputUsed.Add(request.LeaseId!))
                            throw new InvalidOperationException("本次连接已尝试填写密码，请手动确认；如需重试请重新连接。");
                    }
                    LoginInputResult login=new("status");
                    if(fill || DateTimeOffset.UtcNow-_desktopInputReadAt>TimeSpan.FromMilliseconds(600)) {
                        var observed=await DesktopInputProbe.RequestAsync(_root,_interactive,
                            _ownerSid?.Value ?? throw new InvalidOperationException("授权的 Windows 用户不可用。"),
                            fill?request.LoginSecret:null,request.LoginSubmit==true,cancellationToken);
                        _desktopInputState=observed.Desktop;login=observed.Login;_desktopInputReadAt=DateTimeOffset.UtcNow;
                        // A not-ready answer proves zero input was sent. All uncertain/partial
                        // attempts remain consumed, including a timeout or lost response.
                        if(fill && login.State=="not-ready")_loginInputUsed.Remove(request.LeaseId!);
                    }
                    payload=JsonSerializer.Serialize(new {desktop=_desktopInputState,login,
                        lockOnExit=_lockOnExit.Contains(request.LeaseId!)},RuntimeProtocol.JsonOptions);
                    return new(true,fill?login.Filled?"密码已填写，请确认电脑登录结果。":"未填写密码；请点选 Windows 密码框，或改为手动输入。":"桌面输入状态已读取。",PayloadJson:payload);
                } else message=_lockOnExit.Contains(request.LeaseId!)?"退出锁屏已开启":"退出锁屏已关闭";
            }
            else if (command == "remote-privacy")
            {
                if (request.Profile != "remote" || !_remoteSession.IsStreaming(request.LeaseId ?? "", request.ClientId ?? "", DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("请先连接主屏远控，再调整隐私保护。");
                if (request.PrivacyEnabled is bool enabled)
                {
                    _privacyScreenEnabled = enabled;
                    _privacyScreenFailures = 0;
                    _privacyScreenRetryAt = default;
                    await SaveDesiredAsync(cancellationToken);
                    await ReconcilePrivacyScreenAsync(cancellationToken);
                }
                message = _privacyScreenMessage;
            }
            else if (command == "privacy-set")
            {
                if (request.PrivacyEnabled is not bool enabled)
                    throw new InvalidOperationException("隐私保护设置无效。");
                _privacyScreenEnabled = enabled;
                _privacyScreenFailures = 0;
                _privacyScreenRetryAt = default;
                await SaveDesiredAsync(cancellationToken);
                await ReconcilePrivacyScreenAsync(cancellationToken);
                message = enabled
                    ? _privacyScreen?.Running == true ? "远控隐私保护已开启。" : _privacyScreenMessage
                    : "远控隐私保护已关闭。";
            }
            else if (command == "privacy-recover")
            {
                _privacyScreenEnabled = false;
                _privacyScreenFailures = 0;
                _privacyScreenRetryAt = default;
                await StopPrivacyScreenAsync(cancellationToken);
                await SaveDesiredAsync(cancellationToken);
                _privacyScreenMessage = "本机屏幕已恢复，远控隐私保护已关闭";
                message = "本机屏幕已恢复，远控隐私保护已关闭。";
            }
            else if (command is "tablet-configure" or "configure")
            {
                var current = await GetEngineStatusAsync(_tablet, cancellationToken);
                if (current.Streaming) throw new InvalidOperationException("副屏正在串流，请先在安卓端退出会话，再更改显示配置。");
                await StopEngineAsync(_tablet, cancellationToken);
                await _profiles.ConfigureTabletAsync(request.Orientation, request.AutoOrientation, request.DisplayOutput, cancellationToken,
                    request.Width, request.Height, request.RefreshRate);
                _tabletMode = null;
                if (_tablet.Desired) await TryStartEngineAsync(_tablet, cancellationToken);
                message = "副屏配置已保存。";
            }
            else if (command == "stop-all")
            {
                _remote.Desired = false;
                _remoteSession.Reset();
                await ReconcilePrivacyScreenAsync(cancellationToken);
                await EndTabletSessionAsync("远程服务已停止。", cancellationToken);
                await SaveDesiredAsync(cancellationToken);
                await StopEngineAsync(_remote, cancellationToken);
                await _networkGate.WaitAsync(cancellationToken);
                try { _networkDesired = false; await SaveDesiredAsync(cancellationToken); await StopNetworkProcessAsync(cancellationToken); }
                finally { PublishNetworkStatus(); _networkGate.Release(); }
                message = "远程服务已停止，后台管理仍在运行。";
            }
            else if (command == "repair")
            {
                foreach (var engine in new[] { _remote, _tablet })
                {
                    if ((await GetEngineStatusAsync(engine, cancellationToken)).Streaming)
                        throw new InvalidOperationException("请先结束正在进行的串流，再修复组件。");
                }
                foreach (var engine in new[] { _remote, _tablet })
                {
                    await StopEngineAsync(engine, cancellationToken);
                    await _profiles.EnsureAsync(engine.Profile, cancellationToken);
                    engine.Failures = 0;
                    if (engine.Desired) await TryStartEngineAsync(engine, cancellationToken);
                }
                message = "配置已检查并按当前设置重新启动。";
            }
            else if (command == "network-provider")
            {
                if (!await _networkGate.WaitAsync(0, requestToken))
                    return new(false, "组网操作正在进行，请稍后重试。", ErrorStatus: 409);
                try
                {
                        if (_remoteSession.HasLease(DateTimeOffset.UtcNow) || _tabletLeaseId is not null ||
                            _files?.Snapshot().Items.Any(item => item.State is not ("completed" or "failed" or "cancelled")) == true)
                            return new(false, "请先结束串流和未完成的文件任务，再切换通道。", ErrorStatus: 409);
                        var selected = new TransportSettings(request.Provider ?? "", request.ServerUrl ?? "", request.ManualAddresses ?? "").Validate();
                        if (_network?.Running == true) await _networkClient.ConfigureAsync(selected, requestToken);
                        _networkDesired = false;
                        await SaveDesiredAsync(requestToken);
                        requestToken.ThrowIfCancellationRequested();
                        _transportSettings.Save(selected);
                        payload = JsonSerializer.Serialize(selected, RuntimeProtocol.JsonOptions);
                        message = "已保存连接通道；异地连接已暂停，账号与电脑绑定保留。选择异地通道后可启用并登录。";
                        return new(true, message, PayloadJson: payload);
                }
                finally { PublishNetworkStatus(); _networkGate.Release(); }
            }
            else throw new ArgumentException("无法识别此操作，请更新我控制。");
            if (command is "clipboard-authorize" or "clipboard-claim") return new(true, message);
            return new(true, message, await SnapshotAsync(cancellationToken), payload, TimingsMs: timings);
        }
        catch (OperationCanceledException) when (providerChange && !cancellationToken.IsCancellationRequested)
        {
            return new(false, "通道应用超时，请重新读取设置后重试；连接可能已暂停。", ErrorStatus: 504);
        }
        catch (OperationCanceledException) when (mediaIdentity && !cancellationToken.IsCancellationRequested)
        {
            // The backend may already have sent an exact-UUID deletion. Recheck on retry.
            return new(false, "媒体身份预检超时，请重新核验；重复记录可能已部分清理。", ErrorStatus: 504);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (string.Equals(request.Command, "media-identity", StringComparison.OrdinalIgnoreCase))
                // Do not log certificate material, upstream bodies, credentials or filesystem paths.
                return new(false, ex is MediaIdentityException identityError ? identityError.Message : "媒体身份预检未完成，请检查所选模式和公开证书后重试。",
                    ErrorStatus: ex is MediaIdentityException mediaError ? mediaError.Status : ex is ArgumentException ? 400 : 503);
            if (request.Command is "clipboard-authorize" or "clipboard-claim") return new(false, ex.Message);
            if (request.Command is "remote-privacy" or "remote-reconfigure" or "remote-desktop") return new(false, ex.Message, ErrorStatus: 409);
            Log($"操作 {request.Command} 失败：{ex.Message}");
            return new(false, ex.Message, await SnapshotAsync(cancellationToken));
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> CanDeduplicateMediaIdentityAsync(Engine engine, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (engine.Process?.Running != true || (engine == _remote ? _remoteSession.IsStreaming(now)
            : _tabletStreamingSeen && !TabletLeaseExpired(now))) return false;
        // Require a positively observed FREE state, not merely a missing BUSY flag.
        // This callback runs under _gate, again immediately before each API deletion.
        try
        {
            var xml = await _probe.GetStringAsync($"http://127.0.0.1:{ProfileStore.Port(engine.Profile)}/serverinfo", cancellationToken);
            var root = XDocument.Parse(xml).Root;
            var states = root?.Elements("state").ToArray();
            return root?.Name == "root" && root.Attribute("status_code")?.Value == "200" &&
                states is { Length: 1 } && states[0].Value == "SUNSHINE_SERVER_FREE";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { return false; }
    }

    private Engine GetEngine(string? profile) => profile?.ToLowerInvariant() switch
    {
        "remote" => _remote, "tablet" => _tablet, _ => throw new ArgumentException("请选择远程主屏或平板副屏。")
    };

    private async Task LockDesktopAsync(CancellationToken token)
    {
        var executable=Path.Combine(_root,"service","串流中心后台.exe");
        using var helper=SessionLauncher.Start(executable,["--lock-desktop"],_root,_interactive,
            _ownerSid?.Value ?? throw new InvalidOperationException("授权的 Windows 用户不可用。"));
        await helper.Process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(4),token);
        if(helper.Process.ExitCode!=0)throw new InvalidOperationException("电脑锁屏未完成，请重试。");
    }

    private async Task<TabletPreparationResult> PrepareTabletSessionAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        if (request.LeaseId is null || !Regex.IsMatch(request.LeaseId, "^[A-Za-z0-9_-]{8,128}$"))
            throw new InvalidOperationException("副屏连接标识无效，请更新安卓端后重试。");
        if (request.ClientId is { Length: > 128 } || request.ClientId?.Any(char.IsControl) == true)
            throw new InvalidOperationException("副屏设备标识无效。");
        if (request.Width is not (>= 320 and <= 7680) || request.Height is not (>= 320 and <= 7680) ||
            request.RefreshRate is not (>= 24 and <= 240) || request.Orientation is not (0 or 90 or 180 or 270))
            throw new InvalidOperationException("副屏分辨率、刷新率或方向无效，请重新选择显示设置。");
        var timings = new Dictionary<string, long>();
        static void AddTiming(Dictionary<string, long> target, string key, long milliseconds)
        {
            target[key] = target.GetValueOrDefault(key) + Math.Max(0, milliseconds);
        }
        async Task StopTabletEngineWithTimingAsync()
        {
            var watch = Stopwatch.StartNew();
            await StopEngineAsync(_tablet, cancellationToken);
            AddTiming(timings, "stop-engine", watch.ElapsedMilliseconds);
        }
        PruneReleasedTabletLeases();
        if (_releasedTabletLeases.ContainsKey((request.ClientId, request.LeaseId)))
            throw new InvalidOperationException("本次副屏连接已取消，请重新发起连接。");

        var now = DateTimeOffset.UtcNow;
        if (_tablet.Desired && TabletLeaseExpired(now))
            await EndTabletSessionAsync("上次副屏连接已超时。", cancellationToken);
        var current = await GetEngineStatusAsync(_tablet, cancellationToken);
        var warmReconnect = TabletWarmActive(DateTimeOffset.UtcNow) && _displayCleanupDone &&
            current.Healthy && !current.BackendBusy;
        if (_tabletWarmExpiresAt != default && !warmReconnect)
        {
            await StopTabletEngineWithTimingAsync();
            _tabletMode = _tabletRequestedMode = null;
            current = await GetEngineStatusAsync(_tablet, cancellationToken);
        }
        // Rotation reconnects reuse the authenticated lease while Sunshine can
        // still report BUSY for the old video socket. Treat that short overlap
        // as the same session so the dynamic display path can run immediately;
        // a different lease must still be rejected as a concurrent session.
        var reusingLease = _tabletLeaseId is not null &&
            _tabletLeaseId == request.LeaseId && _tabletClientId == request.ClientId;
        if (current.Streaming && !reusingLease)
            throw new InvalidOperationException("副屏正在使用，请先断开当前会话。");
        if (_tabletLeaseId is not null && !reusingLease)
            throw new InvalidOperationException("已有设备正在连接副屏，请取消或等待本次连接结束。");

        var requested = new TabletDisplayMode(request.Width.Value, request.Height.Value,
            request.RefreshRate.Value, request.Orientation.Value);
        // An idempotent prepare reuses the selected fallback too; comparing only
        // the effective mode would restart an unchanged degraded connection.
        // During a rotation Android closes the old video socket before the
        // replacement activity asks for prepare again.  Sunshine can briefly
        // stop answering /serverinfo in that gap even though its process and
        // the virtual display are still alive.  Reusing the exact lease must
        // therefore prefer the hot display update; requiring a fresh healthy
        // probe here turns every rotation into a cold engine restart.
        var healthyEngine = _tabletMode is not null && _tablet.Process?.Running == true &&
            (current.Healthy || reusingLease);
        // A normal exit detached the display even while the encoder stayed warm.
        // Always re-apply and verify the negotiated mode before reusing it.
        var reuse = healthyEngine && _tabletRequestedMode == requested && !warmReconnect;
        // A direction change used to stop Sunshine, detach the virtual display,
        // and start the engine again.  That path can spend more than a minute
        // waiting for the encoder and health endpoint.  The display helper can
        // apply a new negotiated mode in the active console session while the
        // engine is idle, so keep the healthy process and only update its
        // capture profile.  If the helper cannot apply the change, fall back to
        // the old full restart path below.
        var hotReconfigure = healthyEngine && !reuse;
        _tabletWarmExpiresAt = default;
        _tabletLeaseId = request.LeaseId;
        _tabletClientId = request.ClientId;
        _tabletStreamingSeen = false;
        _tabletPreparationExpiresAt = now + TabletPreparationGrace;
        _tabletHeartbeatExpiresAt = now + TabletHeartbeatGrace;
        _displayCleanupDone = false;
        try
        {
            async Task ConfigureAndPersistModeAsync()
            {
                _tablet.Message = "正在准备副屏显示模式";
                var displayWatch = Stopwatch.StartNew();
                var negotiation = await _configureTabletMode(
                    new TabletDisplayRequest(requested.Width, requested.Height, requested.RefreshRate, requested.Orientation), cancellationToken);
                _tabletMode = negotiation.Mode;
                _tabletSupportedModes = negotiation.SupportedModes;
                _tabletRequestedMode = requested;
                await _profiles.ConfigureTabletAsync(_tabletMode.Orientation, request.AutoOrientation, request.DisplayOutput,
                    cancellationToken, _tabletMode.Width, _tabletMode.Height, _tabletMode.RefreshRate);
                AddTiming(timings, "virtual-screen", displayWatch.ElapsedMilliseconds);
                var outputWatch = Stopwatch.StartNew();
                if (!_profiles.HasTabletOutput())
                    await EnsureTabletOutputAsync(cancellationToken);
                AddTiming(timings, "virtual-output", outputWatch.ElapsedMilliseconds);
            }
            if (hotReconfigure)
            {
                Exception? lastHotError = null;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        _tablet.Message = "正在切换副屏显示模式";
                        await ConfigureAndPersistModeAsync();
                        Log(warmReconnect ? "副屏在20秒保留期内重新连接，显示模式已重新配置，复用现有引擎。"
                            : $"副屏复用现有引擎，仅更新显示模式（{requested.Orientation}°）。");
                        lastHotError = null;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastHotError = ex;
                        // A helper timeout already consumed its bounded
                        // 15-second budget. Retrying that same blocked driver
                        // call would turn a quick rotation into 30-45 seconds;
                        // reserve retries for immediate topology races.
                        var timedOut = ex.Message.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
                        if (attempt < 3 && !timedOut)
                        {
                            Log($"副屏显示模式热切换第 {attempt} 次失败，正在短暂重试：{ex.Message}");
                            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                        }
                        else if (timedOut) break;
                    }
                }
                if (lastHotError is not null)
                {
                    // A driver/console transition can reject a hot update.  A
                    // complete restart remains the compatibility path, but only
                    // after the short retries above have all failed.
                    Log("副屏显示模式热切换重试失败，回退到完整重启：" + lastHotError.Message);
                    await StopTabletEngineWithTimingAsync();
                    if (_tablet.Process?.Running == true)
                        throw new InvalidOperationException("副屏引擎仍在退出，请稍后重新连接。");
                    await ConfigureAndPersistModeAsync();
                }
            }
            else if (!reuse)
            {
                await StopTabletEngineWithTimingAsync();
                if (_tablet.Process?.Running == true)
                    throw new InvalidOperationException("副屏引擎仍在退出，请稍后重新连接。");
                await ConfigureAndPersistModeAsync();
            }
            _tablet.Desired = true;
            await SaveDesiredAsync(cancellationToken);
            await TryStartEngineAsync(_tablet, cancellationToken, timings);
            if (_tablet.Process?.Running != true) throw new InvalidOperationException(_tablet.Message);
            // Display configuration and an encoder start may take several
            // seconds. The receiver gets the full grace period after this work.
            _tabletPreparationExpiresAt = DateTimeOffset.UtcNow + TabletPreparationGrace;
            return new TabletPreparationResult(_tabletMode!, timings, _tabletSupportedModes);
        }
        catch
        {
            // API cancellation and helper errors must not leave a display path
            // attached. Cleanup has its own bounded token and is retried later.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try { await EndTabletSessionAsync("副屏准备未完成，正在回收桌面。", cleanup.Token); }
            catch (Exception ex) { Log("副屏准备失败后的清理将继续重试：" + ex.Message); }
            throw;
        }
    }

    private bool TabletLeaseExpired(DateTimeOffset now) => _tabletStreamingSeen
        ? _tabletLeaseId is not null && now >= _tabletHeartbeatExpiresAt
        : _tabletPreparationExpiresAt != default && now >= _tabletPreparationExpiresAt;

    private async Task EnsureTabletOutputAsync(CancellationToken cancellationToken)
    {
        // Sunshine's stable capture identifier comes from its own hardware
        // enumeration. Only initialize the remote engine when this first-run
        // identifier is missing; an existing healthy engine is never restarted.
        var remote = await GetEngineStatusAsync(_remote, cancellationToken);
        if (!remote.Healthy)
        {
            _remote.Desired = true;
            if (_remote.Process?.Running != true) await TryStartEngineAsync(_remote, cancellationToken);
            if (_remote.Process?.Running != true) throw new InvalidOperationException(_remote.Message);
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            var output = DetectTabletOutput();
            if (output is not null && (remote.Healthy || (await GetEngineStatusAsync(_remote, cancellationToken)).Healthy))
            {
                await _profiles.ConfigureTabletAsync(null, null, output, cancellationToken);
                return;
            }
            await Task.Delay(250, cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new InvalidOperationException("未能识别唯一的虚拟副屏，请确认显示驱动已安装且实体屏已打开，再重试连接。");
    }

    private void ValidateTabletLease(RuntimeRequest request)
    {
        if (_tabletLeaseId is null || !string.Equals(_tabletLeaseId, request.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(_tabletClientId, request.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("副屏连接标识已失效，请重新连接。");
    }

    private void PruneReleasedTabletLeases()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _releasedTabletLeases.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            _releasedTabletLeases.Remove(key);
    }

    private void RememberTabletLeaseRelease(RuntimeRequest request)
    {
        // Cancellation uses a separate pipe from preparation. It can arrive
        // first even after the other pipe has read its request. Remember the
        // exact cancelled identity so a late prepare cannot resurrect it.
        // Administrative stop commands do not supply a lease and stay separate.
        if (string.IsNullOrEmpty(request.LeaseId)) return;
        if (!Regex.IsMatch(request.LeaseId, "^[A-Za-z0-9_-]{8,128}$") ||
            request.ClientId is { Length: > 128 } || request.ClientId?.Any(char.IsControl) == true)
            throw new InvalidOperationException("副屏连接标识无效。");
        PruneReleasedTabletLeases();
        _releasedTabletLeases[(request.ClientId, request.LeaseId)] = DateTimeOffset.UtcNow.AddMinutes(5);
        while (_releasedTabletLeases.Count > 256)
            _releasedTabletLeases.Remove(_releasedTabletLeases.MinBy(x => x.Value).Key);
    }

    private bool TabletWarmActive(DateTimeOffset now) => !_tablet.Desired &&
        _tabletWarmExpiresAt > now && _tablet.Process?.Running == true &&
        (_interactive || _tablet.Process.SessionId == SessionLauncher.ActiveSession);

    private async Task EndTabletSessionAsync(string message, CancellationToken cancellationToken, bool allowWarm = false)
    {
        // Once release has been accepted, client cancellation must not abandon
        // the desktop. Bound cleanup independently and retain retry state on failure.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        cancellationToken = cleanup.Token;
        // Only an authenticated normal end of a fully connected, now-idle
        // backend is eligible. Expiry, cancellation, explicit stop and failures
        // continue to use the existing complete shutdown path.
        var keepWarm = false;
        if (allowWarm && _tabletStreamingSeen && _tabletMode is not null && _tabletRequestedMode is not null &&
            _tablet.Process?.Running == true && (_interactive || _tablet.Process.SessionId == SessionLauncher.ActiveSession))
        {
            var status = await GetEngineStatusAsync(_tablet, cancellationToken);
            keepWarm = status.Healthy && !status.BackendBusy;
        }
        if (_tabletLeaseId is not null)
            RememberTabletLeaseRelease(new("tablet-release", LeaseId: _tabletLeaseId, ClientId: _tabletClientId));
        _tablet.Desired = false;
        _tabletStreamingSeen = false;
        _tabletStreamLostAt = default;
        _tabletLeaseId = _tabletClientId = null;
        _tabletPreparationExpiresAt = _tabletHeartbeatExpiresAt = default;
        _tabletWarmExpiresAt = keepWarm ? DateTimeOffset.UtcNow + TabletWarmGrace : default;
        if (!keepWarm) _tabletMode = _tabletRequestedMode = null;
        _displayCleanupDone = false;
        _displayCleanupRetryAt = DateTimeOffset.UtcNow;
        try { await SaveDesiredAsync(cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log("副屏停止状态未能保存；本次连接仍会立即清理：" + ex.Message); }
        finally
        {
            if (!keepWarm) await StopEngineAsync(_tablet, cancellationToken);
            _tablet.Message = message;
            await TryCleanupDisplayAsync(cancellationToken);
            if (keepWarm && _displayCleanupDone && TabletWarmActive(DateTimeOffset.UtcNow))
            {
                _tabletWarmExpiresAt = DateTimeOffset.UtcNow + TabletWarmGrace;
                _tablet.Message = "已停止，可快速重连";
                Log("副屏显示已回收，仅保留空闲引擎20秒供重新连接。");
            }
            else if (keepWarm)
            {
                await StopEngineAsync(_tablet, cancellationToken);
                _tabletMode = _tabletRequestedMode = null;
                await TryCleanupDisplayAsync(cancellationToken);
            }
        }
    }

    private async Task TryCleanupDisplayAsync(CancellationToken cancellationToken)
    {
        if (_tablet.Process?.Running == true)
        {
            var idleWarm = TabletWarmActive(DateTimeOffset.UtcNow);
            if (idleWarm)
            {
                var status = await GetEngineStatusAsync(_tablet, cancellationToken);
                idleWarm = status.Healthy && !status.BackendBusy;
                if (!idleWarm) await StopEngineAsync(_tablet, cancellationToken);
            }
            if (_tablet.Process?.Running == true && !idleWarm)
            {
                _displayCleanupDone = false;
                _displayCleanupRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                return;
            }
        }
        try
        {
            await DisableDisplayAsync(cancellationToken);
            _displayCleanupDone = true;
            _displayCleanupRetryAt = DateTimeOffset.UtcNow.AddSeconds(10);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _displayCleanupDone = false;
            _displayCleanupRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _tablet.Message = "副屏桌面尚未回收，后台正在重试。";
            Log("副屏显示区域未能回收，后台将重试：" + ex.Message);
        }
    }

    private async Task ReconcilePrivacyScreenAsync(CancellationToken cancellationToken)
    {
        if (!await StopExpiredPrivacyStreamAsync(cancellationToken)) return;
        var now = DateTimeOffset.UtcNow;
        var remoteRunning = _remote.Process?.Running == true;
        var authenticatedStream = _remoteSession.IsStreaming(now);
        var shouldShow = PrivacyScreenPolicy.ShouldShow(_privacyScreenEnabled, authenticatedStream, remoteRunning);

        if (_privacyScreen is { } exited && !exited.Running)
        {
            _privacyScreen = null;
            _privacyScreenStop?.Dispose();
            _privacyScreenStop = null;
            try
            {
                if (await HandlePrivacyScreenExitAsync(exited)) return;
            }
            finally { exited.Dispose(); }
            Log("远控隐私黑屏助手意外退出，将在短暂冷却后重试。");
            RecordPrivacyScreenFailure(now, "隐私黑屏助手已退出");
        }

        if (_privacyScreen is { } active && active.Running &&
            PrivacyScreenPolicy.RequiresSessionRestart(_interactive, active.SessionId, SessionLauncher.ActiveSession))
        {
            Log("电脑登录会话已切换，正在迁移远控隐私黑屏助手。");
            await StopPrivacyScreenAsync(cancellationToken);
            _privacyScreenRetryAt = now;
            _privacyScreenMessage = "电脑登录会话已切换，正在重新开启本机隐私黑屏";
        }

        if (!shouldShow)
        {
            await StopPrivacyScreenAsync(cancellationToken);
            _privacyScreenMessage = PrivacyScreenPolicy.Message(_privacyScreenEnabled, false, authenticatedStream, remoteRunning);
            return;
        }
        if (_privacyScreen?.Running == true)
        {
            _privacyScreenFailures = 0;
            _privacyScreenMessage = PrivacyScreenPolicy.Message(true, true, authenticatedStream, remoteRunning);
            return;
        }
        if (!PrivacyScreenPolicy.ShouldRetry(_privacyScreenFailures))
        {
            _privacyScreenMessage = PrivacyScreenPolicy.RetryLimitMessage();
            return;
        }
        if (now < _privacyScreenRetryAt) return;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            _privacyScreenRetryAt = now.AddSeconds(10);
            _privacyScreenMessage = "未找到隐私黑屏助手，等待后台修复";
            return;
        }

        OwnedProcess? helper = null;
        PrivacyScreenLifetime? lifetime = null;
        try
        {
            _privacyScreenMessage = "正在开启本机隐私黑屏";
            lifetime = PrivacyScreenLifetime.Create(_remoteSession.Generation);
            using var owner = Process.GetCurrentProcess();
            var remoteProcess = _remote.Process!.Process;
            helper = SessionLauncher.Start(executable, ["--privacy-screen", lifetime.Name,
                remoteProcess.Id.ToString(), remoteProcess.StartTime.ToFileTimeUtc().ToString(),
                _remoteSession.PrivacyTimeoutMs.ToString(), owner.Id.ToString(), owner.StartTime.ToFileTimeUtc().ToString(),
                _remoteSession.PrivacyDeadlineTick(DateTimeOffset.UtcNow).ToString()], _root, _interactive);
            if (!_interactive && helper.SessionId != SessionLauncher.ActiveSession)
                throw new InvalidOperationException("电脑登录会话已切换，请重新连接远控。");
            var startup = Stopwatch.StartNew();
            while (!lifetime.IsReady && helper.Running && startup.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            if (!helper.Running)
            {
                if (await HandlePrivacyScreenExitAsync(helper)) return;
                throw new InvalidOperationException("隐私黑屏助手未能启动，请确认 Windows 版本支持捕获排除。");
            }
            if (!lifetime.IsReady) throw new InvalidOperationException("隐私黑屏助手启动超时，未显示黑屏。");
            _privacyScreen = helper;
            _privacyScreenStop = lifetime;
            helper = null;
            lifetime = null;
            _privacyScreenFailures = 0;
            _privacyScreenMessage = PrivacyScreenPolicy.Message(true, true, authenticatedStream, remoteRunning);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            RecordPrivacyScreenFailure(DateTimeOffset.UtcNow, "未能开启本机隐私黑屏");
            Log("远控隐私黑屏未能开启：" + ex.Message);
        }
        finally
        {
            if (helper is not null)
            {
                try { if (helper.Running) helper.Process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                helper.Dispose();
            }
            lifetime?.Dispose();
        }
    }

    // An expired control channel alone does not prove the media stream ended.
    // Keep the overlay until the owned engine has actually exited.
    private async Task<bool> StopExpiredPrivacyStreamAsync(CancellationToken cancellationToken)
    {
        if (_privacyScreen?.Running != true || !_remoteSession.PrivacyHeartbeatExpired(DateTimeOffset.UtcNow)) return true;
        var engine = _remote.Process;
        if (engine?.Running == true)
        {
            try
            {
                engine.Process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(PrivacyScreenGuard.EngineExitBudgetMs));
                try { await engine.Process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { Log("隐私会话超时后停止主屏引擎失败：" + ex.Message); }
            if (engine.Running)
            {
                _privacyScreenMessage = "正在安全结束失联的远控，隐私黑屏保持开启";
                return false;
            }
        }
        if (engine is not null)
        {
            _remote.Process = null;
            _remote.ReadinessWatch = null;
            engine.Dispose();
            _remote.Message = "隐私会话已结束，正在恢复待机";
        }
        _remoteSession.Reset();
        Log("隐私会话心跳超时，主屏引擎已停止，准备恢复本机屏幕。");
        return true;
    }

    private async Task<bool> HandlePrivacyScreenExitAsync(OwnedProcess helper)
    {
        var exitCodeAvailable = helper.TryGetExitCode(out var exitCode);
        if (exitCodeAvailable && exitCode is not (PrivacyScreenWindow.EmergencyRecovery or PrivacyScreenWindow.HotkeyUnavailable or PrivacyScreenGuard.Recovered))
            return false;
        _privacyScreenEnabled = false;
        _privacyScreenFailures = 0;
        _privacyScreenRetryAt = default;
        _privacyScreenMessage = !exitCodeAvailable
            ? "隐私黑屏助手已退出但无法确认原因，为避免再次黑屏，远控隐私保护已关闭"
            : exitCode == PrivacyScreenGuard.Recovered
            ? "隐私连接失联或登录会话已切换，已安全结束远控并恢复本机屏幕；隐私保护已关闭"
            : exitCode == PrivacyScreenWindow.EmergencyRecovery
            ? "已通过 Ctrl+Alt+F12 恢复本机屏幕，远控隐私保护已关闭"
            : "无法注册恢复热键 Ctrl+Alt+F12（可能已被占用），未开启隐私黑屏";
        if (exitCode == PrivacyScreenGuard.Recovered) _remoteSession.Reset();
        try { await SaveDesiredAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _privacyScreenMessage += "；设置保存失败，请检查后台权限";
            Log("隐私保护关闭后保存设置失败：" + ex.Message);
        }
        Log(_privacyScreenMessage);
        return true;
    }

    private async Task StopPrivacyScreenAsync(CancellationToken cancellationToken)
    {
        var helper = _privacyScreen;
        var stop = _privacyScreenStop;
        _privacyScreen = null;
        _privacyScreenStop = null;
        if (helper is null) { stop?.Dispose(); return; }
        try
        {
            if (helper.Running)
            {
                stop?.RequestStop();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PrivacyScreenGracefulStop);
                try { await helper.Process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                if (helper.Running)
                {
                    try { helper.Process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    using var forced = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    forced.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await helper.Process.WaitForExitAsync(forced.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }
            }
        }
        finally { stop?.Dispose(); helper.Dispose(); }
    }

    private void RecordPrivacyScreenFailure(DateTimeOffset now, string prefix)
    {
        _privacyScreenFailures = Math.Min(_privacyScreenFailures + 1, PrivacyScreenPolicy.MaxConsecutiveFailures);
        _privacyScreenRetryAt = now.AddSeconds(5);
        _privacyScreenMessage = PrivacyScreenPolicy.ShouldRetry(_privacyScreenFailures)
            ? $"{prefix}，正在重试（{_privacyScreenFailures}/{PrivacyScreenPolicy.MaxConsecutiveFailures}）"
            : PrivacyScreenPolicy.RetryLimitMessage();
    }

    private async Task EnsureControlApiAsync(CancellationToken cancellationToken)
    {
        if (_controlApi?.IsRunning == true || DateTimeOffset.UtcNow < _controlApiRetryAt ||
            !File.Exists(ControlApiIdentity.PathFor(ControlIdentityDirectory))) return;
        if (_controlApi is not null) { await _controlApi.DisposeAsync(); _controlApi = null; }
        ControlApiIdentity? identity = null;
        try
        {
            identity = ControlApiIdentity.Read(ControlIdentityDirectory, DataProtectionScope.LocalMachine);
            var server = new ControlApiServer(new RuntimeClient(), ConfigureDisplayAsync, identity,
                _interactive ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.MachineKeySet,
                token => DisplayModeProbe.ReadAsync(_root, _interactive, token), _files, RequestClipboardAsync,
                addresses => { try { var transport = _transportSettings.Load(); return transport.Transport.Addresses(addresses, transport); } catch { return []; } },
                discoveryDeviceId: _deviceId, maintenance: _maintenance);
            _controlApi = server;
            await server.StartAsync(cancellationToken);
            _controlApiError = null;
            Log("后台设备绑定接口已启动。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never log migrated private keys, tokens, invitations, or caller data.
            _controlApiError = "后台设备接口未能启动，请检查 49199 端口或修复安装。已有设备绑定仍保留。";
            _controlApiRetryAt = DateTimeOffset.UtcNow.AddSeconds(15);
            Log("后台设备绑定接口启动失败，将自动重试。");
        }
        finally { identity?.Clear(); }
    }

    private static bool ClipboardAgentIdValid(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

    private async Task<ClipboardPayload?> RequestClipboardAsync(string mode, string leaseId, string clientId,
        string action, ClipboardPayload? payload, CancellationToken token)
    {
        using var operation = _maintenance.Enter();
        return await _clipboard.RequestAsync(mode, leaseId, clientId, action, payload, token, RunClipboardAgentAsync);
    }

    private async Task RunClipboardAgentAsync(ClipboardJob job, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Path.Combine(_root, "我控制.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("电脑剪贴板组件缺失，请修复安装。");
        try
        {
            using var helper = SessionLauncher.Start(executable, ["--clipboard-job", job.Id], _root, _interactive,
                _ownerSid?.Value ?? throw new InvalidOperationException("授权的 Windows 用户不可用。"));
            // The job object bounds helper lifetime to this request and service.
            // Only the request ID is on the command line; content stays in the authenticated pipe.
            await helper.Process.WaitForExitAsync(token);
            if (helper.ExitCode != 0) throw new InvalidOperationException("电脑剪贴板暂时不可用，请解锁桌面后重试。");
        }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("无法访问已授权用户的剪贴板，请用安装时的 Windows 账号登录并解锁桌面。"); }
    }

    private async Task ConfigureDisplayAsync(int orientation, bool verify, CancellationToken cancellationToken)
    {
        if (orientation is not (0 or 90 or 180 or 270)) throw new InvalidOperationException("副屏方向无效。");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("未能找到后台显示助手。");
        using var helper = SessionLauncher.Start(executable,
            ["--display-helper", orientation.ToString(System.Globalization.CultureInfo.InvariantCulture), verify ? "verify" : "prepare"],
            _root, _interactive);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try { await helper.Process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (helper.Running) helper.Process.Kill(entireProcessTree: true);
            throw;
        }
        if (helper.ExitCode != 0)
            throw new InvalidOperationException("副屏显示助手未能完成配置，请确认电脑已登录、实体屏已打开，且虚拟屏处于扩展布局。");
        if (!_interactive && helper.SessionId != SessionLauncher.ActiveSession)
            throw new InvalidOperationException("电脑登录会话已切换，请重新准备副屏连接。");
    }

    private async Task DisableDisplayAsync(CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("未能找到后台显示助手。\n");
        using var helper = SessionLauncher.Start(executable, ["--display-disable"], _root, _interactive);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try { await helper.Process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (helper.Running) helper.Process.Kill(entireProcessTree: true);
            throw;
        }
        if (helper.ExitCode != 0)
            throw new InvalidOperationException("副屏显示助手未能停用虚拟显示器，请确认电脑已登录且实体屏处于打开状态。");
        if (!_interactive && helper.SessionId != SessionLauncher.ActiveSession)
            throw new InvalidOperationException("电脑登录会话已切换，请重新停用副屏连接。");
    }

    private async Task TryStartEngineAsync(Engine engine, CancellationToken cancellationToken,
        IDictionary<string, long>? timings = null)
    {
        if (engine.Process?.Running == true) return;
        ObserveExitedEngine(engine);
        engine.Process?.Dispose();
        engine.Process = null;
        engine.ReadinessWatch = null;
        try
        {
            engine.Message = "正在初始化内置串流配置";
            Log($"{engine.Profile} 引擎正在初始化。");
            await _profiles.EnsureAsync(engine.Profile, cancellationToken);
            if (engine.Profile == "tablet" && !_profiles.HasTabletOutput())
            {
                var output = DetectTabletOutput();
                if (output is null) throw new InvalidOperationException("尚未检测到唯一的虚拟副屏。请确认显示驱动已安装，并先启动远程主屏完成硬件检测。");
                await _profiles.ConfigureTabletAsync(null, null, output, cancellationToken);
            }
            var executable = _profiles.ExecutableFor(engine.Profile);
            var processWatch = Stopwatch.StartNew();
            engine.Process = SessionLauncher.Start(executable, new[] { _profiles.ConfigFor(engine.Profile) },
                Path.GetDirectoryName(executable)!, _interactive);
            if (timings is not null) timings["engine-start"] = processWatch.ElapsedMilliseconds;
            engine.ProcessStartedAt = DateTimeOffset.UtcNow;
            engine.ReadinessWatch = Stopwatch.StartNew();
            engine.Message = "串流引擎正在准备";
            Log($"{engine.Profile} 引擎已启动，进程 {engine.Process.Process.Id}。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            engine.Failures++;
            engine.RetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(engine.Failures, 5))));
            engine.Message = ex.Message;
            Log($"{engine.Profile} 引擎启动失败：{ex.Message}");
        }
    }

    private void ObserveExitedEngine(Engine engine)
    {
        var owned = engine.Process;
        if (owned is null || owned.Running) return;
        engine.Process = null;
        engine.ReadinessWatch = null;
        var exitDescription = "无法读取";
        var processId = "未知";
        try
        {
            processId = owned.Process.Id.ToString();
            var exitCode = owned.ExitCode;
            exitDescription = $"{exitCode} / 0x{unchecked((uint)exitCode):X8}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { exitDescription = "无法读取：" + ex.Message; }
        finally { owned.Dispose(); }
        engine.Failures++;
        var retrySeconds = Math.Min(30, Math.Pow(2, Math.Min(engine.Failures, 5)));
        engine.RetryAt = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
        engine.Message = $"串流引擎意外退出（{exitDescription}），{retrySeconds:0} 秒后重试";
        Log($"{engine.Profile} 引擎意外退出，进程 {processId}，退出码 {exitDescription}，{retrySeconds:0} 秒后重试。");
    }

    private async Task StopEngineAsync(Engine engine, CancellationToken cancellationToken)
    {
        if (engine == _tablet) _tabletWarmExpiresAt = default;
        var owned = engine.Process;
        var readinessWatch = engine.ReadinessWatch;
        engine.Process = null;
        engine.ReadinessWatch = null;
        if (owned is null) return;
        try
        {
            if (owned.Running)
            {
                var executable = Environment.ProcessPath;
                if (File.Exists(executable))
                {
                    using var signal = SessionLauncher.Start(executable, new[] { "--signal", owned.Process.Id.ToString() }, _root, _interactive);
                    using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await signal.Process.WaitForExitAsync(signalTimeout.Token); } catch (OperationCanceledException) { }
                    Log($"{engine.Profile} 引擎停止信号，进程 {owned.Process.Id}，" +
                        (signal.Running ? "信号助手超时。" : signal.ExitCode == 0 ? "Ctrl+C 已发送。" : "未能发送 Ctrl+C。"));
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await owned.Process.WaitForExitAsync(grace.Token); } catch (OperationCanceledException) { }
                }
                if (owned.Running)
                {
                    Log($"{engine.Profile} 引擎未在宽限期退出，强制结束进程 {owned.Process.Id}。");
                    owned.Process.Kill(entireProcessTree: true);
                    // Sunshine can restore its previous display state while
                    // exiting. Wait until it is actually gone before detaching
                    // MTT, otherwise that late restoration can attach it again.
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await owned.Process.WaitForExitAsync(killed.Token); }
                    catch (OperationCanceledException) { Log($"{engine.Profile} 引擎结束仍在等待，副屏清理将由监督任务继续检查。"); }
                }
                else Log($"{engine.Profile} 引擎已优雅退出，进程 {owned.Process.Id}。");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Log($"{engine.Profile} 引擎停止提示：{ex.Message}"); }
        finally
        {
            if (owned.Running)
            {
                // Retain ownership so a failed/slow termination remains visible
                // and can be retried. Do not declare a cleaned display while an
                // old engine could still restore its topology on exit.
                engine.Process = owned;
                engine.ReadinessWatch = readinessWatch;
                engine.Message = "正在停止串流引擎";
            }
            else { owned.Dispose(); engine.Message = "已停止"; }
        }
    }

    private string? DetectTabletOutput()
    {
        var path = Path.Combine(_data, "logs", "remote.log");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024) stream.Seek(-4 * 1024 * 1024, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var matches = Regex.Matches(text, "\\\"device_id\\\"\\s*:\\s*\\\"(?<id>\\{[0-9a-fA-F-]{36}\\})\\\"(?<body>.*?)(?=\\\"device_id\\\"|\\z)", RegexOptions.Singleline);
        var candidates = matches.Cast<Match>().Where(m => Regex.IsMatch(m.Groups["body"].Value, "\\\"manufacturer_id\\\"\\s*:\\s*\\\"MTT\\\"") &&
            Regex.IsMatch(m.Groups["body"].Value, "\\\"product_code\\\"\\s*:\\s*\\\"1337\\\""))
            .Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private async Task<RuntimeStatus> SnapshotAsync(CancellationToken cancellationToken)
    {
        var remote = await GetEngineStatusAsync(_remote, cancellationToken);
        var tablet = await GetEngineStatusAsync(_tablet, cancellationToken);
        var privacyActive = _privacyScreen?.Running == true;
        return new(_deviceId, Environment.MachineName, remote, tablet, Volatile.Read(ref _networkSnapshot),
            ProductVersion.Name, _data, _startedAt, _privacyScreenEnabled, privacyActive,
            privacyActive ? PrivacyScreenPolicy.Message(true, true, _remoteSession.IsStreaming(DateTimeOffset.UtcNow), remote.Running)
                : _privacyScreenMessage);
    }

    private async Task<EngineStatus> GetEngineStatusAsync(Engine engine, CancellationToken cancellationToken)
    {
        var owned = engine.Process;
        var running = owned?.Running == true;
        var healthy = false;
        var streaming = false;
        var busy = false;
        if (running)
        {
            try
            {
                var xml = await _probe.GetStringAsync($"http://127.0.0.1:{ProfileStore.Port(engine.Profile)}/serverinfo", cancellationToken);
                var document = XDocument.Parse(xml);
                healthy = document.Root?.Attribute("status_code")?.Value == "200";
                var readinessWatch = engine.ReadinessWatch;
                if (healthy && readinessWatch is not null && ReferenceEquals(owned, engine.Process) &&
                    ReferenceEquals(Interlocked.CompareExchange(ref engine.ReadinessWatch, null, readinessWatch), readinessWatch))
                    Log($"{engine.Profile} 引擎首次检测到就绪，进程 {owned!.Process.Id}，启动后 {readinessWatch.ElapsedMilliseconds} 毫秒（检测时间）。");
                busy = document.Descendants("state").Any(x => x.Value.Contains("BUSY", StringComparison.OrdinalIgnoreCase));
                var now = DateTimeOffset.UtcNow;
                streaming = healthy && busy && (engine == _remote ? _remoteSession.IsStreaming(now)
                    : _tabletLeaseId is not null && now < _tabletHeartbeatExpiresAt);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { }
        }
        var cleanupPending = engine == _tablet && !engine.Desired && !_displayCleanupDone;
        return new(engine.Profile, running, engine.Desired, running ? engine.Process!.Process.Id : null, ProfileStore.Port(engine.Profile),
            healthy, cleanupPending ? "副屏桌面尚未回收，正在重试" : TabletWarmActive(DateTimeOffset.UtcNow) && engine == _tablet
                ? "已停止，可快速重连" : healthy ? streaming ? "正在串流" : "已就绪，等待设备连接" : engine.Message, streaming,
            SessionVerified: true, BackendBusy: busy, DesktopCleanupPending: cleanupPending);
    }

    private async Task SaveDesiredAsync(CancellationToken cancellationToken)
    {
        await _desiredGate.WaitAsync(cancellationToken);
        try
        {
            var temporary = Path.Combine(_data, "desired.json.new");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new DesiredState(_remote.Desired, _tablet.Desired,
                _networkDesired, _privacyScreenEnabled)), cancellationToken);
            File.Move(temporary, Path.Combine(_data, "desired.json"), true);
        }
        finally { _desiredGate.Release(); }
    }

    private void Log(string message)
    {
        try
        {
            var path = Path.Combine(_data, "logs", "service.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch (IOException) { }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) return text.ToString();
            var newline = Array.IndexOf(buffer, '\n', 0, count);
            var length = newline >= 0 ? newline : count;
            if (text.Length + length > limit) throw new IOException("操作请求过长。");
            text.Append(buffer, 0, length);
            if (newline >= 0) return text.ToString().TrimEnd('\r');
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopPrivacyScreenAsync(CancellationToken.None);
        _networkClient.Dispose();
        _probe.Dispose();
        _gate.Dispose();
        _networkGate.Dispose();
        _desiredGate.Dispose();
    }
}
