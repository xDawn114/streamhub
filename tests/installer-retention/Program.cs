using System.Text.Json;
using StreamHub.Installer;

if (args.Length != 1) throw new ArgumentException("An isolated fixture root is required.");
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var passed = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    passed.Add(message);
    Console.WriteLine("PASS " + message);
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error is IOException or JsonException) { Check(true, message); return; }
    throw new Exception("Expected rejection: " + message);
}
void Marker(string directory, string installed, string product = "StreamHub.PC.0b1e5f94")
{
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, ".streamhub-install.json"), JsonSerializer.Serialize(new
    { productId = product, version = "0.8.13", versionCode = 154, root = installed }));
    File.WriteAllText(Path.Combine(directory, "app.exe"), "isolated program fixture");
}
string Backup(string catalog, string name, string installed, DateTime created, string product = "StreamHub.PC.0b1e5f94")
{
    var directory = Path.Combine(catalog, name);
    var backup = Path.Combine(directory, "backup");
    Marker(backup, installed, product);
    Directory.SetCreationTimeUtc(directory, created);
    return backup;
}
DateTime At(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);
var install = Path.Combine(root, "installed");
Marker(install, install);
var namingRoot = Path.Combine(root, "naming");
var naming = new RollbackCatalog(namingRoot, install);
var now = new DateTimeOffset(2026, 11, 14, 15, 40, 12, 123, TimeSpan.FromHours(8));
var transaction = naming.CreateTransaction("9.9.9.999", now);
Check(transaction == "0.8.13.154-20261114T074012123Z", "snapshot names describe the backed-up version, build and UTC time");
Check(naming.CreateTransaction("9.9.9.999", now) == transaction + "-1", "same-millisecond installations reserve separate directories");
Marker(Path.Combine(namingRoot, transaction, "backup"), install);
var record = naming.Capture(transaction);
naming.Commit(record);
Check(naming.Read().Transaction == transaction, "readable names retain full-hash rollback verification");
var legacy = Guid.NewGuid().ToString("N");
Backup(namingRoot, legacy, install, At(1));
Check(naming.Verify(naming.Capture(legacy)).EndsWith(Path.Combine(legacy, "backup")), "legacy GUID rollback snapshots remain readable");
foreach (var invalid in new[] { "..\\escape", transaction + ":ads", transaction + "\\backup", "0.8.13-20260230T000000000Z", transaction + ".", new string('1', 100) })
    Reject(() => naming.Verify(record with { Transaction = invalid }), "unsafe or malformed transaction is rejected: " + invalid[..Math.Min(45, invalid.Length)]);

var retentionRoot = Path.Combine(root, "retention");
var retention = new RollbackCatalog(retentionRoot, install);
var oldGuid = Guid.NewGuid().ToString("N");
var old = Backup(retentionRoot, oldGuid, install, At(1));
var middleGuid = Guid.NewGuid().ToString("N");
var middle = Backup(retentionRoot, middleGuid, install, At(2));
var recent = "0.8.12.152-20260103T000000000Z";
var recentBackup = Backup(retentionRoot, recent, install, At(3));
var current = "0.8.13.154-20260104T000000000Z";
var currentBackup = Backup(retentionRoot, current, install, At(4));
retention.Commit(retention.Capture(current));
var unrelated = Backup(retentionRoot, "runner-unrelated", install, At(1));
var foreign = Backup(retentionRoot, Guid.NewGuid().ToString("N"), install, At(1), "AnotherProduct");
var anotherInstall = Backup(retentionRoot, Guid.NewGuid().ToString("N"), Path.Combine(root, "another-install"), At(1));
var sibling = Path.Combine(Path.GetDirectoryName(middle)!, "leave-this.txt");
File.WriteAllText(sibling, "unrelated transaction sibling");
var notes = new List<string>();
retention.Prune(notes.Add);
Check(!Directory.Exists(old) && !Directory.Exists(middle), "successful retention removes older GUID backups");
Check(!Directory.Exists(Path.GetDirectoryName(old)!), "empty retired transaction directories are removed");
Check(File.Exists(sibling), "cleanup removes only the backup subtree and preserves other transaction files");
Check(Directory.Exists(recentBackup) && Directory.Exists(currentBackup), "the newest two snapshots remain even when both exceed thirty days");
Check(Directory.Exists(unrelated) && Directory.Exists(foreign) && Directory.Exists(anotherInstall), "unrecognized names, other products and other installation roots are untouched");
Check(retention.Read().Transaction == current, "retention preserves a usable current rollback pointer");

var pinnedRoot = Path.Combine(root, "pinned");
var pinned = new RollbackCatalog(pinnedRoot, install);
var pinnedOld = Backup(pinnedRoot, "0.8.10.150-20260101T000000000Z", install, At(1));
var dropped = Backup(pinnedRoot, "0.8.11.151-20260102T000000000Z", install, At(2));
var kept = Backup(pinnedRoot, "0.8.12.152-20260103T000000000Z", install, At(3));
pinned.Commit(pinned.Capture(Path.GetFileName(Path.GetDirectoryName(pinnedOld))!));
pinned.Prune(notes.Add);
Check(Directory.Exists(pinnedOld) && Directory.Exists(kept) && !Directory.Exists(dropped), "the active rollback source is pinned even when its timestamp is oldest");

