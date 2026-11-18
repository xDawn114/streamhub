using System.Reflection;
using System.Runtime.CompilerServices;
using StreamHub.Host.Runtime;
using StreamHub.Installer;
using StreamHub.Service;
using StreamHub.Updates;

var root = Path.GetFullPath(args.Single());
var scratch = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp")) + Path.DirectorySeparatorChar;
if (!root.StartsWith(scratch, StringComparison.OrdinalIgnoreCase) ||
    Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new Exception("New workspace fixtures only");
Directory.CreateDirectory(root);
int checks = 0, fixtures = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task<ProfileStore> Profile(string profile, string? config)
{
    var folder = Path.Combine(root, (++fixtures).ToString());
    var store = new ProfileStore(folder, folder);
    Directory.CreateDirectory(store.DirectoryFor(profile));
    // An initialized synthetic state prevents invoking credentials or an EXE.
    await File.WriteAllTextAsync(Path.Combine(store.DirectoryFor(profile), "state.json"), "{\"username\":\"streamhub\"}");
    if (config is not null) await File.WriteAllTextAsync(store.ConfigFor(profile), config);
    return store;
}
foreach (var name in new[] { "remote", "tablet" })
{
    var fresh = await Profile(name, null);
    Check(await fresh.ReadEncoderProbeAsync(name, default) == "sdr-auto", "new profile defaults match prewarm negotiation " + name);
    await fresh.EnsureAsync(name, default);
    var text = await File.ReadAllTextAsync(fresh.ConfigFor(name));
    Check(text.Contains("streamhub_persist_probe = enabled") && text.Contains("streamhub_probe_plan = sdr-auto"), "fresh generated profile enables cache " + name);
    await fresh.PrepareEncoderCacheAsync(name, default);
    Check(await File.ReadAllTextAsync(fresh.ConfigFor(name)) == text, "fresh setup is byte idempotent " + name);

    const string legacy = "# keep user choices\nencoder = quicksync\noutput_name = chosen-output\ncustom_setting = keep\n";
    var old = await Profile(name, legacy);
    await old.PrepareEncoderCacheAsync(name, default);
    var migrated = await File.ReadAllTextAsync(old.ConfigFor(name));
    Check(migrated.Replace("\r\n", "\n").StartsWith(legacy) && migrated.Contains("streamhub_persist_probe = enabled"), "upgrade retains encoder, output and custom values " + name);
    await old.PrepareEncoderCacheAsync(name, default);
    Check(await File.ReadAllTextAsync(old.ConfigFor(name)) == migrated, "upgrade does not rewrite an already prepared identity " + name);
    foreach (var plan in new[] { "full", "sdr-hevc", "sdr-h264", "sdr-av1" })
    {
        var explicitSettings = $"streamhub_probe_plan = {plan}\nstreamhub_persist_probe = disabled\nencoder = nvenc\n";
        var custom = await Profile(name, explicitSettings);
        await custom.PrepareEncoderCacheAsync(name, default);
        Check(await File.ReadAllTextAsync(custom.ConfigFor(name)) == explicitSettings, "explicit plan and opt-out stay byte identical " + name + "/" + plan);
    }
    var duplicate = await Profile(name, "streamhub_persist_probe = enabled\nSTREAMHUB_PERSIST_PROBE = disabled\n");
    var original = await File.ReadAllBytesAsync(duplicate.ConfigFor(name));
    try { await duplicate.PrepareEncoderCacheAsync(name, default); throw new Exception("duplicate accepted"); }
    catch (InvalidOperationException) { Check(original.SequenceEqual(await File.ReadAllBytesAsync(duplicate.ConfigFor(name))), "duplicate rejection precedes any write " + name); }
}

// Exercise the real stopped-engine negotiation, not a mirrored decision helper.
var store = await Profile("tablet", "streamhub_probe_plan = sdr-auto\nstreamhub_persist_probe = enabled\n");
var runtime = (HostRuntime)RuntimeHelpers.GetUninitializedObject(typeof(HostRuntime));
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
typeof(HostRuntime).GetField("_profiles", flags)!.SetValue(runtime, store);
var engineType = typeof(HostRuntime).Assembly.GetType("StreamHub.Service.Engine")!;
var engine = Activator.CreateInstance(engineType, "tablet", false)!;
var prepare = typeof(HostRuntime).GetMethod("PrepareEncoderProbeAsync", flags)!;
var before = await File.ReadAllBytesAsync(store.ConfigFor("tablet"));
await (Task)prepare.Invoke(runtime, [engine, "sdr-hevc", CancellationToken.None])!;
Check(before.SequenceEqual(await File.ReadAllBytesAsync(store.ConfigFor("tablet"))), "narrower hint retains stopped-engine cache fingerprint");
await (Task)prepare.Invoke(runtime, [engine, "full", CancellationToken.None])!;
Check(await store.ReadEncoderProbeAsync("tablet", default) == "full", "HDR/full request widens only a stopped engine");
var full = await File.ReadAllBytesAsync(store.ConfigFor("tablet"));
await (Task)prepare.Invoke(runtime, [engine, "sdr-auto", CancellationToken.None])!;
Check(full.SequenceEqual(await File.ReadAllBytesAsync(store.ConfigFor("tablet"))), "explicit full plan remains compatible without a rewrite");

var idle = new EngineStatus("remote", true, true, 123, 48989, true, "ready", EncoderCacheState: "ready");
var status = new RuntimeStatus("fixture", "fixture", idle, idle with { Profile = "tablet", Desired = false },
    new(false, false, null, "disabled"), "fixture", root, DateTimeOffset.UtcNow);
Check(EncoderPrewarm.Outcome(status) == "ready", "only native verified cache authorizes prewarm ready");
Check(EncoderPrewarm.Outcome(status with { ConnectionsPaused = true }) == "skipped-paused", "pause is respected despite a healthy engine");
Check(EncoderPrewarm.Outcome(status with { Remote = idle with { Desired = false } }) == "skipped-paused", "explicitly stopped remote is not resumed");
foreach (var remote in new[] { idle with { Streaming = true }, idle with { BackendBusy = true }, idle with { BackendReleasePending = true } })
    Check(EncoderPrewarm.Outcome(status with { Remote = remote }) == "deferred-active-session", "active or ending session is not used as prewarm proof");
foreach (var state in new[] { "disabled", "not-applicable", "unavailable", "unknown" })
    Check(EncoderPrewarm.Outcome(status with { Remote = idle with { EncoderCacheState = state } }) != "ready", "healthy API is not cached startup proof: " + state);
Check(EncoderPrewarm.Outcome(status with { Remote = idle with { Healthy = false } }) is null, "wait for startup rather than stale ready metadata");
int calls = 0;
var ready = await EncoderPrewarm.WaitAsync(_ => { calls++; return Task.FromResult(new RuntimeResponse(true, "ok", status)); }, TimeSpan.FromSeconds(1));
Check(ready == "ready" && calls == 1, "completed startup does not repeat probing");
var pending = await EncoderPrewarm.WaitAsync(_ => Task.FromResult(new RuntimeResponse(false, "not yet")), TimeSpan.FromMilliseconds(30));
Check(pending == "deferred-timeout", "missing service or desktop defers within a bounded budget");
var never = new TaskCompletionSource<RuntimeResponse>();
Check(await EncoderPrewarm.WaitAsync(_ => never.Task, TimeSpan.FromMilliseconds(30)) == "deferred-timeout", "transport ignoring cancellation cannot extend installation wait");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await EncoderPrewarm.WaitAsync(_ => never.Task, TimeSpan.FromSeconds(1), cancelled.Token); throw new Exception("cancel lost"); }
catch (OperationCanceledException) { Check(true, "caller cancellation is preserved"); }
Check(!Directory.EnumerateFiles(root, "*.new", SearchOption.AllDirectories).Any(), "no incomplete configuration writes remain");
// The installer writes this trace before querying the engine. Exercise the
// actual shared serializer together with every outcome from the prewarm code.
var traceId = Guid.NewGuid().ToString("N");
var tracePath = Path.Combine(root, InstallTrace.FileName(traceId));
var now = DateTimeOffset.UtcNow;
var outcomes = new HashSet<string> { "wait", ready, pending, "deferred-error" };
foreach (var value in new[] { status, status with { ConnectionsPaused = true },
    status with { Remote = idle with { Streaming = true } } })
    if (EncoderPrewarm.Outcome(value) is { } outcome) outcomes.Add(outcome);
foreach (var state in new[] { "disabled", "not-applicable", "unavailable", "unknown" })
    if (EncoderPrewarm.Outcome(status with { Remote = idle with { EncoderCacheState = state } }) is { } outcome) outcomes.Add(outcome);
foreach (var outcome in outcomes)
{
    var phase = "encoder-prewarm-" + outcome;
    InstallTrace.Save(tracePath, new(traceId, 178, Environment.ProcessId, phase, now, now, 1));
    Check(InstallTrace.Read(tracePath, traceId, 178, now)?.Phase == phase, "installer persists real prewarm stage " + outcome);
}
try { InstallTrace.Save(tracePath, new(traceId, 178, Environment.ProcessId, "encoder-prewarm-unrecognized", now, now, 1)); throw new Exception("unknown stage accepted"); }
catch (InvalidOperationException) { Check(true, "unknown prewarm stage still fails closed"); }
Console.WriteLine($"{checks} encoder cache and installer prewarm checks passed; isolated files and injected status only.");
