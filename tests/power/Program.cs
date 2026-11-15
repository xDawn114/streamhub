using System.Text.Json;
using StreamHub.Host.Runtime;
using StreamHub.Service;

int count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
foreach (var mode in new[] {PowerAwakePolicy.System, PowerAwakePolicy.Streaming, PowerAwakePolicy.PluggedIn})
foreach (bool streaming in new[] {false, true})
foreach (bool plugged in new[] {false, true})
{
    bool expected = mode == PowerAwakePolicy.Streaming ? streaming : mode == PowerAwakePolicy.PluggedIn && (streaming || plugged);
    Check(PowerAwakePolicy.ShouldPreventSleep(mode, streaming, plugged) == expected, $"policy {mode} stream={streaming} ac={plugged}");
}
Check(!PowerAwakePolicy.IsValid("always") && !PowerAwakePolicy.IsValid(null), "reject unsupported settings");
Check(PowerAwakePolicy.Normalize(null) == "streaming", "missing setting migrates to session-only");
int acquired = 0, released = 0;
bool ac = true;
var guard = new PowerAwakeGuard(() => { acquired++; return new Request(() => released++); }, () => ac);
guard.Update("streaming", false); Check(!guard.Active && acquired == 0, "idle engine does not inhibit sleep");
guard.Update("streaming", true); guard.Update("streaming", true);
Check(guard.Active && acquired == 1, "repeated heartbeats acquire only one request");
guard.Update("streaming", false); Check(!guard.Active && released == 1, "disconnect releases request");
guard.Update("plugged-in", false); ac = false; guard.Update("plugged-in", false);
Check(!guard.Active && released == 2, "unplug releases idle request");
guard.Update("plugged-in", true); Check(guard.Active, "battery streaming remains awake");
guard.Update("system", true); Check(!guard.Active && released == 3, "system policy releases even during streaming");
guard.Update("plugged-in", true); guard.Dispose(); guard.Dispose(); guard.Update("plugged-in", true);
Check(released == 4 && acquired == 4 && !guard.Active, "shutdown clears once and cannot reacquire");
int attempts = 0;
using (var retry = new PowerAwakeGuard(() => ++attempts == 1 ? null : new Request(() => {}), () => true))
{
    retry.Update("streaming", true); Check(retry.Failed && !retry.Active, "request failure is reported honestly");
    retry.Update("streaming", true); Check(!retry.Failed && retry.Active, "transient request failure can recover");
}
bool nativeAccepted = false;
if (args.Contains("--native"))
{
    using var native = SystemAwakeRequest.TryCreate();
    Check(native is not null, "Windows accepts scoped SYSTEM_REQUIRED request");
    nativeAccepted = native is not null;
}
Console.WriteLine(JsonSerializer.Serialize(new {passed=true, count, nativeAccepted, changedPowerPlan=false}));
sealed class Request(Action release) : IDisposable { public void Dispose() => release(); }
