using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace StreamHub.Updates;

public sealed class VerifiedUpdate(string path, FileStream lockedFile) : IDisposable
{
    public string Path { get; } = path;
    // The open handle prevents replacement between verification and ShellExecute.
    public void Dispose() => lockedFile.Dispose();
}

public sealed class UpdateClient(UpdateTrust trust, HttpClient http,
    Func<string, string[], CancellationToken, Task> verifyPublisher)
{
    private UpdateManifest? _accepted;
    public async Task<UpdateManifest> CheckAsync(string channel, string version, int code, CancellationToken token)
    {
        _accepted=null;
        if (!UpdatePolicy.QueryConfigured(trust)) throw new InvalidOperationException("更新发布源尚未配置。当前包仍需手动安装。");
        using var request = new HttpRequestMessage(HttpMethod.Get, trust.Feed);
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        RequireCompleteResponse(response);
        RequireOriginalUrl(response, trust.Feed);
        if (response.Content.Headers.ContentLength > UpdatePolicy.MaximumManifestBytes) throw new IOException("更新清单过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > UpdatePolicy.MaximumManifestBytes) throw new IOException("更新清单过大。");
            output.Write(buffer, 0, read);
        }
        return _accepted = UpdatePolicy.Read(output.GetBuffer().AsSpan(0, (int)output.Length), trust, channel, version, code, DateTimeOffset.UtcNow);
    }
    public async Task<string> DownloadAsync(UpdateManifest manifest, string cacheRoot, IProgress<int>? progress, CancellationToken token)
    {
        if (!UpdatePolicy.Configured(trust)) throw new InvalidOperationException("可检查新版本；Windows 安装包的可信发布者尚未配置，暂不能下载并安装。");
        RequireAccepted(manifest);
        var directory = System.IO.Path.Combine(System.IO.Path.GetFullPath(cacheRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var part = System.IO.Path.Combine(directory, "update.part"); var path = System.IO.Path.Combine(directory, "StreamHub-update.exe");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            RequireCompleteResponse(response);
            RequireOriginalUrl(response, manifest.Url);
            if (response.Content.Headers.ContentLength is long size && size != manifest.Size) throw new IOException("更新包大小与签名清单不符。");
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[64 * 1024]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += read;
                    if (total > manifest.Size || total > UpdatePolicy.MaximumPackageBytes) throw new IOException("更新包超过签名清单大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report((int)(100 * total / manifest.Size));
                }
                if (total != manifest.Size) throw new IOException("更新包下载不完整，请重试。");
                await output.FlushAsync(token);
            }
            File.Move(part, path);
            using var verified = await OpenVerifiedAsync(manifest, path, token);
            return path;
        }
        catch
        {
            // Only files created in this operation's unique directory are removed.
            if (File.Exists(part)) File.Delete(part);
            if (File.Exists(path)) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            throw;
        }
    }
    public async Task<VerifiedUpdate> OpenVerifiedAsync(UpdateManifest manifest, string path, CancellationToken token)
    {
        if (!UpdatePolicy.Configured(trust)) throw new InvalidOperationException("Windows 安装包的可信发布者尚未配置，已停止安装。");
        RequireAccepted(manifest);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        try
        {
            if (stream.Length != manifest.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token))
                .Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新包校验失败，已停止安装。");
            await verifyPublisher(path, trust.PublisherSha256, token);
            return new VerifiedUpdate(path, stream);
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    private void RequireAccepted(UpdateManifest manifest)
    {
        if (!ReferenceEquals(manifest, _accepted) || manifest.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("请重新检查更新并验证签名清单。");
    }
    private static void RequireOriginalUrl(HttpResponseMessage response, string expected)
    {
        if (response.RequestMessage?.RequestUri != new Uri(expected))
            throw new IOException("更新源发生了重定向，已停止更新。");
    }
    private static void RequireCompleteResponse(HttpResponseMessage response)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new HttpRequestException("更新源未返回完整数据，请重试。", null, response.StatusCode);
    }
}
