using System.Text.Json;
using StreamHub.Installer;

if (args.Length != 1) throw new ArgumentException("Supply an isolated fixture directory.");
var fixture = Path.GetFullPath(args[0]);
Directory.CreateDirectory(fixture);
var tests = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    tests++;
    Console.WriteLine("PASS " + name);
}
void Reject(Action action, string expected, string name)
{
    try { action(); }
    catch (IOException error) { Check(error.Message.Contains(expected), name); return; }
    throw new Exception("Expected rejection: " + name);
}
string Service(string root) => $"\"{root}\\service\\串流中心后台.exe\" --root \"{root}\" --data \"C:\\ProgramData\\StreamHub\"";
var root = @"E:\Original App";
var clean = InstallationLocation.Resolve(null, null, _ => false);
Check(clean.Root is null && clean.Error is null, "fresh install");
Check(clean.SelectRoot(@"C:\Program Files\StreamHub", false) == @"C:\Program Files\StreamHub", "fresh default unchanged");
var existing = InstallationLocation.Resolve(root, Service(root), path => path == root);
Check(existing.CanRepair && existing.Root == root, "registry and service agree");
Check(existing.SelectRoot(@"C:\Program Files\StreamHub", false) == root, "reuse custom path including spaces");
Check(existing.SelectRoot(@"D:\Explicit", true) == @"D:\Explicit", "explicit destination preserved");
Reject(() => existing.ValidateDestination(@"D:\Explicit"), root, "explicit mismatch shows original location");
Check(InstallationLocation.Resolve(null, Service(root), _ => true).CanRepair, "service-only registration");
Check(InstallationLocation.Resolve(root, null, _ => true).CanRepair, "registry-only registration");
Check(InstallationLocation.Resolve(root.ToUpperInvariant() + "\\", Service(root), _ => true).CanRepair, "case and trailing separator");
var conflict = InstallationLocation.Resolve(@"D:\Other", Service(root), _ => true);
Check(conflict.Error?.Contains(root) == true && conflict.Error.Contains(@"D:\Other"), "conflicting registration names both paths");
Reject(() => conflict.ValidateDestination(root), "不一致", "conflict cannot install");
var incomplete = InstallationLocation.Resolve(root, Service(root), _ => false);
Check(incomplete.Root == root && incomplete.CanRepair && incomplete.Incomplete, "registered missing marker enables repair");
incomplete.ValidateDestination(root);
Check(true, "registered missing directory can be recreated");
Reject(() => incomplete.ValidateDestination(@"D:\Other"), root, "incomplete repair stays at registered location");
Check(InstallationLocation.Resolve(root, null, _ => false).CanRepair, "uninstall registration can recover missing service and marker");
Check(InstallationLocation.Resolve(null, Service(root), _ => false).CanRepair, "service registration can recover missing uninstall entry and marker");
Check(InstallationLocation.ServiceRoot(Service(root)) == root, "Windows quoted command parsing");
Check(InstallationLocation.ServiceRoot(Service(@"E:\BO")) == @"E:\BO", "letter O preserved");
Check(InstallationLocation.ServiceRoot(Service(@"E:\B0")) == @"E:\B0", "digit zero preserved");
Check(InstallationLocation.ServiceRoot($"\"{root}\\service\\串流中心后台.exe\"") == root, "legacy command without root option");
Check(InstallationLocation.ServiceRoot(Service(root).Replace("--root \"E:\\Original App\"", "--root \"D:\\Other\"")) is null, "executable and root must agree");
Check(InstallationLocation.ServiceRoot(Service(root) + " --root \"E:\\Original App\"") is null, "ambiguous duplicate root rejected");
Check(InstallationLocation.ServiceRoot(Service(root).Replace("后台.exe", "后台.exe.other")) is null, "executable substring cannot establish ownership");
Check(InstallationLocation.ServiceRoot("\"C:\\Other.exe\" --data " + Service(root)) is null, "path in unrelated arguments cannot establish ownership");
Check(InstallationLocation.Resolve(null, "", _ => true).Error is not null, "service with missing ImagePath rejected");
Check(InstallationLocation.Resolve("relative", null, _ => true).Error is not null, "invalid registry path rejected");
var empty = Path.Combine(fixture, "empty");
Directory.CreateDirectory(empty);
clean.ValidateDestination(empty);
Check(true, "empty directory accepted");
var occupied = Path.Combine(fixture, "occupied");
Directory.CreateDirectory(occupied);
File.WriteAllText(Path.Combine(occupied, "personal.txt"), "keep");
Reject(() => clean.ValidateDestination(occupied), occupied, "unrelated files protected with exact path");
var partial = InstallationLocation.Resolve(occupied, Service(occupied), _ => false);
partial.ValidateDestination(occupied);
Check(File.ReadAllText(Path.Combine(occupied, "personal.txt")) == "keep", "registered partial directory accepted without deleting files");
var marker = Path.Combine(empty, InstallationLocation.Marker);
foreach (var content in new[] { "{", "[]", "null", "{\"productId\":1}", "{\"productId\":\"other\"}" })
{
    File.WriteAllText(marker, content);
    Check(!InstallationLocation.HasProductMarker(empty), "invalid marker: " + content);
    Reject(() => clean.ValidateDestination(empty), empty, "invalid marker blocked: " + content);
}
File.WriteAllText(marker, JsonSerializer.Serialize(new { productId = InstallationLocation.ProductId }));
Check(InstallationLocation.HasProductMarker(empty), "product marker accepted");
clean.ValidateDestination(empty);
Check(true, "product directory can be repaired");

