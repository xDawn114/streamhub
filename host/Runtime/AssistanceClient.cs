using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

public sealed record AssistanceConnection(int Version, string[] Addresses, int Port, string Pin, string? Code = null);

/// <summary>Ephemeral media identity and a pinned control channel with optional local trust.</summary>
public sealed class AssistanceClient : IAsyncDisposable
{
    private HttpClient? _http;
    private RSA? _key;
    private X509Certificate2? _certificate;
    private string? _proof, _sessionId, _address, _directory, _identityId;
    private Process? _receiver;
    private ReceiverProcessJob? _receiverJob;
    private FileStream? _sessionLock;
    private readonly string _installRoot;
    private readonly AssistancePeerStore _store;
    private readonly Func<AssistanceConnection, string, HttpMessageHandler> _handlerFactory;
    private AssistancePeerStore.Binding? _binding;
    private bool _discardPending;
    public string? CleanupWarning { get; private set; }
    public AssistanceClient(string? installRoot = null, AssistancePeerStore? store = null)
        : this(installRoot, store, CreateHandler) { }
    internal AssistanceClient(string? installRoot, AssistancePeerStore? store,
        Func<AssistanceConnection, string, HttpMessageHandler> handlerFactory)
    {
        _installRoot = installRoot ?? AppContext.BaseDirectory;
        _store = store ?? new AssistancePeerStore();
        _handlerFactory = handlerFactory;
    }
    public IReadOnlyList<AssistancePeer> ListSavedPeers() => _store.List();
    public void Forget(AssistancePeer peer) => _store.Forget(peer);
    public static AssistanceConnection ParseConnection(string text)
    {
        if (!text.StartsWith("icontrol-help:", StringComparison.Ordinal) || text.Length > 8192)
            throw new ArgumentException("请粘贴被控电脑复制的连接信息。");
        try
        {
            var result = JsonSerializer.Deserialize<AssistanceConnection>(Convert.FromBase64String(text[14..]), RuntimeProtocol.JsonOptions);
            if (result is null || result.Version != 1 || result.Port != 49199 || result.Pin is not { Length: 64 } ||
                !result.Pin.All(char.IsAsciiHexDigit) || result.Addresses is not { Length: > 0 and <= 16 } ||
                result.Addresses.Any(a => !IPAddress.TryParse(a, out var ip) ||
                    ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.GetAddressBytes()[0] >= 224))
                throw new ArgumentException("连接信息格式无效。");
            return result;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new ArgumentException("连接信息格式无效，请重新复制。"); }
    }
    public static string CreateInvitation(string connectionInfo, string code)
    {
        var connection = ParseConnection(connectionInfo);
        if (!ValidCode(code)) throw new ArgumentException("协助码格式无效。");
        return "icontrol-help:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            connection with { Code = code.Trim().ToUpperInvariant() }, RuntimeProtocol.JsonOptions));
    }
    public static bool ValidCode(string? code)
    {
        string compact = (code ?? "").Trim().Replace("-", "");
        return compact.Length == 8 && compact.All(char.IsAsciiLetterOrDigit);
    }
    public Task<AssistanceReply> JoinAsync(string connectionInfo, string code, CancellationToken token)
    {
        if (!ValidCode(code)) throw new ArgumentException("协助码格式无效。");
        var connection = ParseConnection(connectionInfo.Trim());
        return JoinCoreAsync(connection, () => _store.Prepare(connection), code.Trim().ToUpperInvariant(), null, token);
    }
    public Task<AssistanceReply> JoinTrustedAsync(string peerId, CancellationToken token)
    {
        var binding = _store.Resolve(peerId);
        string mode = binding.Peer.Mode == "control" ? "control" : "view";
        return JoinCoreAsync(ParseConnection(binding.Peer.ConnectionInfo), () => binding, null, mode, token);
    }
    public Task<AssistanceReply> JoinTrustedAsync(string peerId, string mode, CancellationToken token)
    {
        var binding = _store.Resolve(peerId);
        if (mode is not ("view" or "control") || !binding.Peer.Mode.Split(',').Contains(mode))
            throw new InvalidOperationException("此电脑尚未授予该协助权限。");
        return JoinCoreAsync(ParseConnection(binding.Peer.ConnectionInfo), () => binding, null, mode, token);
    }
    private async Task<AssistanceReply> JoinCoreAsync(AssistanceConnection connection,
        Func<AssistancePeerStore.Binding> getBinding, string? code, string? mode, CancellationToken token)
    {
        if (_http is not null || _key is not null) throw new InvalidOperationException("请先结束上次协助。");
        token.ThrowIfCancellationRequested();
        ValidateReceiver(); // No owner approval or temporary host pairing without a usable receiver.
        CleanupWarning = null;
        _discardPending = false;
        _binding = getBinding();
        try
        {
            _key = RSA.Create(2048);
            _identityId = Guid.NewGuid().ToString("N");
            var csr = new CertificateRequest("CN=iControl-help-" + _identityId, _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _certificate = csr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(3));
            _proof = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Exception? last = null;
            foreach (string address in connection.Addresses)
            {
                token.ThrowIfCancellationRequested();
                _http = new(_handlerFactory(connection, address)) {
                    BaseAddress = new Uri($"https://{address}:{connection.Port}/"), Timeout = TimeSpan.FromSeconds(40)
                };
                try
                {
                    // Select a reachable pinned address without consuming a code or trust challenge.
                    using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using var probe = await _http.GetAsync("v1/assistance/probe", HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token);
                }
                catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !token.IsCancellationRequested)
                { last = error; _http.Dispose(); _http = null; continue; }
                _address = address;
                string certificate = Convert.ToBase64String(_certificate.RawData);
                string operation;
                object body;
                if (mode is null)
                {
                    operation = "join";
                    body = new { code, name = Environment.MachineName, proof = _proof, certificate,
                        clientId = _binding.ClientId, credential = _binding.Credential };
                }
                else
                {
                    var challenge = (await SendResultAsync("trusted-challenge", new { clientId = _binding.ClientId, mode }, token))
                        .Deserialize<AssistanceTrustChallenge>(RuntimeProtocol.JsonOptions);
                    if (challenge is null || challenge.ChallengeId is not { Length: 64 } ||
                        !challenge.ChallengeId.All(char.IsAsciiHexDigit) || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
                        throw new InvalidDataException("被控电脑返回的身份验证已失效。");
                    operation = "trusted-join";
                    body = new { clientId = _binding.ClientId, credential = _binding.Credential,
                        challengeId = challenge.ChallengeId, authProof = CreateAuthProof(_binding, challenge.ChallengeId, mode, _proof, certificate),
                        mode, name = Environment.MachineName, proof = _proof, certificate };
                }
                // Once sent, neither redemption nor a consumed trust challenge is replayed on another address.
                var reply = await SendAsync(operation, body, token);
                if (mode is not null && (reply.State != "active" || !reply.Trusted || reply.Mode != mode))
                    throw new InvalidDataException("被控电脑未确认本次信任授权。");
                return reply;
            }
            throw new InvalidOperationException("无法连接被控电脑，请确认处于同一局域网或 Tailscale 已连通。", last);
        }
        catch { await EndAsync(); throw; }
    }
    internal static HttpClientHandler CreateHandler(AssistanceConnection connection, string address) => new() {
        UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(connection.Pin))
    };
    private static string CreateAuthProof(AssistancePeerStore.Binding binding, string challengeId,
        string mode, string proof, string certificate)
    {
        byte[] secret = Convert.FromHexString(binding.Credential);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(
                AssistanceTrustRegistry.AuthenticationMessage(binding.ClientId, challengeId, mode, proof, certificate))));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public Task<AssistanceReply> PollAsync(CancellationToken token) => SendAsync("poll", new { sessionId = _sessionId, proof = _proof }, token);
    public Task<AssistanceReply> HeartbeatAsync(CancellationToken token) => SendAsync("heartbeat", new { sessionId = _sessionId, proof = _proof }, token);
    private async Task<AssistanceReply> SendAsync(string operation, object body, CancellationToken token)
    {
        JsonElement result;
        try { result = await SendResultAsync(operation, body, token); }
        catch
        {
            if (_discardPending && _binding is not null) _store.DiscardPending(_binding);
            throw;
        }
        var reply = result.Deserialize<AssistanceReply>(RuntimeProtocol.JsonOptions)
            ?? throw new InvalidDataException("协助回复不完整。");
        if (!Guid.TryParseExact(reply.SessionId, "D", out var id) || id == Guid.Empty ||
            _sessionId is not null && reply.SessionId != _sessionId)
            throw new InvalidDataException("协助回复与本次会话不匹配。");
        _sessionId = reply.SessionId;
        if (reply.State == "active" && _binding is not null && reply.Trusted)
        {
            string? mode = reply.TrustedMode ?? reply.Mode;
            if (!AssistancePeerStore.ValidMode(mode)) throw new InvalidDataException("协助信任权限无效。");
            _store.Confirm(_binding, mode!, reply.HostName);
        }
        return reply;
    }
    private async Task<JsonElement> SendResultAsync(string operation, object body, CancellationToken token)
    {
        using var response = await (_http ?? throw new InvalidOperationException("协助未连接。"))
            .PostAsJsonAsync("v1/assistance/" + operation, body, token);
        if ((int)response.StatusCode is >= 400 and < 500) _discardPending = true;
        if (response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("协助回复过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (bounded.Length + count > 16384) throw new InvalidDataException("协助回复过大。");
            bounded.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(bounded.ToArray());
        if (!response.IsSuccessStatusCode || !document.RootElement.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
        {
            _discardPending = true;
            string message = document.RootElement.TryGetProperty("message", out var errorMessage) &&
                errorMessage.ValueKind == JsonValueKind.String ? errorMessage.GetString()! : "被控电脑未接受协助请求。";
            foreach (string? secret in new[] { _binding?.Credential, _proof })
                if (secret is not null) message = message.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(message);
        }
        if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("协助回复不完整。");
        return result.Clone();
    }
    public async Task StreamAsync(AssistanceReply reply, Action started, CancellationToken token)
    {
        if (reply.State != "active" || reply.SessionId != _sessionId || reply.MediaHostId != _sessionId ||
            reply.Mode is not ("view" or "control") || _key is null || _certificate is null || _address is null)
            throw new InvalidOperationException("协助尚未获得被控端授权。");
        var server = MediaIdentityPolicy.ParseCertificate("remote", reply.ServerCertificate);
        using var serverCert = new X509Certificate2(server);
        string source = ValidateReceiver();
        string sessions = SessionsRoot;
        Directory.CreateDirectory(sessions);
        _directory = Path.Combine(sessions, _identityId!);
        Directory.CreateDirectory(_directory);
        // Keep private keys accessible only to this logged-in user and SYSTEM.
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(acl);
        _sessionLock = new FileStream(Path.Combine(_directory, "owner.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        CopyRuntime(source, _directory);
        string settings = Path.Combine(_directory, "Moonlight Game Streaming Project");
        Directory.CreateDirectory(settings);
        string ini = BuildSettings(reply, _address, _certificate.ExportCertificatePem(), _key.ExportRSAPrivateKeyPem(), serverCert.ExportCertificatePem());
        await File.WriteAllTextAsync(Path.Combine(settings, "Moonlight.ini"), ini, new UTF8Encoding(false), token);
        await File.WriteAllTextAsync(Path.Combine(_directory, "portable.dat"), "", token);
        var start = new ProcessStartInfo(Path.Combine(_directory, "Moonlight.exe")) {
            WorkingDirectory = _directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "stream", reply.MediaHostId!, "远程协助桌面", "--1080", "--fps", "60",
            "--bitrate", "15000", "--display-mode", "windowed", "--no-quit-after", "--no-multi-controller" })
            start.ArgumentList.Add(argument);
        _receiver = Process.Start(start) ?? throw new InvalidOperationException("无法启动协助画面。");
        _receiverJob = new(_receiver);
        using var logLifetime = new CancellationTokenSource();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var videoReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task output = ReadReceiverLogAsync(_receiver.StandardOutput, failure, videoReady, logLifetime.Token);
        Task error = ReadReceiverLogAsync(_receiver.StandardError, failure, videoReady, logLifetime.Token);
        Task exited = _receiver.WaitForExitAsync(token);
        bool receivedVideo = false;
        DateTimeOffset videoDeadline = DateTimeOffset.UtcNow.AddSeconds(35);
        try
        {
            while (!_receiver.HasExited)
            {
                Task heartbeat = Task.Delay(TimeSpan.FromSeconds(3), token);
                if (receivedVideo) await Task.WhenAny(exited, failure.Task, heartbeat);
                else await Task.WhenAny(exited, failure.Task, heartbeat, videoReady.Task);
                token.ThrowIfCancellationRequested();
                if (failure.Task.IsCompleted) throw new InvalidOperationException(await failure.Task);
                if (!receivedVideo && videoReady.Task.IsCompleted)
                {
                    receivedVideo = true;
                    started();
                }
                if (!receivedVideo && DateTimeOffset.UtcNow >= videoDeadline)
                    throw new InvalidOperationException("未能收到协助画面，请检查两台电脑的连接后重试。");
                if (!_receiver.HasExited) await HeartbeatAsync(token);
            }
            if (_receiver.ExitCode != 0) throw new InvalidOperationException("协助接收程序未能完成连接。");
            if (!receivedVideo) throw new InvalidOperationException("协助画面尚未建立，连接已结束。");
        }
        finally
        {
            logLifetime.Cancel();
            try { await EndAsync(); }
            finally { await Task.WhenAll(output, error); }
        }
    }
    private static async Task ReadReceiverLogAsync(StreamReader reader, TaskCompletionSource<string> failure,
        TaskCompletionSource videoReady, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.Contains("Received first video packet after ", StringComparison.Ordinal))
                    videoReady.TrySetResult();
                if (line.Contains("Failed to import private key:", StringComparison.Ordinal))
                    failure.TrySetResult("Windows 无法使用本次临时密钥，协助已请求结束。");
                else if (line.Contains(" - Qt Critical:", StringComparison.Ordinal) || line.Contains(" - Qt Fatal:", StringComparison.Ordinal))
                    failure.TrySetResult("协助画面已结束或启动失败，本次授权已请求回收。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { if (!token.IsCancellationRequested) failure.TrySetResult("无法读取协助画面状态，本次授权已请求回收。"); }
    }
    internal static string BuildSettings(AssistanceReply reply, string address, string cert, string key, string serverCert, int port = 48989)
    {
        static string Bytes(string value) => "@ByteArray(" + value.Replace("\r", "").Replace("\n", "\\n") + ")";
        return "[General]\nmdns=false\nuniqueid=" + reply.SessionId!.Replace("-", "")[..16] +
            "\ncertificate=" + Bytes(cert) + "\nkey=" + Bytes(key) +
            "\n\n[hosts]\nsize=1\n1\\hostname=iControl temporary assistance\n1\\uuid=" + reply.MediaHostId +
            "\n1\\manualaddress=" + address + "\n1\\manualport=" + port + "\n1\\localaddress=" + address +
            "\n1\\localport=" + port + "\n1\\srvcert=" + Bytes(serverCert) + "\n1\\nvidiasw=false\n";
    }
    private string ValidateReceiver()
    {
        string root = Path.Combine(_installRoot, "components", "assistance-receiver");
        string record = Path.Combine(root, "receiver-files.json");
        if (!File.Exists(record)) throw new InvalidOperationException("Windows 协助接收组件缺失，请修复安装。");
        using var manifest = JsonDocument.Parse(File.ReadAllText(record));
        var files = manifest.RootElement.GetProperty("files");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateArray())
        {
            string relative = item.GetProperty("path").GetString()!;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !expected.Add(relative))
                throw new InvalidDataException("协助接收组件完整性核对失败。");
            using var input = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("协助接收组件完整性核对失败。");
        }
        if (!expected.Contains("Moonlight.exe") || !expected.Contains(Path.Combine("tls", "qopensslbackend.dll")))
            throw new InvalidDataException("协助接收组件缺失，请修复安装。");
        return root;
    }
    internal static void CopyRuntime(string source, string destination)
    {
        // Only copy manifest-listed files, never a previous user's portable profile.
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "receiver-files.json")));
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string name = item.GetProperty("path").GetString()!;
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, name), target);
        }
    }
    public async Task EndAsync()
    {
        // Revocation must be attempted even if process or local-file cleanup fails.
        if (_http is not null && _sessionId is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await SendResultAsync("end", new { sessionId = _sessionId, proof = _proof }, timeout.Token); }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or
                JsonException or IOException or InvalidDataException)
            { CleanupWarning = "暂未收到被控电脑的结束确认，请在被控电脑检查协助状态。"; }
        }
        try
        {
            _receiverJob?.Dispose(); _receiverJob = null;
            if (_receiver is { } process)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                process.Dispose(); _receiver = null;
            }
        }
        finally
        {
            var binding = _binding;
            bool discardPending = _discardPending || _sessionId is not null;
            _http?.Dispose(); _http = null; _key?.Dispose(); _key = null; _certificate?.Dispose(); _certificate = null;
            _proof = _sessionId = null;
            _binding = null; _address = null; _discardPending = false;
            if (_receiver is null)
            {
                _sessionLock?.Dispose(); _sessionLock = null;
                if (_directory is not null) { DeleteSession(_directory); _directory = null; }
                _identityId = null;
            }
            if (binding is not null && discardPending) _store.DiscardPending(binding);
        }
    }
    private static string SessionsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamHub", "assistance-sessions");
    public static void CleanupPreviousSessions()
    {
        if (!Directory.Exists(SessionsRoot)) return;
        foreach (string path in Directory.GetDirectories(SessionsRoot)) DeleteSession(path);
    }
    private static void DeleteSession(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Parent?.FullName != Path.GetFullPath(SessionsRoot) ||
            !Guid.TryParseExact(directory.Name, "N", out _) || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("协助会话目录无法安全清理。");
        string marker = Path.Combine(path, "owner.lock");
        if (File.Exists(marker))
        {
            // Another logged-in instance's live receiver must remain untouched.
            using var check = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        // Refuse links anywhere in the tree before recursive removal.
        if (directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(f => (f.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("协助会话目录含链接，未清理。");
        // Qt's Schannel backend can persist an imported key under the certificate CN.
        string keyName = "iControl-help-" + directory.Name;
        if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
        {
            using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            key.Delete();
        }
        directory.Delete(true);
    }
    public async ValueTask DisposeAsync() => await EndAsync();
}
