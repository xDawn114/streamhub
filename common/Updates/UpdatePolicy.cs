using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamHub.Updates;

public sealed record UpdateTrust(string Feed, string ManifestPublicKey, string[] PublisherSha256);
public sealed record UpdateEnvelope(string Payload, string Signature);
public sealed record UpdateManifest(int Schema, string Product, string Channel, string Architecture,
    string Version, int VersionCode, int MinimumInstalledCode, DateTimeOffset PublishedAt,
    DateTimeOffset ExpiresAt, string Url, long Size, string Sha256, string Notes);
public sealed class NoNewUpdateException() : InvalidOperationException("已经是当前渠道的最新版本。");

/// <summary>Trust is embedded at build time. The network cannot supply its own signing keys.</summary>
public static class UpdatePolicy
{
    public const int MaximumManifestBytes = 64 * 1024;
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static bool QueryConfigured(UpdateTrust trust) => Https(trust.Feed, out _) &&
        trust.ManifestPublicKey is { Length: > 64 and < 2048 };
    public static bool Configured(UpdateTrust trust) => QueryConfigured(trust) && trust.PublisherSha256 is { Length: > 0 and <= 8 } &&
        trust.PublisherSha256.All(IsHash);

    public static UpdateManifest Read(ReadOnlySpan<byte> envelope, UpdateTrust trust, string channel,
        string installedVersion, int installedCode, DateTimeOffset now)
    {
        if (!QueryConfigured(trust)) throw new InvalidOperationException("更新渠道尚未配置清单公钥和发布源。");
        if (envelope.Length is 0 or > MaximumManifestBytes) throw new InvalidOperationException("更新清单大小无效。");
        try
        {
            var signed = JsonSerializer.Deserialize<UpdateEnvelope>(envelope, Json) ?? throw new JsonException();
            var bytes = Convert.FromBase64String(signed.Payload);
            var signature = Convert.FromBase64String(signed.Signature);
            if (bytes.Length is 0 or > 32 * 1024 || signature.Length is < 256 or > 1024 ||
                Convert.ToBase64String(bytes) != signed.Payload || Convert.ToBase64String(signature) != signed.Signature)
                throw new CryptographicException();
            using var rsa = RSA.Create();
            var key = Convert.FromBase64String(trust.ManifestPublicKey);
            rsa.ImportSubjectPublicKeyInfo(key, out var used);
            if (used != key.Length || rsa.KeySize < 3072 ||
                !rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new CryptographicException();
            var value = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new JsonException();
            if (value.Schema != 1 || value.Product != "StreamHub" || value.Channel != channel ||
                value.Architecture != "win-x64" || !Version.TryParse(value.Version, out var target) || target.Build < 0 || target.Revision >= 0 ||
                !Version.TryParse(installedVersion, out var current) || target < current || value.VersionCode < installedCode ||
                value.VersionCode == installedCode && target != current ||
                value.MinimumInstalledCode < 1 || value.MinimumInstalledCode > installedCode ||
                value.Size is < 1 or > MaximumPackageBytes || !IsHash(value.Sha256) ||
                value.Notes is not { Length: <= 4096 } ||
                value.PublishedAt > now.AddMinutes(5) || value.ExpiresAt <= now || value.ExpiresAt <= value.PublishedAt ||
                value.ExpiresAt > value.PublishedAt.AddDays(90) || !Https(value.Url, out var package) ||
                !Https(trust.Feed, out var feed) || package!.GetLeftPart(UriPartial.Authority) != feed!.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("更新版本、渠道、有效期或下载地址不符合本机策略。");
            if(value.VersionCode==installedCode)throw new NoNewUpdateException();
            return value;
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { throw new InvalidOperationException("更新清单签名或格式无效，已停止更新。", error); }
    }
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Https(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
}
