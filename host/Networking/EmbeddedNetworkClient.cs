using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Networking;

/// <summary>
/// Tailscale LocalAPI over the product's own protected named pipe. This class is also linked
/// into the Windows service, so the UI never needs to impersonate SYSTEM or launch an external UI.
/// </summary>
public sealed class EmbeddedNetworkClient : IDisposable
{
    private readonly HttpClient _http;
    // Tests use a memory handler; production always uses the protected product pipe.
    internal EmbeddedNetworkClient(HttpClient http) { _http = http; }

    public EmbeddedNetworkClient(string socketPath = NetworkConfiguration.DefaultSocketPath)
    {
        const string prefix = @"\\.\pipe\ProtectedPrefix\Administrators\StreamHub\";
        if (!socketPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("拒绝连接产品系统保护路径以外的组网控制管道。", nameof(socketPath));
        var pipeName = socketPath[@"\\.\pipe\".Length..];
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                try
                {
                    await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
                    return pipe;
                }
                catch { pipe.Dispose(); throw; }
            }
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-tailscaled.sock/localapi/v0/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<NetworkStatus> GetStatusAsync(CancellationToken cancellationToken = default, TransportSettings? settings = null)
    {
        settings = (settings ?? new()).Validate();
        if (!settings.Transport.UsesAccount) return new NetworkStatus { Available = true, State = "Local", StateText = settings.Transport.Label + " · 无需云账号", Transport = settings };
        try
        {
            await RequireCurrentAsync(settings, cancellationToken).ConfigureAwait(false);
            using var response = await SendAsync(HttpMethod.Get, "status", null, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseStatus(document.RootElement, settings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or JsonException or TaskCanceledException)
        {
            return new NetworkStatus { Error = FriendlyError(exception), Transport = settings };
        }
    }

    /// <summary>Returns the browser authorization URL; no password or reusable auth key is handled here.</summary>
    public async Task<NetworkStatus> BeginLoginAsync(CancellationToken cancellationToken = default, TransportSettings? settings = null)
    {
        settings = (settings ?? new()).Validate();
        await RequireCurrentAsync(settings, cancellationToken).ConfigureAwait(false);
        var initial = await GetStatusAsync(cancellationToken, settings).ConfigureAwait(false);
        if (!initial.Available) throw new InvalidOperationException("内置异地连接服务未就绪，请先启用异地连接。");
        if (initial.LoggedIn && initial.State == "Running") return initial;
        // Start preserves the stored profile. Masked edits preserve all unrelated preferences and keys.
        using (await SendAsync(HttpMethod.Post, "start", "{}", cancellationToken).ConfigureAwait(false)) { }
        await SetRunningAsync(true, cancellationToken, settings).ConfigureAwait(false);
        using (await SendAsync(HttpMethod.Post, "login-interactive", null, cancellationToken).ConfigureAwait(false)) { }
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var status = await GetStatusAsync(cancellationToken, settings).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(status.BrowseUrl) || status.State == "Running") return status;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("暂时未取得授权地址，请检查电脑能否访问登录网站后重试。已有账号状态不会被清除。");
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "logout", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pause/resume the product's network without deleting its saved login.</summary>
    public async Task SetRunningAsync(bool running, CancellationToken cancellationToken = default, TransportSettings? settings = null)
    {
        if (running) await RequireCurrentAsync((settings ?? new()).Validate(), cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new
        {
            WantRunning = running,
            WantRunningSet = true,
            ForceDaemon = true,
            ForceDaemonSet = true,
            CorpDNS = false,
            CorpDNSSet = true,
            RouteAll = false,
            RouteAllSet = true
        });
        using var response = await SendAsync(HttpMethod.Patch, "prefs", body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NetworkDiagnostics> DiagnoseAsync(CancellationToken cancellationToken = default, TransportSettings? settings = null)
    {
        var status = await GetStatusAsync(cancellationToken, settings).ConfigureAwait(false);
        return new NetworkDiagnostics
        {
            Status = status,
            Checks =
            [
                status.Available ? "内置组网控制通道：正常" : "内置组网控制通道：不可用",
                status.LoggedIn ? "账号授权：已保存" : "账号授权：未完成",
                status.State == "Running" ? "异地网络：已连接" : "异地网络：未连接",
                $"可见设备：{status.Peers.Length}，在线：{status.Peers.Count(peer => peer.Online)}",
                "设备在线状态不等于已安装我控制；串流权限仍需单独绑定。",
                "本地连接不依赖云账号；异地直连或中继由实际网络决定。"
            ]
        };
    }

    private async Task<string> CurrentServerAsync(CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "prefs", null, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var value = Text(document.RootElement, "ControlURL");
        return HubTransports.ControlServer(value);
    }
    public async Task RequireCurrentAsync(TransportSettings settings, CancellationToken token)
    {
        if (!settings.Transport.UsesAccount) throw new InvalidOperationException("当前通道无需连接账号，请先选择 Tailscale 或自建服务区。");
        if (HubTransports.ControlServer(settings.ControlUrl) != await CurrentServerAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("内置连接的服务区与设置不一致，请重新应用通道设置。");
    }
    public async Task ConfigureAsync(TransportSettings settings, CancellationToken token)
    {
        settings = settings.Validate();
        var patch = new Dictionary<string, object> { ["WantRunning"] = false, ["WantRunningSet"] = true };
        var changedServer = settings.Transport.UsesAccount && HubTransports.ControlServer(settings.ControlUrl) != await CurrentServerAsync(token).ConfigureAwait(false);
        if (changedServer)
        {
            using var response = await SendAsync(HttpMethod.Get, "status", null, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = document.RootElement;
            var user = Text(Property(root, "Self"), "UserID");
            if (Strings(root, "TailscaleIPs").Length > 0 || user is not ("" or "0"))
                throw new InvalidOperationException("更换服务区前请先退出当前连接账号。电脑绑定会保留。");
            patch["ControlURL"] = settings.ControlUrl;
            patch["ControlURLSet"] = true;
        }
        using (await SendAsync(HttpMethod.Patch, "prefs", JsonSerializer.Serialize(patch), token).ConfigureAwait(false)) { }
        if (changedServer)
        {
            // Rebuild the controlclient from saved prefs; do not replace the
            // full preferences object or start interactive login here.
            using var started = await SendAsync(HttpMethod.Post, "start", "{}", token).ConfigureAwait(false);
        }
    }

    /// <summary>Resume a saved account without pausing or rebuilding a healthy control client.</summary>
    public async Task ResumeAsync(TransportSettings settings, string? ownerSid, CancellationToken token)
    {
        settings = settings.Validate();
        if (!settings.Transport.UsesAccount) throw new InvalidOperationException("当前通道无需启用异地连接。");
        using var currentResponse = await SendAsync(HttpMethod.Get, "profiles/current", null, token).ConfigureAwait(false);
        using var current = JsonDocument.Parse(await currentResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (Text(current.RootElement, "ID").Length == 0)
        {
            // Windows can restart with an empty current profile even though a saved
            // account remains. Only restore one unambiguous profile owned by this
            // service or the registered product owner, on the selected server.
            using var response = await SendAsync(HttpMethod.Get, "profiles/", null, token).ConfigureAwait(false);
            using var profiles = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (profiles.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            var matches = profiles.RootElement.EnumerateArray().Where(profile =>
            {
                var owner = Text(profile, "LocalUserID");
                if (owner.Length != 0 && owner != "S-1-5-18" && (string.IsNullOrEmpty(ownerSid) || owner != ownerSid)) return false;
                try { return Text(profile, "ID").Length > 0 && Text(profile, "Key").Length > 0 &&
                    HubTransports.ControlServer(Text(profile, "ControlURL")) == HubTransports.ControlServer(settings.ControlUrl); }
                catch (ArgumentException) { return false; }
            }).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("找到多个已保存的同服务区账号，未自动切换。请在账号页确认后重新登录。");
            if (matches.Length == 1)
            {
                var id = Text(matches[0], "ID");
                if (id.Length > 128 || !id.All(char.IsAsciiLetterOrDigit)) throw new JsonException();
                using (await SendAsync(HttpMethod.Post, "profiles/" + id, null, token).ConfigureAwait(false)) { }
            }
        }
        // A fresh installation may configure a server, but never overwrite a
        // different saved account to satisfy a startup preference.
        if (await CurrentServerAsync(token).ConfigureAwait(false) != HubTransports.ControlServer(settings.ControlUrl))
        {
            if (Text(current.RootElement, "ID").Length != 0)
                throw new InvalidOperationException("已保存账号的服务区与设置不一致，未自动切换。请在账号页确认连接设置。");
            await ConfigureAsync(settings, token).ConfigureAwait(false);
        }
        await SetRunningAsync(true, token, settings).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        var code = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(code == HttpStatusCode.Forbidden
            ? "组网服务拒绝了此操作，请通过我控制后台服务执行。"
            : $"内置组网服务返回错误（{(int)code}），请查看诊断信息。", null, code);
    }

    internal static NetworkStatus ParseStatus(JsonElement root, TransportSettings? settings = null)
    {
        var state = Text(root, "BackendState");
        var self = Property(root, "Self");
        var user = Property(Property(root, "User"), Text(self, "UserID"));
        var peers = new List<NetworkPeer>();
        var peerMap = Property(root, "Peer");
        if (peerMap.ValueKind == JsonValueKind.Object)
            foreach (var item in peerMap.EnumerateObject())
            {
                var p = item.Value;
                var relay = Text(p, "Relay");
                peers.Add(new NetworkPeer
                {
                    Id = Text(p, "ID"),
                    Name = Text(p, "HostName"),
                    OperatingSystem = Text(p, "OS"),
                    Addresses = UsableIpv4.Filter(Strings(p, "TailscaleIPs")),
                    Online = Boolean(p, "Online"),
                    Active = Boolean(p, "Active"),
                    RouteText = Text(p, "CurAddr").Length > 0 ? "直连" : Text(p, "PeerRelay").Length > 0
                        ? "设备中继" : relay.Length > 0 ? "中继可用" : "尚未连接",
                    RelayRegion = relay,
                    ReceivedBytes = Number(p, "RxBytes"),
                    SentBytes = Number(p, "TxBytes")
                });
            }
        var addresses = UsableIpv4.Filter(Strings(root, "TailscaleIPs"));
        settings = (settings ?? new()).Validate();
        var authUrl = HubTransports.AuthorizationUrl(settings, Text(root, "AuthURL"));
        return new NetworkStatus
        {
            Transport = settings,
            Available = true,
            LoggedIn = addresses.Length > 0 && Text(self, "UserID") is not ("" or "0"),
            State = state,
            StateText = state switch
            {
                "Running" => "异地连接已就绪",
                "NeedsLogin" => "等待账号登录",
                "NeedsMachineAuth" => "等待账号管理员批准设备",
                "Stopped" => "异地连接已暂停",
                "Starting" => "异地连接启动中",
                "InUseOtherUser" => "组网状态正由其他用户使用",
                "NoState" => "组网组件正在准备",
                _ => "正在读取异地连接状态"
            },
            Version = Text(root, "Version"),
            UserName = Text(user, "DisplayName"),
            LoginName = Text(user, "LoginName"),
            HostName = Text(self, "HostName"),
            NetworkName = Text(Property(root, "CurrentTailnet"), "Name"),
            Addresses = addresses,
            Peers = peers.OrderByDescending(p => p.Online).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            BrowseUrl = authUrl,
            Health = Strings(root, "Health")
        };
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : default;
    private static string Text(JsonElement element, string name)
    {
        var property = Property(element, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" :
            property.ValueKind == JsonValueKind.Number ? property.GetRawText() : "";
    }
    private static bool Boolean(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static long Number(JsonElement element, string name) => Property(element, name).TryGetInt64Safe();
    private static string[] Strings(JsonElement element, string name)
    {
        var property = Property(element, name);
        return property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray()
            : [];
    }
    private static string FriendlyError(Exception exception) => exception switch
    {
        TaskCanceledException or TimeoutException => "内置组网服务未响应，请确认后台服务正在运行。",
        JsonException => "内置组网状态格式不兼容，请修复安装。",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "后台组网操作权限不足，请修复安装。",
        _ => "内置异地连接尚未启动，或后台服务暂时不可用。"
    };

    public void Dispose() => _http.Dispose();
}

internal static class NetworkJsonExtensions
{
    public static long TryGetInt64Safe(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
