using StreamHub.Packaging;
using System.Diagnostics;
using System.IO.Compression;

if (args.Length is < 2 or > 4 || args.Length == 3 && args[2] != "--legacy" ||
    args.Length == 4 && args[2] != "--lzma2")
    throw new ArgumentException("stage-directory archive.zip [--legacy] | stage-directory archive.7z --lzma2 7z.exe");
bool lzma2 = args.Length == 4;
string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "pack-" + Guid.NewGuid().ToString("N"));
if (lzma2) Directory.CreateDirectory(directory);
try
{
var zip = lzma2 ? Path.Combine(directory, PayloadContainer.EntryName) : args[1];
var result = PayloadArchive.Create(args[0], zip, args.Length != 3,
    lzma2 ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize);
if (lzma2)
{
    if (File.Exists(args[1])) throw new IOException("Use a new payload output path.");
    var start = new ProcessStartInfo(Path.GetFullPath(args[3])) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
    foreach (var argument in new[] { "a", "-t7z", "-m0=LZMA2", "-mx=9", "-md=64m", "-ms=on", "-mmt=2", "-bsp0", "-bso0", "-mtc=off", "-mta=off", Path.GetFullPath(args[1]), PayloadContainer.EntryName })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Cannot start 7-Zip.");
    process.WaitForExit();
    if (process.ExitCode != 0) throw new IOException("7-Zip failed: " + process.ExitCode);
}
Console.WriteLine($"Payload: {result.Aliases} duplicate files omitted, {result.OmittedBytes} uncompressed bytes; runtime directories remain separate.");
}
finally { if (lzma2 && Directory.Exists(directory)) Directory.Delete(directory, true); }
