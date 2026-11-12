using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using StreamHub.Host.Networking;

namespace StreamHub.Host.Runtime;

/// <summary>
/// A capability granted by an invitation from the signed-in owner. TLS is pinned by
/// the client; the invitation is never logged and contains no upstream credentials.
/// </summary>
public sealed class ControlApiServer(RuntimeClient runtime, Func<int, bool, CancellationToken, Task> configureDisplay,
    ControlApiIdentity identity, X509KeyStorageFlags keyStorage,
    Func<CancellationToken, Task<HostDisplayMode>>? readHostMode = null,
    FileTransferVault? files = null, Func<string, string, string, string, ClipboardPayload?, CancellationToken, Task<ClipboardPayload?>>? clipboard = null,
    Func<string[], string[]>? advertisedAddresses = null, string? discoveryDeviceId = null,
    MaintenanceWindow? maintenance = null) : IAsyncDisposable
{
    public const int Port = 49199;
    private const int MaximumBodyBytes = 8192;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _rateGate = new();
    private readonly Dictionary<string, (DateTimeOffset Since, int Count)> _rates = new(), _anonymousRates = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CaptureReadinessPolicy _captureReadiness = new();
    private readonly TabletRequestRegistry _receivers = new();
    private readonly FileTransferApi? _fileApi = files is null ? null : new(files);
    private readonly object _jobGate = new();
    private readonly Dictionary<string, PrepareJob> _jobs = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private WebApplication? _server;
    private X509Certificate2? _certificate;
    private byte[]? _token;
    private string? _invitation;
    private LocalBindingRegistry? _localBinding;
    private LanDiscovery? _discovery;
    public LanDiscoveryStatus? DiscoveryStatus => _discovery?.Status;
    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    private sealed record PrepareRequest(
        string Mode,
        int Orientation = 0,
        int? Width = null,
        int? Height = null,
        int? RefreshRate = null,
        string[]? Codecs = null, string? ClientId = null, string? LeaseId = null, string? RequestId = null, int? DisplayIndex = null,
        bool StreamSizeOverride = false);
    private sealed record ReceiverPoll(string ClientId, string Name, int Width, int Height, int RefreshRate, int[]? Orientations = null);
    private sealed record RequestAnswer(string ClientId, string Action, string? Message = null);
    private sealed record SessionRequest(string LeaseId, string Mode = "tablet", string? ClientId = null, string? RequestId = null,
        bool StreamReady = false, bool FastPrivacyHeartbeat = false, bool BoundedPrivacyHeartbeat = false, bool LockOnExit = false, string? ExitReason = null);
    private sealed record DesktopRequest(string LeaseId,string ClientId,string Action,bool? Enabled=null,
        string? Password=null,bool Submit=false);
    private sealed record PrivacyRequest(string LeaseId, string ClientId, string Mode = "remote", bool? Enabled = null);
    private sealed class PrepareJob(string id, PrepareRequest request, CancellationToken stopping)
    {
        public string Id = id;
        public PrepareRequest Request = request;
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public string State = "queued", Stage = "queued", Message = "等待电脑准备";
        public DateTimeOffset StageStarted = DateTimeOffset.UtcNow;
        public Dictionary<string, long> StageDurationsMs = new();
        public object? Result;
        public Task? Work;
    }
    private sealed record PairRequest(string Mode, string Pin, string? Name = null);
    private sealed record BindingRequest(string Code, string ClientId, string Name, string Nonce);
    private sealed record BindingPoll(string RequestId, string Nonce);
    private sealed class ApiError(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public string GetInvitation() => IsRunning && _invitation is not null ? _invitation :
        throw new InvalidOperationException(LastError ?? "设备绑定接口尚未启动。");

    public string BindingCommand(RuntimeRequest request)
    {
        var registry = _localBinding ?? throw new InvalidOperationException("设备绑定接口尚未启动。");
        var snapshot = request.Command switch
        {
            "binding-start" => registry.Start(),
            "binding-cancel" => registry.Cancel(),
            "binding-approve" => registry.Decide(request.RequestId ?? "", true),
            "binding-reject" => registry.Decide(request.RequestId ?? "", false),
            "binding-status" => registry.Snapshot(),
            _ => throw new InvalidOperationException("设备确认操作无效。")
        };
        return JsonSerializer.Serialize(snapshot, RuntimeProtocol.JsonOptions);
    }

    // Called by the local authenticated IPC while its runtime gate is held.
    // Only memory is accessed here; any release is scheduled after returning.
    public string ReceiverCommand(string command, RuntimeRequest request)
    {
        if (command == "tablet-request")
        {
            ValidateId(request.ClientId, "接收设备");
            var angle = request.Orientation ?? 0;
            if (angle is not (0 or 90 or 180 or 270)) throw new InvalidOperationException("请选择正确的副屏方向。");
            var link = request.Profile ?? "auto";
            if (link is not ("auto" or "lan" or "usb")) throw new InvalidOperationException("连接方式无效。");
            _receivers.Create(request.ClientId!, angle, link);
        }
        else if (command == "tablet-request-cancel")
        {
            ValidateId(request.RequestId, "副屏请求");
            _receivers.Cancel(request.RequestId!);
            CancelRequestJobs(request.RequestId!);
        }
        else if (command is not ("receiver-list" or "tablet-request-status")) throw new InvalidOperationException("未知的副屏请求操作。");
        return JsonSerializer.Serialize(_receivers.Snapshot(command == "tablet-request" ? null : request.RequestId), RuntimeProtocol.JsonOptions);
    }

    private static void ValidateId(string? id, string label)
    {
        if (id is not { Length: >= 8 and <= 128 } || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new InvalidOperationException(label + "标识无效。");
    }

    private void CancelRequestJobs(string requestId)
    {
        PrepareJob[] jobs;
        lock (_jobGate) jobs = _jobs.Values.Where(x => x.Request.RequestId == requestId).ToArray();
        foreach (var job in jobs)
        {
            job.Cancellation.Cancel();
            // A succeeded job already handed its lease to the player.
            if (job.State == "succeeded" && job.Request.Mode == "tablet")
                _ = ReleaseAsync(job.Request);
        }
        // Job history is short-lived; a connected session can last all day.
        // The registry retains its lease independently of prepare-job history.
        if (_receivers.LeaseFor(requestId) is { } lease && !jobs.Any(x => x.State == "succeeded"))
            _ = ReleaseAsync(new("tablet", ClientId: lease.ClientId, LeaseId: lease.LeaseId));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _ = configureDisplay; // Keep the runtime constructor compatible with older callers.
        if (IsRunning) return;
        try
        {
            LoadIdentity(identity, keyStorage);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [], ApplicationName = typeof(ControlApiServer).Assembly.FullName
            });
            // Kestrel request logging can contain device addresses. No token, request
            // body, invitation, authorization header or remote metadata is logged.
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = ClipboardPolicy.MaximumEnvelopeBytes;
                options.Limits.MaxConcurrentConnections = 16;
                options.Limits.MaxConcurrentUpgradedConnections = 0;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(8);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                options.ListenAnyIP(Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.UseHttps(_certificate!, https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
                });
            });
            _server = builder.Build();
            _server.Run(HandleAsync);
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
            IsRunning = true;
            LastError = null;
            var pin = Convert.ToHexString(SHA256.HashData(_certificate!.RawData));
            _localBinding = new(pin);
            if (discoveryDeviceId is not null)
            {
                _discovery = new(discoveryDeviceId, Environment.MachineName, pin);
                _discovery.Start();
            }
        }
        catch
        {
            LastError = "设备绑定接口启动失败，请确认 49199 端口未被占用，或重新打开我控制。";
            if (_server is not null) { await _server.DisposeAsync().ConfigureAwait(false); _server = null; }
            throw new InvalidOperationException(LastError);
        }
    }

    private void LoadIdentity(ControlApiIdentity identity, X509KeyStorageFlags keyStorage)
    {
        // Schannel requires a persisted private key association. The service uses
        // MachineKeySet; the TLS bytes and pinned fingerprint remain unchanged.
        _certificate = new X509Certificate2(identity.Pfx, (string?)null, keyStorage | X509KeyStorageFlags.PersistKeySet);
        if (!_certificate.HasPrivateKey || _certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new CryptographicException("设备绑定证书已失效。");
        _token = identity.Token.ToArray();
        var invitation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1, port = Port, pin = Convert.ToHexString(SHA256.HashData(_certificate.RawData)), token = Convert.ToHexString(_token)
        });
        try { _invitation = Convert.ToBase64String(invitation).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(invitation); }
    }

    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            bool fileRequest = context.Request.Path.StartsWithSegments("/v1/files");
            bool authorized = Authorized(context.Request.Headers.Authorization.ToString());
            if (!AllowRequest(context.Connection.RemoteIpAddress, fileRequest, fileRequest && IsFileDataRequest(context.Request), authorized))
                throw new ApiError(429, "操作过于频繁，请稍后重试。");
            if (context.Request.Path.StartsWithSegments("/v1/binding"))
            {
                var address = context.Connection.RemoteIpAddress;
                if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
                if (address is null || !LanDiscoveryPacket.IsLocalAddress(address) || context.Request.QueryString.HasValue || _localBinding is null)
                    throw new ApiError(403, "短码绑定仅用于同一局域网，请使用电脑端设备绑定码。");
                if (context.Request.Method == "GET" && context.Request.Path == "/v1/binding/identity")
                {
                    // The address fallback bootstraps only public identity, never a credential.
                    // It is available only while the owner has explicitly opened a short-code window.
                    if (_localBinding.Snapshot().Code is null || discoveryDeviceId is null)
                        throw new ApiError(409, "请先在电脑端生成六位短码，再通过地址绑定。");
                    await WriteAsync(context, 200, new { success = true, result = new { v = 1,
                        id = discoveryDeviceId, name = Environment.MachineName, port = Port,
                        pin = Convert.ToHexString(SHA256.HashData(_certificate!.RawData)) } }, lifetime.Token).ConfigureAwait(false);
                    return;
                }
                if (context.Request.Method != "POST") throw new ApiError(404, "找不到此设备确认接口。");
                LocalBindingResult result;
                if (context.Request.Path == "/v1/binding/request")
                {
                    var request = await ReadJsonAsync<BindingRequest>(context, lifetime.Token).ConfigureAwait(false);
                    result = _localBinding.Begin(request.Code, request.ClientId, request.Name, request.Nonce);
                }
                else if (context.Request.Path == "/v1/binding/poll")
                {
                    var request = await ReadJsonAsync<BindingPoll>(context, lifetime.Token).ConfigureAwait(false);
                    result = _localBinding.Poll(request.RequestId, request.Nonce, GetInvitation);
                }
                else throw new ApiError(404, "找不到此设备确认接口。");
                await WriteAsync(context, 200, new { success = true, result }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (!authorized) throw new ApiError(401, "此设备尚未绑定，请从电脑端导入邀请码。");
            if (context.Request.QueryString.HasValue) throw new ApiError(400, "请求参数必须放在 JSON 正文中。");
            if (fileRequest)
            {
                using var operation = maintenance?.Enter();
                if (_fileApi is null) throw new ApiError(503, "文件功能暂不可用，请在电脑端检查。");
                await _fileApi.HandleAsync(context, lifetime.Token, _stopping.Token).ConfigureAwait(false); return;
            }
            if (context.Request.Method == "GET" && context.Request.Path == "/v1/info")
            {
                var status = await GetStatusAsync(lifetime.Token).ConfigureAwait(false);
                await WriteAsync(context, 200, new
                {
                    success = true, v = 1, deviceId = status.DeviceId, name = status.DeviceName,
                    addresses = advertisedAddresses is null ? LocalAddresses() : advertisedAddresses(LocalAddresses()),
                    ports = new { remote = 48989, tablet = 49089, control = Port },
                    capabilities = new { remote = true, tablet = true, pair = true, orientations = new[] { 0, 90, 180, 270 }, automaticOrientationDuringStream = true, integratedVpn = true,
                        prepareJobs = true, receiverRequests = true, dynamicDisplay = true, sessionLease = true, liveSessionState = true, fastPrivacyHeartbeat = true, boundedPrivacyHeartbeat = true,
                        fileTransfer = _fileApi is not null, fileBrowse = _fileApi is not null, fileRoots = _fileApi is not null, fileRecycle = _fileApi is not null, remoteDesktopLock = true,
                        remotePrivacy = true, streamResolution = true, desktopInputState = true, loginPasswordFill = true },
                    remote = new { status.Remote.Running, status.Remote.Healthy, status.Remote.Streaming, status.Remote.BackendBusy },
                    tablet = new { status.Tablet.Running, status.Tablet.Healthy, status.Tablet.Streaming, status.Tablet.BackendBusy,
                        status.Tablet.DesktopCleanupPending }
                }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            var path = context.Request.Path.Value ?? "";
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (context.Request.Method == "GET" && segments is ["v1", "jobs", var jobId])
            {
                var job = FindJob(jobId);
                object snapshot;
                lock (_jobGate)
                {
                    var now = DateTimeOffset.UtcNow;
                    var durations = new Dictionary<string, long>(job.StageDurationsMs)
                    { [job.Stage] = (long)(now - job.StageStarted).TotalMilliseconds };
                    snapshot = new { success = true, jobId = job.Id, state = job.State,
                        stage = job.Stage, message = job.Message,
                        elapsedMs = (long)(now - job.Created).TotalMilliseconds,
                        stageElapsedMs = (long)(now - job.StageStarted).TotalMilliseconds,
                        stageDurationsMs = durations, result = job.Result };
                }
                await WriteAsync(context, 200, snapshot, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method != "POST") throw new ApiError(404, "找不到此设备接口。");
            if(path=="/v1/session/desktop")
            {
                var request=await ReadJsonAsync<DesktopRequest>(context,lifetime.Token);
                ValidateId(request.LeaseId,"会话");ValidateId(request.ClientId,"接收设备");
                if(request.Action is not ("status" or "lock" or "configure" or "login-fill"))throw new ApiError(400,"桌面操作无效。");
                if(request.Action=="login-fill")DesktopInputPolicy.ValidatePassword(request.Password);
                else if(request.Password is not null || request.Submit)throw new ApiError(400,"此桌面操作不接受密码。");
                var response=await runtime.SendAsync(new("remote-desktop","remote",LeaseId:request.LeaseId,
                    ClientId:request.ClientId,ClipboardAction:request.Action,LockOnExit:request.Enabled,
                    LoginSecret:request.Password,LoginSubmit:request.Submit),lifetime.Token);
                if(!response.Success)throw new ApiError(409,response.Message);
                object? state=response.PayloadJson is null?null:JsonSerializer.Deserialize<JsonElement>(response.PayloadJson);
                await WriteAsync(context,200,new {success=true,message=response.Message,state},lifetime.Token);return;
            }
            if (path == "/v1/session/privacy")
            {
                var request = await ReadJsonAsync<PrivacyRequest>(context, lifetime.Token).ConfigureAwait(false);
                ValidateId(request.LeaseId, "会话"); ValidateId(request.ClientId, "接收设备");
                if (request.Mode != "remote") throw new ApiError(400, "隐私保护仅适用于主屏远控。");
                var response = await runtime.SendAsync(new("remote-privacy", "remote", LeaseId: request.LeaseId,
                    ClientId: request.ClientId, PrivacyEnabled: request.Enabled), lifetime.Token).ConfigureAwait(false);
                if (!response.Success) throw new ApiError(409, response.Message);
                await WriteAsync(context, 200, new { success = true,
                    enabled = response.Status?.PrivacyScreenEnabled == true,
                    active = response.Status?.PrivacyScreenActive == true, message = response.Message }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (path == "/v1/clipboard")
            {
                if (clipboard is null) throw new ApiError(503, "电脑剪贴板暂不可用。");
                var api = new ClipboardApi(async (mode, leaseId, clientId, token) =>
                {
                    var authorized = await runtime.SendAsync(new("clipboard-authorize", mode, LeaseId: leaseId, ClientId: clientId), token).ConfigureAwait(false);
                    if (!authorized.Success) throw new InvalidOperationException(authorized.Message);
                }, clipboard);
                await api.HandleAsync(context, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (path == "/v1/prepare-jobs")
            {
                var request = Normalize(await ReadJsonAsync<PrepareRequest>(context, lifetime.Token).ConfigureAwait(false));
                PrepareJob job;
                lock (_jobGate)
                {
                    foreach (var old in _jobs.Values.Where(x => x.State is not ("queued" or "running") && DateTimeOffset.UtcNow - x.Created > TimeSpan.FromMinutes(10)).ToArray())
                    { _jobs.Remove(old.Id); old.Cancellation.Dispose(); }
                    if (_jobs.Count >= 64 || _jobs.Values.Count(x => x.State is "queued" or "running") >= 4)
                        throw new ApiError(409, "连接准备已排队，请稍后重试。");
                    if (_jobs.Values.Any(x => x.Request.LeaseId == request.LeaseId && x.State is "queued" or "running"))
                        throw new ApiError(409, "此会话已在准备中，请等待或取消。");
                    if (request.Mode == "tablet") _receivers.BindLease(request.RequestId, request.ClientId, request.LeaseId!);
                    job = new(Guid.NewGuid().ToString("N"), request, _stopping.Token);
                    _jobs.Add(job.Id, job);
                    job.Work = Task.Run(() => RunJobAsync(job));
                }
                await WriteAsync(context, 200, new { success = true, jobId = job.Id }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (segments is ["v1", "jobs", var cancelId, "cancel"])
            {
                var job = FindJob(cancelId);
                job.Cancellation.Cancel();
                // Release unconditionally: cancellation may race the final
                // running->succeeded transition. Runtime matches the exact lease.
                _ = ReleaseAsync(job.Request);
                await WriteAsync(context, 200, new { success = true, message = "已取消，正在回收本次连接" }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (path == "/v1/receiver/poll")
            {
                var poll = await ReadJsonAsync<ReceiverPoll>(context, lifetime.Token).ConfigureAwait(false);
                ValidateId(poll.ClientId, "接收设备");
                if (string.IsNullOrWhiteSpace(poll.Name) || poll.Name.Length > 80 || poll.Name.Any(char.IsControl)) throw new ApiError(400, "接收设备名称无效。");
                ValidateDimensions(poll.Width, poll.Height, poll.RefreshRate);
                var angles = poll.Orientations ?? [0, 90, 180, 270];
                if (angles.Length is < 1 or > 4 || angles.Any(x => x is not (0 or 90 or 180 or 270))) throw new ApiError(400, "接收设备方向无效。");
                var request = _receivers.Poll(new(poll.ClientId, poll.Name, poll.Width, poll.Height, poll.RefreshRate, angles, DateTimeOffset.UtcNow));
                await WriteAsync(context, 200, new { success = true, request }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (segments is ["v1", "requests", var requestId, "respond"])
            {
                var answer = await ReadJsonAsync<RequestAnswer>(context, lifetime.Token).ConfigureAwait(false);
                var request = _receivers.Respond(requestId, answer.ClientId, answer.Action, answer.Message);
                if (answer.Action is "reject" or "failed") CancelRequestJobs(requestId);
                await WriteAsync(context, 200, new { success = true, request }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (path is "/v1/session/heartbeat" or "/v1/session/end")
            {
                var session = await ReadJsonAsync<SessionRequest>(context, lifetime.Token).ConfigureAwait(false);
                ValidateMode(session.Mode); ValidateId(session.LeaseId, "会话");
                if (session.Mode == "tablet")
                {
                    ValidateId(session.ClientId, "接收设备");
                    var ended = path.EndsWith("/end", StringComparison.Ordinal);
                    var response = await runtime.SendAsync(new(ended ? "tablet-release" : "tablet-heartbeat", "tablet",
                        LeaseId: session.LeaseId, ClientId: session.ClientId), lifetime.Token).ConfigureAwait(false);
                    if (!response.Success) throw new ApiError(409, response.Message);
                    _receivers.Session(session.ClientId!, session.LeaseId, ended);
                }
                else
                {
                    ValidateId(session.ClientId, "接收设备");
                    var response = await runtime.SendAsync(new(path.EndsWith("/end", StringComparison.Ordinal)
                        ? "remote-release" : "remote-heartbeat", "remote", LeaseId: session.LeaseId,
                        ClientId: session.ClientId, StreamReady: session.StreamReady,
                        FastPrivacyHeartbeat: session.FastPrivacyHeartbeat,
                        BoundedPrivacyHeartbeat: session.BoundedPrivacyHeartbeat,LockOnExit:session.LockOnExit,ExitReason:session.ExitReason), lifetime.Token).ConfigureAwait(false);
                    if (!response.Success) throw new ApiError(409, response.Message);
                }
                await WriteAsync(context, 200, new { success = true }, lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (context.Request.Path == "/v1/prepare")
            {
                var request = Normalize(await ReadJsonAsync<PrepareRequest>(context, lifetime.Token).ConfigureAwait(false));
                if (request.RequestId is not null) throw new ApiError(400, "电脑发起的副屏请求需要使用新版连接流程。");
                if (!await _operationGate.WaitAsync(0, lifetime.Token).ConfigureAwait(false)) throw new ApiError(409, "电脑正在处理另一项绑定或副屏操作，请稍后重试。");
                try { await WriteAsync(context, 200, await PrepareAsync(request, (_, _) => { }, lifetime.Token).ConfigureAwait(false), lifetime.Token).ConfigureAwait(false); }
                catch { await ReleaseAsync(request).ConfigureAwait(false); throw; }
                finally { _operationGate.Release(); }
                return;
            }
            if (path == "/v1/media-identity")
            {
                var request = await ReadJsonAsync<MediaIdentityRequest>(context, lifetime.Token).ConfigureAwait(false);
                try { _ = MediaIdentityPolicy.ParseCertificate(request.Mode, request.ClientCertificate); }
                catch (ArgumentException error) { throw new ApiError(400, error.Message); }
                if (!await _operationGate.WaitAsync(0, lifetime.Token).ConfigureAwait(false))
                    throw new ApiError(409, "电脑正在准备串流，请完成后重试媒体身份预检。");
                try
                {
                    var response = await runtime.SendAsync(new("media-identity", request.Mode,
                        ClientCertificate: request.ClientCertificate), lifetime.Token).ConfigureAwait(false);
                    if (!response.Success) throw new ApiError(response.ErrorStatus is 400 or 403 or 409 or 503 or 504
                        ? response.ErrorStatus.Value : 503, response.Message);
                    MediaIdentityResult result;
                    try
                    {
                        result = JsonSerializer.Deserialize<MediaIdentityResult>(response.PayloadJson ?? "null", Json)
                            ?? throw new JsonException();
                        if (result.Paired) _ = MediaIdentityPolicy.ParseCertificate(request.Mode, result.ServerCertificate);
                        else if (result.ServerCertificate is not null) throw new JsonException();
                    }
                    catch (Exception error) when (error is JsonException or ArgumentException)
                    { throw new ApiError(503, "电脑后台未返回有效的媒体身份核验结果。"); }
                    await WriteAsync(context, 200, new { success = true, paired = result.Paired,
                        serverCertificate = result.ServerCertificate }, lifetime.Token).ConfigureAwait(false);
                }
                finally { _operationGate.Release(); }
                return;
            }
            if (context.Request.Path == "/v1/pair")
            {
                var request = await ReadJsonAsync<PairRequest>(context, lifetime.Token).ConfigureAwait(false);
                ValidateMode(request.Mode);
                if (request.Pin is not { Length: 4 } || request.Pin.Any(character => character < '0' || character > '9')) throw new ApiError(400, "配对码必须为四位数字。");
                if (request.Name is not null && (Encoding.UTF8.GetByteCount(request.Name) > 128 || request.Name.Any(char.IsControl))) throw new ApiError(400, "设备名称过长或包含无效字符。");
                if (!await _operationGate.WaitAsync(0, lifetime.Token).ConfigureAwait(false)) throw new ApiError(409, "电脑正在准备串流，请完成后重试配对。");
                try
                {
                    var status = await GetStatusAsync(lifetime.Token).ConfigureAwait(false);
                    var engine = request.Mode == "tablet" ? status.Tablet : status.Remote;
                    if (!engine.Healthy) throw new ApiError(409, "所选串流模式尚未就绪，请先准备连接。");
                    await CommandAsync(new("pair", request.Mode, request.Pin, request.Name ?? "安卓设备"), lifetime.Token).ConfigureAwait(false);
                    await WriteAsync(context, 200, new { success = true, mode = request.Mode, message = "已确认配对，请等待安卓端完成连接。" }, lifetime.Token).ConfigureAwait(false);
                }
                finally { _operationGate.Release(); }
                return;
            }
            throw new ApiError(404, "找不到此设备接口。");
        }
        catch (ApiError error) { await WriteErrorAsync(context, error.Status, error.Message).ConfigureAwait(false); }
        catch (JsonException) { await WriteErrorAsync(context, 400, "JSON 请求格式无效或包含不支持的字段。").ConfigureAwait(false); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { await WriteErrorAsync(context, error.StatusCode, "请求正文过大或格式无效。").ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || _stopping.IsCancellationRequested) { }
        catch (OperationCanceledException) { await WriteErrorAsync(context, 504, "电脑准备超时，请检查串流状态后重试。").ConfigureAwait(false); }
        catch (InvalidOperationException error) { await WriteErrorAsync(context, 409, error.Message).ConfigureAwait(false); }
        catch { await WriteErrorAsync(context, 500, "电脑未能完成操作，请在电脑端检查状态。").ConfigureAwait(false); }
    }

    private PrepareRequest Normalize(PrepareRequest request)
    {
        ValidateMode(request.Mode);
        try { RemoteDisplaySelection.ValidateRequest(request.Mode, request.DisplayIndex); }
        catch (ArgumentException error) { throw new ApiError(400, error.Message); }
        catch (NotSupportedException error) { throw new ApiError(409, error.Message); }
        if (request.Orientation is not (0 or 90 or 180 or 270)) throw new ApiError(400, "方向只能为 0、90、180 或 270 度。");
        var portrait = request.Orientation is 90 or 270;
        var width = request.Width;
        var height = request.Height;
        var fps = request.RefreshRate;

        // A tablet must advertise its own panel mode. The old implementation
        // silently substituted one fixed panel mode, making every device look
        // like the original tablet and caused avoidable scaling and encoder
        // pressure. Receiver polling already keeps the latest capabilities in
        // memory, so older callers that omit the dimensions can be upgraded
        // when they identify the receiver. Requests without an identified
        // receiver are rejected instead of guessing a panel size.
        if (request.Mode == "tablet" && (width is null || height is null || fps is null))
        {
            var receiver = request.ClientId is { Length: > 0 } id ? _receivers.Find(id) : null;
            if (receiver is null)
                throw new ApiError(400, "副屏连接必须先上报接收设备的分辨率和刷新率，请更新平板端后重试。");
            width ??= portrait ? receiver.Height : receiver.Width;
            height ??= portrait ? receiver.Width : receiver.Height;
            fps ??= receiver.RefreshRate;
        }

        // Remote desktop requests may omit a target size because the host's
        // primary display is authoritative. Keep a conservative compatibility
        // value for the negotiation metadata; it is replaced by the actual
        // primary mode in PrepareAsync and is never used to configure a tablet.
        width ??= portrait ? 1080 : 1920;
        height ??= portrait ? 1920 : 1080;
        fps ??= 60;
        ValidateDimensions(width.Value, height.Value, fps.Value);
        var lease = request.LeaseId ?? Guid.NewGuid().ToString("N");
        ValidateId(lease, "会话");
        if (request.ClientId is not null) ValidateId(request.ClientId, "接收设备");
        if (request.RequestId is not null) ValidateId(request.RequestId, "副屏请求");
        return request with { Width = width, Height = height, RefreshRate = fps, LeaseId = lease };
    }

    private static void ValidateDimensions(int width, int height, int fps)
    {
        if (width is < 320 or > 7680 || height is < 320 or > 7680 || (long)width * height > 33_177_600 || fps is < 24 or > 240)
            throw new ApiError(400, "分辨率或刷新率超出支持范围。");
    }

    private static string[] LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
                .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                .Where(x => UsableIpv4.IsUsable(x.Address.ToString()))
                .Select(x => x.Address.ToString()).Distinct().Take(16).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    private PrepareJob FindJob(string id)
    {
        lock (_jobGate) return _jobs.GetValueOrDefault(id) ?? throw new ApiError(404, "连接准备记录已过期，请重新连接。");
    }

    private async Task RunJobAsync(PrepareJob job)
    {
        var entered = false;
        job.Cancellation.CancelAfter(TimeSpan.FromMinutes(4));
        void Progress(string stage, string text) { lock (_jobGate) SetStageLocked(job, stage, text); }
        try
        {
            await _operationGate.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
            entered = true;
            lock (_jobGate) job.State = "running";
            if (job.Request.Mode == "tablet") _receivers.BindLease(job.Request.RequestId, job.Request.ClientId, job.Request.LeaseId!);
            var result = await PrepareAsync(job.Request, Progress, job.Cancellation.Token).ConfigureAwait(false);
            lock (_jobGate)
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                job.State = "succeeded"; SetStageLocked(job, "ready", "电脑已就绪，正在连接画面"); job.Result = result;
            }
        }
        catch (Exception error)
        {
            Progress("cleanup", "正在回收本次连接");
            await ReleaseAsync(job.Request, updateRequest: false).ConfigureAwait(false);
            lock (_jobGate)
            {
                job.State = job.Cancellation.IsCancellationRequested ? "cancelled" : "failed";
                SetStageLocked(job, job.State,
                    job.State == "cancelled" ? "连接已取消或超时，可重新连接" : error is ApiError or InvalidOperationException ? error.Message : "电脑准备失败，请重试");
            }
            if (job.Request.RequestId is { } id)
                try { _receivers.Respond(id, job.Request.ClientId!, "failed", job.Message); } catch (InvalidOperationException) { }
        }
        finally { if (entered) _operationGate.Release(); }
    }

    private static void SetStageLocked(PrepareJob job, string stage, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(job.Stage, stage, StringComparison.Ordinal))
        {
            job.StageDurationsMs[job.Stage] = Math.Max(0, (long)(now - job.StageStarted).TotalMilliseconds);
            job.StageStarted = now;
            job.Stage = stage;
        }
        job.Message = message;
    }

    private async Task ReleaseAsync(PrepareRequest request, bool updateRequest = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        try
        {
            await runtime.SendAsync(new(request.Mode == "tablet" ? "tablet-release" : "remote-release", request.Mode,
                LeaseId: request.LeaseId, ClientId: request.ClientId), timeout.Token).ConfigureAwait(false);
            if (request.Mode == "tablet" && updateRequest && request.ClientId is not null)
                _receivers.Session(request.ClientId, request.LeaseId!, true);
        }
        catch (OperationCanceledException) { } // Lease watchdog independently retries cleanup.
        catch (IOException) { }
    }

    private async Task<object> PrepareAsync(PrepareRequest request, Action<string, string> progress, CancellationToken cancellationToken)
    {
        progress("checking", "正在检查电脑会话");
        // Reserve the lease before checking readiness so the warmup supervisor
        // cannot restart an engine while a receiver is connecting.
        if (request.Mode == "remote")
        {
            ValidateId(request.ClientId, "接收设备");
            if (request.StreamSizeOverride)
                await CommandAsync(new("remote-reconfigure", "remote", LeaseId: request.LeaseId, ClientId: request.ClientId), cancellationToken).ConfigureAwait(false);
            await CommandAsync(new("remote-prepare", "remote", LeaseId: request.LeaseId, ClientId: request.ClientId), cancellationToken)
                .ConfigureAwait(false);
        }
        var initial = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        // A rotation reconnect keeps the same tablet lease. The old video
        // socket may remain BUSY for a short interval while Android replaces
        // its activity; allowing that exact lease through avoids the old
        // disconnect/restart path and lets HostRuntime hot-reconfigure the
        // display immediately. Any different active session is still blocked.
        // HostRuntime validates the exact tablet lease and rejects a different
        // receiver, so the API can safely pass through the transient BUSY state
        // for tablet preparations (including clients that have no requestId).
        if ((initial.Remote.Streaming && !(request.Mode == "remote" && request.StreamSizeOverride)) || (initial.Tablet.Streaming && request.Mode != "tablet"))
            throw new ApiError(409, "电脑正在串流，请先断开当前会话再切换模式。");
        TabletDisplayMode? tablet = null;
        TabletDisplayMode[] supportedModes = [];
        var hostStagesMs = new Dictionary<string, long>();
        if (request.Mode == "tablet")
        {
            progress("display", "正在匹配副屏分辨率和方向");
            var response = await runtime.SendAsync(new("tablet-prepare", "tablet", Orientation: request.Orientation,
                Width: request.Width, Height: request.Height, RefreshRate: request.RefreshRate,
                LeaseId: request.LeaseId, ClientId: request.ClientId, RequestId: request.RequestId), cancellationToken).ConfigureAwait(false);
            if (!response.Success) throw new ApiError(409, response.Message);
            if (response.TimingsMs is not null)
                foreach (var (name, duration) in response.TimingsMs)
                    if (duration >= 0 && duration <= 600000)
                        hostStagesMs[name] = duration;
            tablet = JsonSerializer.Deserialize<TabletDisplayMode>(response.PayloadJson ?? "null", RuntimeProtocol.JsonOptions)
                ?? throw new ApiError(503, "副屏未返回实际显示模式，请重试。");
            using var result = JsonDocument.Parse(response.PayloadJson!);
            if (result.RootElement.TryGetProperty("supportedModes", out var modes))
                supportedModes = modes.Deserialize<TabletDisplayMode[]>(RuntimeProtocol.JsonOptions) ?? [];
        }
        else if (!initial.Remote.Healthy)
        {
            progress("engine", "正在启动主屏串流");
            await CommandAsync(new("start", "remote"), cancellationToken).ConfigureAwait(false);
        }
        if (RemoteWarmupPolicy.NeedsHealthWait(request.Mode, initial.Remote.Healthy))
        {
            progress("capture", "正在等待画面采集就绪");
            var readiness = await WaitHealthyAsync(request.Mode, cancellationToken).ConfigureAwait(false);
            hostStagesMs["serverinfo-200"] = readiness.FirstServerInfo200Ms;
            hostStagesMs["capture-ready"] = readiness.ElapsedMs;
        }
        progress("negotiate", "正在确认画面参数");
        var hostMode = request.Mode == "remote"
            ? readHostMode is not null ? await readHostMode(cancellationToken).ConfigureAwait(false) : HostDisplayMode.ReadPrimary()
            : default;
        var width = tablet?.Width ?? (request.StreamSizeOverride ? request.Width!.Value : hostMode.Width);
        var height = tablet?.Height ?? (request.StreamSizeOverride ? request.Height!.Value : hostMode.Height);
        var fps = tablet?.RefreshRate ?? Math.Min(hostMode.RefreshRate, request.RefreshRate!.Value);
        return new
        {
            success = true, mode = request.Mode, httpPort = request.Mode == "tablet" ? 49089 : 48989,
            httpsPort = request.Mode == "tablet" ? 49084 : 48984,
            appName = request.Mode == "tablet" ? "平板副屏" : "远程桌面",
            orientation = request.Orientation,
            nativeWidth = request.Mode == "remote" ? hostMode.Width : request.Width,
            nativeHeight = request.Mode == "remote" ? hostMode.Height : request.Height,
            width,
            height,
            fps,
            supportedModes,
            degraded = tablet?.Degraded ?? (request.Width != width || request.Height != height),
            leaseId = request.LeaseId,
            actual = new { width, height, fps },
            hostStagesMs,
            message = "电脑已准备好，请连接所选模式。"
        };
    }

    private async Task<CaptureReadinessResult> WaitHealthyAsync(string profile, CancellationToken cancellationToken)
    {
        try
        {
            return await _captureReadiness.WaitAsync(profile, runtime.GetStatusAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ApiError(504, profile == "tablet" ? "副屏引擎未在 90 秒内就绪，请在电脑端检查显示驱动。" : "主屏引擎未在 90 秒内就绪，请在电脑端检查串流状态。");
        }
    }

    private async Task<RuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var response = await runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Status is null) throw new ApiError(503, "电脑后台尚未就绪，请在电脑端打开我控制检查。");
        return response.Status;
    }

    private async Task CommandAsync(RuntimeRequest request, CancellationToken cancellationToken)
    {
        var response = await runtime.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Success) throw new ApiError(409, response.Message);
    }

    private static void ValidateMode(string mode)
    {
        if (mode is not ("remote" or "tablet")) throw new ApiError(400, "模式只能为 remote 或 tablet。");
    }

    private bool Authorized(string authorization)
    {
        if (_token is null || authorization.Length != 71 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] candidate;
        try { candidate = Convert.FromHexString(authorization[7..]); }
        catch (FormatException) { return false; }
        try { return CryptographicOperations.FixedTimeEquals(candidate, _token); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    private static bool IsFileDataRequest(HttpRequest request)
    {
        if (request.Method is not ("GET" or "PUT")) return false;
        var segments = (request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["v1", "files", "tasks", var id, "data"] && id.Length == 32 && id.All(Uri.IsHexDigit);
    }

    private bool AllowRequest(IPAddress? address, bool files = false, bool fileData = false, bool authenticated = true)
    {
        // Chunk traffic must sustain the vault's existing byte cap. Sharing the
        // 720/minute metadata allowance stopped healthy files near 180 MiB and
        // also prevented completion/cancel requests until the window expired.
        var limit = fileData
            ? 60 * ((FileTransferVault.IdleBytesPerSecond + FileTransferVault.ChunkBytes - 1) / FileTransferVault.ChunkBytes) + 120
            : files ? 720 : 360;
        if (!authenticated) limit = 60;
        var key = (fileData ? "file-data:" : files ? "files:" : "control:") + (address?.MapToIPv6().ToString() ?? "unknown");
        lock (_rateGate)
        {
            var now = DateTimeOffset.UtcNow;
            var rates = authenticated ? _rates : _anonymousRates;
            foreach (var expired in rates.Where(item => now - item.Value.Since >= TimeSpan.FromMinutes(1)).Select(item => item.Key).ToArray()) rates.Remove(expired);
            // Reserve three category buckets. New addresses share a bounded overflow
            // allowance rather than being rejected or evicting a full peer's quota.
            // Anonymous traffic can never fill the bound-device table.
            if (!rates.ContainsKey(key) && rates.Count >= (authenticated ? 253 : 61))
                key = (fileData ? "overflow-data" : files ? "overflow-files" : "overflow-control");
            if (!rates.TryGetValue(key, out var rate)) rate = (now, 0);
            if (rate.Count >= limit) return false;
            rates[key] = (rate.Since, rate.Count + 1);
            return true;
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken, int maximumBodyBytes = MaximumBodyBytes)
    {
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase)) throw new ApiError(415, "请求正文必须使用 application/json。");
        var limitText = (maximumBodyBytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (context.Request.ContentLength > maximumBodyBytes) throw new ApiError(413, $"请求正文不能超过 {limitText} KB。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var body = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (body.Length + count > maximumBodyBytes) throw new ApiError(413, $"请求正文不能超过 {limitText} KB。");
            body.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, checked((int)body.Length)), Json) ?? throw new ApiError(400, "JSON 请求不能为空。");
    }

    private static Task WriteAsync(HttpContext context, int code, object value, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = code;
        return context.Response.WriteAsJsonAsync(value, cancellationToken);
    }

    private static async Task WriteErrorAsync(HttpContext context, int code, string message)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
        try { await WriteAsync(context, code, new { success = false, message }, context.RequestAborted).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async Task StopAsync()
    {
        IsRunning = false;
        _stopping.Cancel();
        _localBinding?.Cancel();
        if (_discovery is not null) { await _discovery.DisposeAsync(); _discovery = null; }
        if (_server is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await _server.StopAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _certificate?.Dispose();
        if (_token is not null) CryptographicOperations.ZeroMemory(_token);
        _invitation = null;
    }
}
