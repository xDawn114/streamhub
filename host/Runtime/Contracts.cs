using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamHub.Host.Runtime;

public static class RuntimeProtocol
{
    public const string PipeName = "StreamHub.Host.v1";
    public const string ServiceName = "StreamHubHost";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record RuntimeRequest(string Command, string? Profile = null, string? Pin = null, string? Name = null,
    int? Orientation = null, bool? AutoOrientation = null, string? DisplayOutput = null, string? ControlIdentity = null,
    int? Width = null, int? Height = null, int? RefreshRate = null,
    string? LeaseId = null, string? ClientId = null, string? RequestId = null, bool? StreamReady = null,
    bool? FastPrivacyHeartbeat = null,
    string? FilePath = null, bool? TransferEnabled = null,
    string? ClipboardAction = null, string? ClipboardFormat = null, string? ClipboardData = null, string? ClipboardError = null, bool? ClipboardSuccess = null,
    string? ClientCertificate = null, string? Provider = null, string? ServerUrl = null, string? ManualAddresses = null,
    bool? PrivacyEnabled = null, bool? BrowseEnabled = null, bool? BoundedPrivacyHeartbeat = null, string? BrowseRootId = null, bool? UploadAllowed = null,
    bool? DeleteAllowed = null, bool? LockOnExit = null, string? ExitReason = null,
    string? LoginSecret = null, bool? LoginSubmit = null);

public sealed record MediaIdentityRequest(string Mode, string ClientCertificate);
public sealed record MediaIdentityResult([property: JsonRequired] bool Paired, [property: JsonRequired] string? ServerCertificate);

public static class MediaIdentityPolicy
{
    public const int MaximumCertificateBytes = 4096;
    public const int MaximumCertificateCharacters = ((MaximumCertificateBytes + 2) / 3) * 4;

    // The pipe boundary validates independently of the bound-device HTTP API.
    // Accept one public X.509 DER certificate, never PEM, PFX or a chain.
    public static byte[] ParseCertificate(string? mode, string? encoded)
    {
        if (mode is not ("remote" or "tablet")) throw new ArgumentException("模式只能为 remote 或 tablet。");
        if (encoded is not { Length: > 0 and <= MaximumCertificateCharacters })
            throw new ArgumentException("媒体客户端证书格式或大小无效。");
        try
        {
            var der = Convert.FromBase64String(encoded);
            if (der.Length is 0 or > MaximumCertificateBytes || Convert.ToBase64String(der) != encoded ||
                X509Certificate2.GetCertContentType(der) != X509ContentType.Cert)
                throw new CryptographicException();
            using var certificate = new X509Certificate2(der);
            if (certificate.HasPrivateKey || !der.AsSpan().SequenceEqual(certificate.RawData))
                throw new CryptographicException();
            return der;
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        { throw new ArgumentException("媒体客户端证书必须为单个公开 X.509 证书的 Base64 DER。"); }
    }
}

public sealed record ClipboardPayload(string Format, string Data);
public sealed record ClipboardJob(string Id, string Mode, string LeaseId, string ClientId, string Action, ClipboardPayload? Payload, DateTimeOffset ExpiresAt);

public sealed record TabletDisplayRequest(int Width, int Height, int RefreshRate, int Orientation = 0);
public sealed record TabletDisplayMode(int Width, int Height, int RefreshRate, int Orientation, bool Degraded = false);
public sealed record TabletReceiver(string ClientId, string Name, int Width, int Height, int RefreshRate,
    int[] Orientations, DateTimeOffset LastSeen);
public sealed record TabletConnectionRequest(string Id, string ClientId, string State, int Orientation,
    string Link, DateTimeOffset ExpiresAt, string Message);
public sealed record ReceiverSnapshot(TabletReceiver[] Receivers, TabletConnectionRequest? Request);

public sealed record RuntimeResponse(bool Success, string Message, RuntimeStatus? Status = null, string? PayloadJson = null,
    int? ErrorStatus = null, Dictionary<string, long>? TimingsMs = null);

public sealed record ControlApiStatus(bool Running, bool IdentityPresent, string? Invitation, string? LastError);

public sealed record EngineStatus(string Profile, bool Running, bool Desired, int? ProcessId, int Port, bool Healthy, string Message,
    bool Streaming = false, bool SessionVerified = false, bool BackendBusy = false, bool DesktopCleanupPending = false);

public sealed record NetworkEngineStatus(bool Running, bool Desired, int? ProcessId, string Message);

public sealed record RuntimeStatus(string DeviceId, string DeviceName, EngineStatus Remote, EngineStatus Tablet,
    NetworkEngineStatus Network, string Version, string DataDirectory, DateTimeOffset StartedAt,
    bool PrivacyScreenEnabled = false, bool PrivacyScreenActive = false,
    string PrivacyScreenMessage = "远控隐私保护已关闭");