var overlay = Path.Combine(fixture, "overlay");
var source = Path.Combine(overlay, "source");
var target = Path.Combine(overlay, "target");
var backup = Path.Combine(overlay, "backup");
Directory.CreateDirectory(source);
Directory.CreateDirectory(target);
File.WriteAllText(Path.Combine(source, "app.exe"), "new program");
Directory.CreateDirectory(Path.Combine(source, "new-folder"));
File.WriteAllText(Path.Combine(source, "new-folder", "new.dll"), "new library");
File.WriteAllText(Path.Combine(target, "app.exe"), "old program");
File.WriteAllText(Path.Combine(target, "keep.txt"), "personal data");
File.WriteAllText(Path.Combine(target, "obsolete.dll"), "old library");
File.WriteAllText(Path.Combine(target, "changed.dll"), "user changed library");
File.WriteAllText(Path.Combine(target, "组件清单.json"), JsonSerializer.Serialize(new { files = new[] {
    new { path = "obsolete.dll", sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("old library"))) },
    new { path = "changed.dll", sha256 = new string('0', 64) }
}}));
var files = new InstallationFiles(target, source, backup);
files.Snapshot();
Check(!files.CanRollback, "unmarked partial backup cannot replace a working rollback pointer");
files.Apply();
File.WriteAllText(Path.Combine(target, InstallationLocation.Marker), "new marker");
Check(File.ReadAllText(Path.Combine(target, "app.exe")) == "new program", "existing program overwritten in place");
Check(File.ReadAllText(Path.Combine(target, "keep.txt")) == "personal data", "unlisted user file retained");
Check(!File.Exists(Path.Combine(target, "obsolete.dll")), "unchanged obsolete component removed");
Check(File.ReadAllText(Path.Combine(target, "changed.dll")) == "user changed library", "modified unlisted component retained");
Check(File.Exists(Path.Combine(target, "new-folder", "new.dll")), "new component installed");
files.Restore();
Check(File.ReadAllText(Path.Combine(target, "app.exe")) == "old program", "failed install restores overwritten program");
Check(File.Exists(Path.Combine(target, "obsolete.dll")), "failed install restores removed old component");
Check(!File.Exists(Path.Combine(target, InstallationLocation.Marker)) && !Directory.Exists(Path.Combine(target, "new-folder")), "partial repair rollback removes newly introduced marker and files");
Check(File.ReadAllText(Path.Combine(target, "keep.txt")) == "personal data", "failed repair preserves original unrelated file");

var busyFiles = new InstallationFiles(target, source, Path.Combine(overlay, "busy-backup"));
busyFiles.Snapshot();
using (var handle = new FileStream(Path.Combine(target, "app.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    var release = Task.Run(async () => { await Task.Delay(250); handle.Dispose(); });
    busyFiles.Apply();
    release.GetAwaiter().GetResult();
}
Check(File.ReadAllText(Path.Combine(target, "app.exe")) == "new program", "brief file lock retried automatically");
busyFiles.Restore();

var blockedFiles = new InstallationFiles(target, source, Path.Combine(overlay, "blocked-backup"));
blockedFiles.Snapshot();
using (var handle = new FileStream(Path.Combine(target, "app.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
    Reject(blockedFiles.Apply, "", "persistent file lock fails without deleting target");
blockedFiles.Restore();
Check(File.ReadAllText(Path.Combine(target, "app.exe")) == "old program", "persistent lock failure can restore original files");

var freshTarget = Path.Combine(overlay, "fresh-target");
var freshFiles = new InstallationFiles(freshTarget, source, Path.Combine(overlay, "fresh-backup"));
freshFiles.Snapshot();
freshFiles.Apply();
File.WriteAllText(Path.Combine(freshTarget, InstallationLocation.Marker), "new marker");
freshFiles.Restore();
Check(!Directory.Exists(freshTarget), "failed fresh installation removes only its own new files and directories");

File.WriteAllText(Path.Combine(target, "组件清单.json"), "{interrupted");
var damagedFiles = new InstallationFiles(target, source, Path.Combine(overlay, "damaged-backup"));
damagedFiles.Snapshot();
damagedFiles.Apply();
Check(File.Exists(Path.Combine(target, "obsolete.dll")), "damaged old manifest does not prevent repair or authorize removal");
damagedFiles.Restore();

var completeTarget = Path.Combine(overlay, "complete");
Directory.CreateDirectory(completeTarget);
File.WriteAllText(Path.Combine(completeTarget, "app.exe"), "old program");
File.WriteAllText(Path.Combine(completeTarget, InstallationLocation.Marker), JsonSerializer.Serialize(new { productId = InstallationLocation.ProductId }));
File.WriteAllText(Path.Combine(completeTarget, "组件清单.json"), JsonSerializer.Serialize(new { files = new[] {
    new { path = "app.exe", size = 11, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("old program"))) }
}}));
var completeFiles = new InstallationFiles(completeTarget, source, Path.Combine(overlay, "complete-backup"));
completeFiles.Snapshot();
Check(completeFiles.CanRollback, "verified complete snapshot may become rollback source");
File.WriteAllText(Path.Combine(completeTarget, "app.exe"), "changed program");
var changedFiles = new InstallationFiles(completeTarget, source, Path.Combine(overlay, "changed-backup"));
changedFiles.Snapshot();
Check(!changedFiles.CanRollback, "damaged old file cannot replace a working rollback pointer");
File.Delete(Path.Combine(completeTarget, "app.exe"));
var missingFiles = new InstallationFiles(completeTarget, source, Path.Combine(overlay, "missing-backup"));
missingFiles.Snapshot();
Check(!missingFiles.CanRollback, "missing old file cannot replace a working rollback pointer");
Console.WriteLine($"{tests} installer location checks passed.");
