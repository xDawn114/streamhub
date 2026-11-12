using System.Security.Cryptography;
using System.Text.Json;

namespace StreamHub.Packaging;

/// <summary>The installer and static verifier use the same embedded build lock.</summary>
internal static class EngineIntegrity
{
    public static string ExpectedHash(Stream record)
    {
        using var json = JsonDocument.Parse(record);
        var hash = json.RootElement.GetProperty("binarySha256").GetString();
        if (hash is not { Length: 64 } || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("原生引擎构建记录缺少有效SHA256。");
        return hash;
    }

    public static void Verify(Stream binary, string expected, string profile)
    {
        if (!Convert.ToHexString(SHA256.HashData(binary)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("内置串流组件版本校验失败：" + profile);
    }
}
