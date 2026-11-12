using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed class MediaIdentityException(int status, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;
}

internal sealed class BackendApi(ProfileStore profiles,
    Func<string, CancellationToken, Task<HttpClient>>? mediaClientFactory = null)
{
    private const int MaximumClientListBytes = 256 * 1024;
    internal static readonly TimeSpan MediaIdentityTimeout = TimeSpan.FromSeconds(3);
    private const string InvalidClientList = "串流引擎客户端列表结构无效，未更改媒体信任。";

    // Caller holds HostRuntime's gate. The callback checks the selected engine
    // immediately before every deletion; an HTTP operation gate alone is insufficient.
    public async Task<MediaIdentityResult> GetMediaIdentityAsync(string profile, string? clientCertificate,
        Func<CancellationToken, Task<bool>> canDeduplicate, CancellationToken cancellationToken)
    {
        var clientDer = MediaIdentityPolicy.ParseCertificate(profile, clientCertificate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MediaIdentityTimeout);
        var mutationAttempted = false;
        try
        {
            var serverDer = await profiles.ReadMediaServerCertificateAsync(profile, timeout.Token);
            // Pin and return the SAME public DER snapshot, never a private key.
            using var http = await (mediaClientFactory is null ? CreateClientAsync(profile, timeout.Token, serverDer)
                : mediaClientFactory(profile, timeout.Token));
            var expected = await ReadMediaSnapshotAsync(http, profile, clientDer, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            var matches = expected.Where(x => x.Certificate.AsSpan().SequenceEqual(clientDer))
                .OrderBy(x => x.Uuid, StringComparer.Ordinal).ToArray();
            if (matches.Length == 0) return new(false, null);
            if (matches.Length > 1)
            {
                if (!await canDeduplicate(timeout.Token)) throw BusyMediaIdentity();
                using (var response = await http.GetAsync("api/csrf-token", HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                using (var csrf = await ReadMediaDocumentAsync(response, timeout.Token))
                {
                    var root = csrf.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                        !root.TryGetProperty("csrf_token", out var token) || token.ValueKind != JsonValueKind.String ||
                        token.GetString() is not { Length: > 0 and <= 512 } value || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                        throw new MediaIdentityException(503, "串流引擎未提供有效的操作校验令牌，未修复重复记录。");
                    http.DefaultRequestHeaders.Add("X-CSRF-Token", value);
                }
                foreach (var extra in matches.Skip(1))
                {
                    var before = await ReadMediaSnapshotAsync(http, profile, clientDer, timeout.Token);
                    if (!SameMediaProjection(expected, before)) throw ChangedMediaIdentity();
                    if (!await canDeduplicate(timeout.Token)) throw BusyMediaIdentity();
                    // UUIDs are unique and both keeper/target still have the exact DER.
                    // No name matching, enabling, unpair-all or state-file writes.
                    using var body = new StringContent(JsonSerializer.Serialize(new { uuid = extra.Uuid }), Encoding.UTF8, "application/json");
                    // A lost response may still mean the exact duplicate was removed.
                    mutationAttempted = true;
                    using var response = await http.PostAsync("api/clients/unpair", body, timeout.Token);
                    using var removed = await ReadMediaDocumentAsync(response, timeout.Token);
                    var root = removed.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                        !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
                        throw new MediaIdentityException(503, "串流引擎未确认精确记录删除，请重试媒体身份预检。");
                    expected = expected.Where(x => x.Uuid != extra.Uuid).ToArray();
                    var after = await ReadMediaSnapshotAsync(http, profile, clientDer, timeout.Token);
                    if (!SameMediaProjection(expected, after)) throw ChangedMediaIdentity();
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
            return new(true, Convert.ToBase64String(serverDer));
        }
        catch (MediaIdentityException error) when (mutationAttempted)
        { throw new MediaIdentityException(error.Status, "媒体身份预检未完成，重复记录可能已部分清理，请重新核验。"); }
        catch (MediaIdentityException) { throw; }
        catch (JsonException) { throw new MediaIdentityException(503, mutationAttempted ? "媒体身份预检未完成，重复记录可能已部分清理，请重新核验。" : InvalidClientList); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaIdentityException(504, mutationAttempted ? "媒体身份预检超时，重复记录可能已部分清理，请重新核验。" : "媒体身份预检超时，未更改媒体信任。"); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new MediaIdentityException(503, mutationAttempted ? "媒体身份预检未完成，重复记录可能已部分清理，请重新核验。" : "媒体身份预检未完成，未更改媒体信任。请检查所选串流模式后重试。"); }
    }

    private static MediaIdentityException ChangedMediaIdentity() => new(409, "媒体配对状态正在变化，请稍后重试身份预检。");
    private static MediaIdentityException BusyMediaIdentity() => new(409, "所选模式正在串流、忙碌或尚未就绪，请结束会话后重试重复身份修复。");

    private async Task<MediaTrustClient[]> ReadMediaSnapshotAsync(HttpClient http, string profile, byte[] clientDer, CancellationToken token)
    {
        var before = await profiles.ReadMediaTrustAsync(profile, token);
        RejectRevoked(before, clientDer);
        using var response = await http.GetAsync("api/clients/list", HttpCompletionOption.ResponseHeadersRead, token);
        using var document = await ReadMediaDocumentAsync(response, token);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
            !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("named_certs", out var clients) || clients.ValueKind != JsonValueKind.Array || clients.GetArrayLength() > 1024)
            throw new MediaIdentityException(503, InvalidClientList);
        var listed = new Dictionary<string, bool>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        foreach (var client in clients.EnumerateArray())
        {
            if (client.ValueKind != JsonValueKind.Object || HasDuplicateProperties(client) ||
                !client.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !client.TryGetProperty("uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(uuid.GetString(), "D", out var id) || id == Guid.Empty || !ids.Add(id) ||
                !client.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new MediaIdentityException(503, InvalidClientList);
            listed.Add(uuid.GetString()!, enabled.GetBoolean());
        }
        var after = await profiles.ReadMediaTrustAsync(profile, token);
        RejectRevoked(after, clientDer);
        if (after.Any(x => x.Certificate.AsSpan().SequenceEqual(clientDer) && listed.TryGetValue(x.Uuid, out var enabled) && !enabled))
            throw new MediaIdentityException(403, "此媒体证书的授权已被停用，请在电脑端由所有者处理。");
        if (!SameMediaProjection(before, after) || listed.Count != after.Length ||
            after.Any(x => !listed.TryGetValue(x.Uuid, out var enabled) || enabled != x.Enabled)) throw ChangedMediaIdentity();
        return after;
    }

    private static void RejectRevoked(MediaTrustClient[] clients, byte[] clientDer)
    {
        if (clients.Any(x => !x.Enabled && x.Certificate.AsSpan().SequenceEqual(clientDer)))
            throw new MediaIdentityException(403, "此媒体证书的授权已被停用，请在电脑端由所有者处理。");
    }

    private static bool SameMediaProjection(MediaTrustClient[] left, MediaTrustClient[] right)
    {
        if (left.Length != right.Length) return false;
        var indexed = right.ToDictionary(x => x.Uuid, StringComparer.Ordinal);
        return left.All(x => indexed.TryGetValue(x.Uuid, out var other) && x.Enabled == other.Enabled &&
            x.Certificate.AsSpan().SequenceEqual(other.Certificate));
    }

    private static async Task<JsonDocument> ReadMediaDocumentAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumClientListBytes)
            throw new MediaIdentityException(503, "串流引擎接口响应无效，未确认媒体身份操作。");
        using var body = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            if (body.Length + count > MaximumClientListBytes) throw new MediaIdentityException(503, InvalidClientList);
            body.Write(buffer, 0, count);
        }
        // Parse a copy: the document owns the response bytes after this method returns.
        return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    public async Task PairAsync(string profile, string? pin, string? name, CancellationToken cancellationToken)
    {
        try { await PairCoreAsync(profile, pin, name, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("配对等待超时，请在安卓端重新发起配对后再试。"); }
    }

    private async Task PairCoreAsync(string profile, string? pin, string? name, CancellationToken cancellationToken)
    {
        if (pin is null || !Regex.IsMatch(pin, "^[0-9]{4}$")) throw new ArgumentException("请输入客户端显示的四位数字配对码。");
        name = string.IsNullOrWhiteSpace(name) ? "安卓设备" : name.Trim();
        if (Encoding.UTF8.GetByteCount(name) > 128 || name.Any(char.IsControl)) throw new ArgumentException("设备名称过长或包含无效字符。");
        using var http = await CreateClientAsync(profile, cancellationToken);
        string? pairingId = null;
        using (var pending = await http.GetAsync("api/pin", cancellationToken))
        {
            if (pending.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await pending.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("pairings", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    if (items.GetArrayLength() == 0) throw new InvalidOperationException("没有等待确认的配对请求。请先在安卓端点选此电脑，再输入它显示的配对码。");
                    if (items.GetArrayLength() > 1) throw new InvalidOperationException("有多个设备同时请求配对，请先取消其他请求后重试。");
                    pairingId = items[0].GetProperty("id").GetString();
                }
            }
        }
        using (var csrf = await http.GetAsync("api/csrf-token", cancellationToken))
        {
            if (csrf.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("csrf_token", out var token))
                    http.DefaultRequestHeaders.Add("X-CSRF-Token", token.GetString());
            }
        }
        var payload = new Dictionary<string, string?> { ["pin"] = pin, ["name"] = name };
        if (pairingId is not null) payload["pairing_id"] = pairingId;
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("api/pin", body, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("配对确认失败，请确认配对码和所选模式，再重试。");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!result.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("没有匹配的配对请求，或配对码已过期，请在安卓端重新发起。");
    }

    private async Task<HttpClient> CreateClientAsync(string profile, CancellationToken cancellationToken, byte[]? publicCertificate = null)
    {
        // The profile keeps its public certificate and private key in separate
        // PEM files. CreateFromPemFile(certPath) also searches that same file for
        // a private key; TLS pinning only needs the public certificate.
        var expected = SHA256.HashData(publicCertificate ?? await profiles.ReadMediaServerCertificateAsync(profile, cancellationToken));
        var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented is not null &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented.RawData), expected)
        };
        // Sunshine waits up to its default 10-second ping timeout for the client
        // to finish the pairing handshake before returning POST /api/pin.
        var http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{ProfileStore.Port(profile) + 1}/"), Timeout = TimeSpan.FromSeconds(15) };
        var credentials = await profiles.ReadCredentialsAsync(profile, cancellationToken);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Username + ":" + credentials.Password)));
        http.DefaultRequestHeaders.Add("Origin", http.BaseAddress.GetLeftPart(UriPartial.Authority));
        return http;
    }
}
