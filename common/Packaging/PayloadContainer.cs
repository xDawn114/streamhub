using System.IO.Compression;
using SharpCompress.Archives.SevenZip;

namespace StreamHub.Packaging;

// Keep the validated ZIP layout inside an LZMA2 envelope. The temporary ZIP is
// seekable and deleted on close, including when decoding or validation fails.
public sealed class PayloadContainer : IDisposable
{
    public const string EntryName = "payload.zip";
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    private readonly FileStream? _expanded;
    public ZipArchive Archive { get; }

    private PayloadContainer(ZipArchive archive, FileStream? expanded)
    { Archive = archive; _expanded = expanded; }

    public static PayloadContainer Open(Stream input)
    {
        if (!input.CanSeek) throw new InvalidDataException("Payload must be seekable.");
        input.Position = 0;
        Span<byte> signature = stackalloc byte[6];
        input.ReadExactly(signature);
        input.Position = 0;
        if (!signature.SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }))
            return new(new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true), null);

        using var compressed = SevenZipArchive.OpenArchive(input);
        var entries = compressed.Entries.ToArray();
        if (entries.Length != 1 || entries[0].Key != EntryName || entries[0].IsDirectory ||
            entries[0].IsEncrypted || entries[0].Size is <= 0 or > MaximumExpandedBytes)
            throw new InvalidDataException("Invalid LZMA2 payload envelope.");
        var expanded = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
            FileOptions.DeleteOnClose);
        try
        {
            using var source = entries[0].OpenEntryStream();
            var buffer = new byte[128 * 1024];
            long total = 0;
            for (int count; (count = source.Read(buffer)) > 0;)
            {
                total += count;
                if (total > entries[0].Size) throw new InvalidDataException("Payload exceeds declared size.");
                expanded.Write(buffer, 0, count);
            }
            if (total != entries[0].Size) throw new InvalidDataException("Incomplete payload.");
            expanded.Position = 0;
            return new(new ZipArchive(expanded, ZipArchiveMode.Read, leaveOpen: true), expanded);
        }
        catch { expanded.Dispose(); throw; }
    }

    public void Dispose()
    {
        try { Archive.Dispose(); }
        finally { _expanded?.Dispose(); }
    }
}