var corruptRoot = Path.Combine(root, "corrupt-pointer");
var corrupt = new RollbackCatalog(corruptRoot, install);
var untouched = Enumerable.Range(1, 3).Select(day => Backup(corruptRoot, Guid.NewGuid().ToString("N"), install, At(day))).ToArray();
File.WriteAllText(Path.Combine(corruptRoot, "latest-rollback.json"), "{interrupted");
corrupt.Prune(notes.Add);
Check(untouched.All(Directory.Exists), "an unreadable pointer prevents all automatic deletion");

var lockedRoot = Path.Combine(root, "locked");
var locked = new RollbackCatalog(lockedRoot, install);
var lockedOld = Backup(lockedRoot, "0.8.10.150-20260101T000000000Z", install, At(1));
Backup(lockedRoot, "0.8.11.151-20260102T000000000Z", install, At(2));
Backup(lockedRoot, current, install, At(4));
locked.Commit(locked.Capture(current));
using (var busy = new FileStream(Path.Combine(lockedOld, "app.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    locked.Prune(notes.Add);
    Check(File.Exists(Path.Combine(lockedOld, "app.exe")) && File.Exists(Path.Combine(lockedOld, ".streamhub-install.json"))
        && notes.Any(note => note.Contains("暂未清理")), "a busy old backup retains its ownership marker and does not fail the completed installation");
    locked.Prune(_ => throw new IOException("injected log write failure"));
    Check(locked.Read().Transaction == current, "cleanup logging failures cannot invalidate the committed rollback source");
}
locked.Prune(notes.Add);
Check(!Directory.Exists(lockedOld), "a previously busy backup can be cleaned automatically after its lock is released");

// Run.ps1 creates real NTFS junctions within this fixture tree without touching product paths.
var junctionRoot = Path.Combine(root, "junctions");
var junctions = new RollbackCatalog(junctionRoot, install);
var junctionOld = Backup(junctionRoot, "0.8.10.150-20260101T000000000Z", install, At(1));
var junctionPath = Path.Combine(junctionOld, "nested-link");
Check(Directory.Exists(junctionPath) && File.GetAttributes(junctionPath).HasFlag(FileAttributes.ReparsePoint), "the fixture contains a real directory junction");
Backup(junctionRoot, "0.8.11.151-20260102T000000000Z", install, At(2));
Backup(junctionRoot, current, install, At(4));
junctions.Commit(junctions.Capture(current));
junctions.Prune(notes.Add);
Check(Directory.Exists(junctionOld) && File.Exists(Path.Combine(root, "outside-junction", "sentinel.txt")), "linked backup contents and junction targets are never recursively deleted");
Check(junctions.Read().Transaction == current, "a skipped junction does not break the retained rollback source");

var failedRoot = Path.Combine(root, "failed-rollback");
var failed = new RollbackCatalog(failedRoot, install);
var snapshots = Enumerable.Range(1, 3).Select(day =>
    Backup(failedRoot, $"0.8.13.154-2026010{day}T000000000Z", install, At(day))).ToArray();
var restoreName = Path.GetFileName(Path.GetDirectoryName(snapshots[2]))!;
failed.Commit(failed.Capture(restoreName));
var reverse = Guid.NewGuid().ToString("N");
Reject(() =>
{
    RollbackTransaction.Execute(install, snapshots[2], Path.Combine(failedRoot, reverse, "backup"), () => { }, () => { },
        restored => { if (restored) throw new IOException("injected service activation failure"); });
    failed.Prune(notes.Add);
}, "activation failure skips post-success retention");
Check(snapshots.All(Directory.Exists) && failed.Read().Transaction == restoreName, "failed recovery preserves all prior backups and its previous pointer");
var forward = Guid.NewGuid().ToString("N");
RollbackRecord? forwardRecord = null;
RollbackTransaction.Execute(install, snapshots[2], Path.Combine(failedRoot, forward, "backup"), () => { },
    () => forwardRecord = failed.Capture(forward), restored => { if (restored) failed.Commit(forwardRecord!); });
failed.Prune(notes.Add);
Check(failed.Read().Transaction == forward && Guid.TryParseExact(forward, "N", out _), "successful recovery keeps the reverse pointer compatible with older uninstallers");
Check(Directory.EnumerateDirectories(failedRoot).Count(directory => Directory.Exists(Path.Combine(directory, "backup"))) == 2,
    "successful recovery also bounds the retained snapshot count");
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
{ passed = true, count = passed.Count, checks = passed, realServicesUsed = false, installedProgramChanged = false }));
Console.WriteLine($"{passed.Count} installer retention checks passed.");
