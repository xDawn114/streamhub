using System.Text.Json;
using StreamHub.Host.Runtime;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Refuses(Action action, string message)
{
    try { action(); } catch (InvalidOperationException) { checks++; return; }
    throw new Exception(message);
}
var now = DateTimeOffset.UtcNow;
var registry = new TabletRequestRegistry(() => now);
registry.Poll(new("tablet", "Tablet", 2560, 1600, 60, [0, 90, 180, 270], now, true));
foreach (int angle in new[] { 0, 90, 180, 270 })
{
    var fixedRequest = registry.Create("tablet", angle, "auto");
    Check(fixedRequest.Orientation == angle && !fixedRequest.AutoOrientation, "fixed angle must keep its original meaning");
    registry.Cancel(fixedRequest.Id);
}
var automatic = registry.Create("tablet", 0, "lan", true);
var encoded = JsonSerializer.Serialize(automatic, RuntimeProtocol.JsonOptions);
Check(JsonSerializer.Deserialize<TabletConnectionRequest>(encoded, RuntimeProtocol.JsonOptions)!.AutoOrientation,
    "automatic request must survive receiver JSON roundtrip");
registry.Respond(automatic.Id, "tablet", "accept");
registry.BindLease(automatic.Id, "tablet", "same-lease");
registry.Respond(automatic.Id, "tablet", "connecting");
registry.Respond(automatic.Id, "tablet", "connected");
registry.BindLease(automatic.Id, "tablet", "same-lease");
Check(registry.LeaseFor(automatic.Id) == ("tablet", "same-lease") && registry.Snapshot(automatic.Id).Request!.State == "connected",
    "rotation reuses exact connected lease without another approval or allocation");
Refuses(() => registry.BindLease(automatic.Id, "other-tablet", "same-lease"), "rotation may not change receiver");
Refuses(() => registry.BindLease(automatic.Id, "tablet", "new-lease"), "rotation may not allocate a second lease");
registry.Session("tablet", "same-lease", true);
Refuses(() => registry.BindLease(automatic.Id, "tablet", "same-lease"), "ended request must not be revived by rotation");
registry.Poll(new("old-tablet", "Old tablet", 1920, 1200, 60, [0, 90, 180, 270], now));
Refuses(() => registry.Create("old-tablet", 0, "auto", true), "older receiver must not silently turn automatic into fixed orientation");
var legacy = registry.Create("old-tablet", 90, "auto");
Check(!legacy.AutoOrientation && legacy.Orientation == 90, "older receivers still accept fixed directions");
registry.Cancel(legacy.Id);
var waiting = registry.Create("tablet", 0, "auto", true);
now += TimeSpan.FromSeconds(46);
Refuses(() => registry.BindLease(waiting.Id, "tablet", "same-lease"), "expired request may not be reused");
var oldJson = "{\"id\":\"legacy\",\"clientId\":\"tablet\",\"state\":\"waiting\",\"orientation\":90,\"link\":\"lan\",\"expiresAt\":\"2026-11-19T00:00:00Z\",\"message\":\"waiting\"}";
Check(!JsonSerializer.Deserialize<TabletConnectionRequest>(oldJson, RuntimeProtocol.JsonOptions)!.AutoOrientation,
    "legacy request without new field remains fixed");
Console.WriteLine($"{checks} tablet request checks passed; no installed service or device contacted.");
