using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

// The per-user copy is retained for upgrade recovery. The service copy lives below
// the installer-protected ProgramData directory and survives owner UI exit/logon.
public sealed record ControlApiIdentity(byte[] Pfx, byte[] Token)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StreamHub.ControlApi.Identity.v1");
    public static readonly string UserDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamHub", "control-api");

    public static string PathFor(string directory) => Path.Combine(directory, "identity.dpapi");

    public static ControlApiIdentity Read(string directory, DataProtectionScope scope)
    {
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(PathFor(directory)), Entropy, scope);
        try { return Decode(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static ControlApiIdentity Decode(ReadOnlySpan<byte> plain)
    {
        if (plain.Length > 16384) throw new CryptographicException("设备绑定身份过大。");
        var identity = JsonSerializer.Deserialize<ControlApiIdentity>(plain, RuntimeProtocol.JsonOptions)
            ?? throw new CryptographicException("设备绑定身份无效。");
        try
        {
            if (identity.Token is not { Length: 32 } || identity.Pfx is not { Length: > 0 and <= 12288 })
                throw new CryptographicException("设备绑定身份无效。");
            using var certificate = new X509Certificate2(identity.Pfx, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new CryptographicException("设备绑定证书已失效。");
            return identity;
        }
        catch { identity.Clear(); throw; }
    }

    public static ControlApiIdentity LoadOrCreateUser()
    {
        if (File.Exists(PathFor(UserDirectory))) return Read(UserDirectory, DataProtectionScope.CurrentUser);
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=StreamHub Device Binding", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var identity = new ControlApiIdentity(generated.Export(X509ContentType.Pfx), RandomNumberGenerator.GetBytes(32));
        try { identity.WriteNew(UserDirectory, DataProtectionScope.CurrentUser); return identity; }
        catch { identity.Clear(); throw; }
    }

    public void WriteNew(string directory, DataProtectionScope scope)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(this, RuntimeProtocol.JsonOptions);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".new");
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(plain, Entropy, scope));
            File.Move(temporary, PathFor(directory), false); // Never silently replace existing bindings.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear()
    {
        CryptographicOperations.ZeroMemory(Pfx);
        CryptographicOperations.ZeroMemory(Token);
    }
}
