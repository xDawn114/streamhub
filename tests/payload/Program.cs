using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using StreamHub.Packaging;

if (args.Length is < 1 or > 2) throw new ArgumentException("artifact-root [7z.exe]");
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void Reject(Action action, string label)
{
    try { action(); throw new Exception("Expected rejection: " + label); }
    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException) { Check(true, label); }
}
void Inspect(string path) { using var input = File.OpenRead(path); using var container = PayloadContainer.Open(input); PayloadArchive.Open(container.Archive).VerifyFiles(); }
string Fixture(string name, Action<ZipArchive> mutate)
{
    var path = Path.Combine(root, name + ".zip"); File.Copy(Path.Combine(root, "dedup.zip"), path);
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) mutate(zip);
    return path;
}
void TextEntry(ZipArchive zip, string path, string text)
{
    zip.GetEntry(path)?.Delete(); using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(text);
}
const string remote = "components/streaming/remote/";
const string tablet = "components/streaming/tablet/";
var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
var bytes = new byte[128 * 1024]; RandomNumberGenerator.Fill(bytes);
foreach (var profile in new[] { remote, tablet })
{
    var directory = Path.Combine(stage, profile); Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "sunshine.exe"), bytes);
    File.WriteAllText(Path.Combine(directory, "display_device.state"), "profile-specific-state");
    File.WriteAllText(Path.Combine(directory, "sunshine.conf"), "profile-specific-config");
}
var manifest = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Select(p => new
{ path = Path.GetRelativePath(stage, p).Replace('\\', '/'), size = new FileInfo(p).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray();
File.WriteAllText(Path.Combine(stage, PayloadArchive.ManifestName), JsonSerializer.Serialize(new { version = "fixture", files = manifest }));
var dedup = Path.Combine(root, "dedup.zip"); var legacy = Path.Combine(root, "legacy.zip");
var packed = PayloadArchive.Create(stage, dedup); PayloadArchive.Create(stage, legacy, false);
Check(packed.Aliases == 1 && packed.OmittedBytes == bytes.Length, "only immutable mirrored engine omitted; config and state remain physical");
Inspect(dedup); Inspect(legacy); Check(true, "legacy and deduplicated archives both verify");
Check(new FileInfo(dedup).Length < new FileInfo(legacy).Length - bytes.Length / 2, "compressed archive actually gets smaller");
if (args.Length == 2)
{
    string Envelope(string name, string inner)
    {
        var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        File.Copy(inner, Path.Combine(folder, PayloadContainer.EntryName));
        var output = Path.Combine(root, name + ".7z");
        var start = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var argument in new[] { "a", "-t7z", "-m0=LZMA2", "-bso0", "-bsp0", output, PayloadContainer.EntryName }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Fixture compression failed.");
        return output;
    }
    var solid = Envelope("solid", dedup);
    using (var input = File.OpenRead(solid))
    using (var container = PayloadContainer.Open(input))
    {
        var payload = PayloadArchive.Open(container.Archive); payload.VerifyFiles();
        var expanded = Path.Combine(root, "solid-expanded"); payload.Extract(expanded);
        foreach (var file in manifest)
            Check(File.ReadAllBytes(Path.Combine(expanded, file.path)).SequenceEqual(File.ReadAllBytes(Path.Combine(stage, file.path))), "LZMA2 bytes match: " + file.path);
        Check(payload.AliasCount == packed.Aliases, "LZMA2 retains engine alias validation");
    }
    var damaged = Envelope("solid-tamper", Fixture("solid-tamper-inner", z => TextEntry(z, "unexpected.exe", "x")));
    Reject(() => Inspect(damaged), "LZMA2 does not bypass inner manifest validation");
    var truncated = Path.Combine(root, "truncated.7z");
    var compressed = File.ReadAllBytes(solid); File.WriteAllBytes(truncated, compressed[..^20]);
    try { Inspect(truncated); throw new Exception("Truncated archive accepted."); }
    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or SharpCompress.Common.ArchiveException) { Check(true, "truncated LZMA2 rejected"); }
}
using (var zip = ZipFile.OpenRead(dedup))
{
    var payload = PayloadArchive.Open(zip); var expanded = Path.Combine(root, "expanded"); payload.Extract(expanded);
    Check(Directory.EnumerateFiles(expanded, "*", SearchOption.AllDirectories).Count() == manifest.Length + 1, "map is packaging-only; installed file set unchanged");
    foreach (var file in manifest) Check(File.ReadAllBytes(Path.Combine(expanded, file.path)).SequenceEqual(File.ReadAllBytes(Path.Combine(stage, file.path))), "expanded bytes match: " + file.path);
    File.WriteAllText(Path.Combine(expanded, tablet, "sunshine.exe"), "changed");
    Check(File.ReadAllBytes(Path.Combine(expanded, remote, "sunshine.exe")).SequenceEqual(bytes), "editing tablet engine does not change remote engine");
    Reject(() => payload.Extract(expanded), "existing destination cannot be overwritten");
}
Reject(() => Inspect(Fixture("zip-slip", z => TextEntry(z, "../outside", "x"))), "ZIP path traversal rejected");
Reject(() => Inspect(Fixture("duplicate", z => { using var w = new StreamWriter(z.CreateEntry(remote.ToUpperInvariant() + "SUNSHINE.EXE").Open()); w.Write("x"); })), "case-insensitive duplicate entry rejected");
Reject(() => Inspect(Fixture("missing-source", z => z.GetEntry(remote + "sunshine.exe")!.Delete())), "missing alias source rejected");
Reject(() => Inspect(Fixture("tamper", z => { using var w = z.GetEntry(remote + "sunshine.exe")!.Open(); w.Write(new byte[bytes.Length]); })), "same-size source tampering rejected");
Reject(() => Inspect(Fixture("unlisted", z => TextEntry(z, "unexpected.exe", "x"))), "unlisted payload file rejected");
Reject(() => Inspect(Fixture("outside-alias", z => TextEntry(z, PayloadArchive.MapName, JsonSerializer.Serialize(new { format = 1, aliases = new[] { new { target = "../escape", source = remote + "sunshine.exe" } } })))), "alias traversal rejected");
Reject(() => Inspect(Fixture("mutable-alias", z => { z.GetEntry(tablet + "display_device.state")!.Delete(); TextEntry(z, PayloadArchive.MapName, JsonSerializer.Serialize(new { format = 1, aliases = new[] { new { target = tablet + "display_device.state", source = remote + "display_device.state" } } })); })), "mutable display-state alias rejected");
Reject(() => Inspect(Fixture("cycle", z => TextEntry(z, PayloadArchive.MapName, JsonSerializer.Serialize(new { format = 1, aliases = new[] { new { target = tablet + "sunshine.exe", source = tablet + "sunshine.exe" } } })))), "alias chains/cycles rejected");
Reject(() => Inspect(Fixture("symlink", z => z.GetEntry(remote + "sunshine.exe")!.ExternalAttributes = unchecked((int)0xA1FF0000))), "symbolic ZIP entry rejected");
foreach (var name in new[] { "/absolute", "C:/absolute", "folder/CON", "a./b", "folder/x:stream", "a//b" })
    Reject(() => PayloadArchive.ValidatePath(name), "unsafe Windows path rejected: " + name);
File.WriteAllText(Path.Combine(stage, "unlisted.tmp"), "unexpected");
Reject(() => PayloadArchive.Create(stage, Path.Combine(root, "unlisted-stage.zip")), "packager refuses unlisted staged files instead of silently discarding them");
File.Delete(Path.Combine(stage, "unlisted.tmp"));
File.WriteAllText(Path.Combine(stage, tablet, "sunshine.exe"), "different");
Reject(() => PayloadArchive.Create(stage, Path.Combine(root, "stale.zip")), "packager checks omitted bytes against manifest before deduplication");
Console.WriteLine($"PASS {checks} payload checks");
