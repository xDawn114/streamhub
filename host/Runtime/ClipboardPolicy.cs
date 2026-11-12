using System.Text;

namespace StreamHub.Host.Runtime;

public static class ClipboardPolicy
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumEncodedChars = ((MaximumBytes + 2) / 3) * 4;
    public const int MaximumEnvelopeBytes = MaximumEncodedChars + 4096;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static readonly System.Text.Json.JsonSerializerOptions WireJsonOptions = new(RuntimeProtocol.JsonOptions)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Decode(ClipboardPayload payload)
    {
        if (payload.Format is not ("text/plain" or "text/html" or "image/png") ||
            string.IsNullOrEmpty(payload.Data) || payload.Data.Length > MaximumEncodedChars)
            throw new InvalidOperationException("剪贴板格式或内容无效，单项最多 1 MiB。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload.Data); }
        catch (FormatException) { throw new InvalidOperationException("剪贴板内容编码无效。"); }
        if (bytes.Length is < 1 or > MaximumBytes || Convert.ToBase64String(bytes) != payload.Data)
            throw new InvalidOperationException("剪贴板内容无效，单项最多 1 MiB。");
        if (payload.Format == "image/png")
        {
            if (bytes.Length < 24 || !bytes.AsSpan().StartsWith(PngSignature) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidOperationException("剪贴板图片不是有效的 PNG。");
            var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width is 0 or > 16384 || height is 0 or > 16384 || (long)width * height > 40000000)
                throw new InvalidOperationException("剪贴板图片尺寸过大。");
        }
        else
        {
            try { _ = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("剪贴板文字编码无效。"); }
        }
        return bytes;
    }

    public static ClipboardPayload Encode(string format, byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw new InvalidOperationException("剪贴板内容无效，单项最多 1 MiB。");
        var payload = new ClipboardPayload(format, Convert.ToBase64String(bytes));
        _ = Decode(payload);
        return payload;
    }
}
