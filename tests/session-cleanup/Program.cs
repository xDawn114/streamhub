using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StreamHub.Host.Runtime;
using StreamHub.Service;

var root = Path.GetFullPath(args.Single());
if (!root.StartsWith(@"D:\Deepseek\work\.tmp\", StringComparison.OrdinalIgnoreCase))
    throw new Exception("Fixtures must remain in host test scratch.");
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
async Task Error(Func<Task> operation, int status, string label)
{
    try { await operation(); throw new Exception(label + " unexpectedly succeeded"); }
    catch (BackendReleaseException e) { Check(e.Status == status && !e.Message.Contains("synthetic secret"), label); }
}
void Invalid(Action action, string label)
{
    try { action(); throw new Exception(label + " unexpectedly succeeded"); }
    catch (InvalidOperationException) { Check(true, label); }
}
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
HostRuntime Runtime(Fixture f, SemaphoreSlim? gate = null)
{
    var runtime = (HostRuntime)RuntimeHelpers.GetUninitializedObject(typeof(HostRuntime));
    void Set(string name, object value) => typeof(HostRuntime).GetField(name, flags)!.SetValue(runtime, value);
    Set("_gate", gate ?? new SemaphoreSlim(1, 1)); Set("_maintenance", new MaintenanceWindow());
    Set("_remoteSession", f.Session); Set("_remoteBackendRelease", f.Release);
    return runtime;
}
Task<RuntimeResponse> Send(HostRuntime runtime, RuntimeRequest request) =>
    (Task<RuntimeResponse>)typeof(HostRuntime).GetMethod("HandleAsync", flags)!.Invoke(runtime, [request, CancellationToken.None])!;

// No file mutation is needed by the cleanup path, including persistent probe data.
var cache = Path.Combine(root, "persistent-probe-cache.json");
File.WriteAllText(cache, "fixture-cache-preserve");
using (var f = new Fixture(root, ready: false))
{
    var engine = f.Engine;
    Check(f.Session.OwnsLease(Fixture.Lease, Fixture.Client, DateTimeOffset.UtcNow) &&
        !f.Session.IsStreaming(DateTimeOffset.UtcNow), "prepared ownership does not require media readiness");
    var result = await f.End();
    Check(!result.AlreadyCompleted && !f.Release.Pending && f.Posts == 1 && f.Probes == 1 &&
        !f.Session.HasLease(DateTimeOffset.UtcNow), "ACK plus FREE releases the exact lease");
    Check(ReferenceEquals(engine, f.Engine) && f.Alive && File.ReadAllText(cache) == "fixture-cache-preserve",
        "normal cleanup preserves the engine and persistent cache");
    foreach (var newClient in new[] { Fixture.Client, "other-device-client" })
    {
        var next = "replacement-" + newClient;
        f.Session.Prepare(next, newClient, DateTimeOffset.UtcNow);
        f.Session.Heartbeat(next, newClient, true, DateTimeOffset.UtcNow);
        var duplicate = await f.End();
        Check(duplicate.AlreadyCompleted && !duplicate.ShouldLock && f.Posts == 1 && f.Probes == 1 &&
            f.Session.IsStreaming(next, newClient, DateTimeOffset.UtcNow), "lost successful reply retries safely with replacement " + newClient);
        // Exercise the production command's receipt fast path: it must avoid
        // Snapshot/privacy/native dependencies, which are deliberately absent.
        var reply = await Send(Runtime(f), new("remote-release", "remote", LeaseId: Fixture.Lease, ClientId: Fixture.Client, EndBackend: true));
        Check(reply.Success && reply.Status is null && reply.PayloadJson!.Contains("\"alreadyCompleted\":true"),
            "production duplicate returns an exact receipt without claiming current idle");
        f.Session.End(next, newClient);
    }
    var unknown = await f.End("unknown-owner");
    Check(unknown.Obsolete && !unknown.AlreadyCompleted && !unknown.ShouldLock, "unknown old request receives obsolete without fabricated completion");
    var foreign = await f.End(client: "wrong-client");
    Check(foreign.Obsolete && !foreign.AlreadyCompleted && f.Posts == 1 && f.Probes == 1,
        "different client cannot obtain the owner's completed receipt or mutate the backend");
}
using (var f = new Fixture(root))
{
    f.Session.Reset();
    var state = f.Session.Generation;
    var runtime = Runtime(f);
    typeof(HostRuntime).GetField("_lockOnExitEnabled", flags)!.SetValue(runtime, true);
    var reply = await Send(runtime, new("remote-release", "remote", LeaseId:Fixture.Lease,ClientId:Fixture.Client,EndBackend:true,ExitReason:"user"));
    using var receipt = JsonDocument.Parse(reply.PayloadJson!);
    Check(reply.Success && reply.Status is null && !receipt.RootElement.GetProperty("completed").GetBoolean() &&
        receipt.RootElement.GetProperty("obsolete").GetBoolean() && receipt.RootElement.GetProperty("leaseId").GetString() == Fixture.Lease &&
        receipt.RootElement.GetProperty("clientId").GetString() == Fixture.Client,
        "production reset retry returns exact obsolete receipt without snapshot, privacy or lock callbacks");
    Check(f.Posts == 0 && f.Probes == 0 && f.Session.Generation == state,
        "obsolete leaves backend and control state untouched");
    var emptyGeneration = new LiveSessionState();
    var obsolete = await new RemoteBackendRelease().EndAsync(emptyGeneration, Fixture.Lease, Fixture.Client,
        new object(), () => throw new Exception("obsolete must not observe native liveness"),
        (_, _, _) => throw new Exception("obsolete must not close"), _ => throw new Exception("obsolete must not probe FREE"),
        CancellationToken.None, lockOnExit:true);
    Check(obsolete.Obsolete && !obsolete.ShouldLock && !obsolete.AlreadyCompleted,
        "unknown prior service generation resolves purely as obsolete");
}
using (var f = new Fixture(root) { Free = false })
{
    f.AtProbe = () => {
        f.AtProbe = null;
        Invalid(() => f.Session.Heartbeat(Fixture.Lease, Fixture.Client, true, DateTimeOffset.UtcNow),
            "beginning release rejects a still-unexpired ready heartbeat");
        Check(!f.Session.IsStreaming(DateTimeOffset.UtcNow), "beginning release suppresses ready evidence before FREE");
    };
    await Error(() => f.End(shouldLock: true), 409, "ACK while RTSP remains busy fails truthfully");
    f.Expire();
    Check(f.Release.Pending, "expiry retains cleanup ownership and fence");
    Invalid(() => f.Session.Prepare(Fixture.Lease, Fixture.Client, DateTimeOffset.UtcNow), "same-lease fast return cannot bypass ending");
    Invalid(() => f.Session.Prepare("replacement-lease", "other-device-client", DateTimeOffset.UtcNow), "expired ending lease cannot be replaced");
    var runtime = Runtime(f);
    foreach (var request in new[] {
        new RuntimeRequest("remote-prepare", "remote", LeaseId:"replacement-lease",ClientId:"other-device-client"),
        new RuntimeRequest("remote-release", "remote", LeaseId:Fixture.Lease,ClientId:Fixture.Client),
        new RuntimeRequest("remote-release", "remote", LeaseId:Fixture.Lease,ClientId:Fixture.Client,EndBackend:false),
        new RuntimeRequest("remote-heartbeat", "remote", LeaseId:Fixture.Lease,ClientId:Fixture.Client,StreamReady:true) })
        Check(!(await Send(runtime, request)).Success && f.Release.Pending, "production rejects pending " + request.Command + "/" + request.EndBackend);
    f.Free = true;
    var result = await f.End(shouldLock: false);
    Check(result.ShouldLock && f.Posts == 1 && !f.Release.Pending, "retry uses only FREE readback and original exit-lock snapshot");
}
using (var f = new Fixture(root))
{
    f.Expire();
    Check(!(await f.End()).AlreadyCompleted && f.Posts == 1,
        "late cleanup may close an expired lease still held by the same generation");
    await Error(() => Task.Run(() => f.Release.EnsureCanPrepare(Fixture.Lease, Fixture.Client)), 409,
        "completed lease identity cannot be reused for a new video generation");
}
using (var f = new Fixture(root))
{
    f.Expire();
    f.Session.Prepare("new-device-lease", "new-device-client", DateTimeOffset.UtcNow);
    var old = await f.End();
    Check(old.Obsolete && !old.AlreadyCompleted, "late expired cleanup becomes obsolete after replacement");
    Check(f.Posts == 0 && f.Probes == 0 && f.Session.OwnsLeaseForCleanup("new-device-lease", "new-device-client"),
        "replacement wins the gate without stale close, idle probe or lease release");
}
var exitReceipts = Path.Combine(root, "confirmed-exit-receipts.json");
if (File.Exists(exitReceipts)) File.Delete(exitReceipts);
using (var f = new Fixture(root, receiptPath: exitReceipts))
{
    f.Expire();
    f.Release.TrackEngineExit(f.Session, () => f.Alive);
    f.Session.Reset();
    Check(f.Release.Pending && !f.Release.Completed(Fixture.Lease, Fixture.Client) && !File.Exists(exitReceipts),
        "administrative reset captures expired ownership but does not invent exit proof");
    Invalid(f.Release.EnsureCanPrepareOrRelease, "failed native stop keeps administrative exit fenced");
    f.Alive = false;
    f.Release.ObserveEngineExit();
    Check(!f.Release.Pending && f.Release.Completed(Fixture.Lease, Fixture.Client) && File.Exists(exitReceipts),
        "confirmed original-engine exit persists only the corresponding completion receipt");
    var restarted = new RemoteBackendRelease(exitReceipts);
    var newer = new LiveSessionState();
    newer.Prepare("after-restart-lease", "other-device-client", DateTimeOffset.UtcNow);
    var duplicate = await restarted.EndAsync(newer, Fixture.Lease, Fixture.Client, new object(), () => true,
        (_, _, _) => throw new Exception("stale close forbidden"), _ => throw new Exception("stale idle probe forbidden"), CancellationToken.None);
    Check(duplicate.AlreadyCompleted && newer.OwnsLeaseForCleanup("after-restart-lease", "other-device-client"),
        "restart reloads proved receipts without touching the new device session");
    var unproved = await restarted.EndAsync(newer, "unproved-old-lease", Fixture.Client, new object(),
        () => throw new Exception("unknown liveness observation forbidden"),
        (_, _, _) => throw new Exception("unknown close forbidden"), _ => throw new Exception("unknown FREE observation forbidden"), CancellationToken.None);
    Check(unproved.Obsolete && !unproved.AlreadyCompleted && !restarted.Completed("unproved-old-lease", Fixture.Client),
        "unproved prior owner becomes obsolete without fabricated or persisted completion");
}
using (var f = new Fixture(root) { LostResponse = true })
{
    await Error(() => f.End(), 503, "lost close response retains ambiguous mutation fence");
    f.Expire(); f.LostResponse = false;
    await Error(() => f.End(), 503, "FREE or a second close cannot resolve an unknown first mutation");
    Check(f.Posts == 1 && f.Probes == 0, "ambiguous retry sends no close or misleading idle probe");
    f.Session.Reset();
    Invalid(f.Release.EnsureCanPrepareOrRelease, "control reset cannot clear same-engine ambiguity");
    await Error(() => f.End("unknown-old-lease"), 409, "obsolete cannot bypass another lease's pending mutation fence");
    Check(!(await Send(Runtime(f), new("remote-release", "remote", LeaseId:"unknown-old-lease",ClientId:Fixture.Client,EndBackend:true))).Success,
        "production obsolete fast path also rejects an existing pending fence");
    Check(f.Release.Pending && f.Posts == 1 && f.Probes == 0,
        "obsolete attempt cannot probe away or clear an ambiguous fence");
    f.Alive = false; f.Release.ObserveEngineExit();
    Check(!f.Release.Pending && (await f.End()).AlreadyCompleted, "actual engine exit resolves fence and records old completion");
}
foreach (var invalidCsrf in new[] { "{}", "{\"csrf_token\":\"bad token\"}", "{\"csrf_token\":\"a\",\"csrf_token\":\"b\"}" })
{
    using var f = new Fixture(root) { Csrf = invalidCsrf };
    await Error(() => f.End(), 503, "invalid CSRF cannot submit backend close");
    Check(f.Posts == 0 && f.Release.Pending, "failed preflight retains retriable cleanup identity");
    f.Csrf = "{\"csrf_token\":\"fixture-csrf\"}";
    Check(!(await f.End()).AlreadyCompleted && f.Posts == 1, "known preflight failure can retry safely");
}
foreach (var ack in new[] { "{\"status\":false}", "{\"status\":\"true\"}", "{\"status\":true,\"status\":false}" })
{
    using var f = new Fixture(root) { Ack = ack };
    await Error(() => f.End(), 503, "invalid application ACK cannot authorize idle or release");
    Check(f.Probes == 0 && f.Release.Pending, "invalid ACK leaves fence and does not probe idle");
    f.Ack = "{\"status\":true}";
    Check(!(await f.End()).AlreadyCompleted && f.Posts == 2, "confirmed completed HTTP permits a fresh acknowledged close");
}
using (var f = new Fixture(root) { CloseStatus = System.Net.HttpStatusCode.ServiceUnavailable })
{
    await Error(() => f.End(), 503, "HTTP failure cannot be hidden by a true JSON status");
    Check(f.Release.Pending && f.Probes == 0, "HTTP failure cannot authorize FREE verification");
}
using (var f = new Fixture(root))
{
    using var cancelled = new CancellationTokenSource();
    f.BeforeClose = _ => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); };
    await Error(() => f.End(token: cancelled.Token), 504, "cancellation after submission is an ambiguous mutation");
    Check(f.Release.Pending && f.Posts == 1, "cancelled mutation keeps its fence");
}
// Deterministic interleave against the real runtime gate: preparation cannot
// enter while native close runs, then sees the retained BUSY fence after failure.
using (var f = new Fixture(root) { Free = false })
using (var gate = new SemaphoreSlim(1, 1))
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    f.BeforeClose = async _ => { Check(gate.CurrentCount == 0, "close executes under the runtime gate"); entered.SetResult(); await finish.Task; };
    await gate.WaitAsync();
    var ending = Error(() => f.End(), 409, "busy readback retains fence across the gate handoff");
    await entered.Task;
    var preparing = Send(Runtime(f, gate), new("remote-prepare", "remote", LeaseId:"new-lease",ClientId:"new-device"));
    Check(!preparing.IsCompleted, "replacement waits behind native close");
    finish.SetResult(); await ending; gate.Release();
    Check(!(await preparing).Success && f.Release.Pending, "replacement is rejected after an unconfirmed close");
}
const string freeXml = "<root status_code=\"200\"><state>SUNSHINE_SERVER_FREE</state><currentgame>0</currentgame></root>";
Check(RemoteBackendRelease.IsFree(freeXml), "exact FREE/status200/currentgame0 is accepted");
foreach (var xml in new[] { "", "<root/>", freeXml.Replace("200", "503"), freeXml.Replace("FREE", "BUSY"),
    freeXml.Replace("<currentgame>0", "<currentgame>1"), freeXml.Replace("<currentgame>0</currentgame>", ""),
    freeXml.Replace("</root>", "<state>SUNSHINE_SERVER_FREE</state></root>") })
    Check(!RemoteBackendRelease.IsFree(xml), "unknown/malformed/busy serverinfo is never idle");
checks += await WireChecks.RunAsync();
Console.WriteLine($"{checks} focused checks passed; injected HTTP/native callbacks only, no sockets, pipes, desktop or installed state.");
