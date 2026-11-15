using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed class DeviceMediaPairing(ControlDeviceRegistry devices, ProfileStore profiles)
{
    internal static ControlMediaBaseline[] Snapshot(MediaTrustClient[] clients) => clients.Select(c =>
        new ControlMediaBaseline(c.Uuid, Convert.ToHexString(SHA256.HashData(c.Certificate)), c.Enabled)).ToArray();

    internal async Task BeginAsync(string credentialId, string profile, CancellationToken token) =>
        devices.BeginMediaPairing(credentialId, profile, Snapshot(await profiles.ReadMediaTrustAsync(profile, token)));

    internal async Task CompleteAsync(string profile, CancellationToken token) =>
        devices.CompleteMediaPairing(profile, Snapshot(await profiles.ReadMediaTrustAsync(profile, token)));

    // Caller has positively confirmed the selected product engine has exited.
    // Keep the durable fence on any I/O failure; startup must retry before launch.
    internal async Task RecoverAsync(string profile, CancellationToken token)
    {
        var pending = devices.PendingMediaPairing(profile);
        if (pending is null) return;
        string path = Path.Combine(profiles.DirectoryFor(profile), "state.json");
        var current = await profiles.ReadMediaTrustAsync(profile, token);
        if (current.Length > 0)
        {
            byte[] before = await ProfileStore.ReadMediaFileAsync(path, 1048576, token);
            var document = JsonNode.Parse(before) ?? throw new IOException("媒体授权记录无效。");
            var entries = document["root"]?["named_devices"]?.AsArray() ?? throw new IOException("媒体授权记录无效。");
            var marks = Snapshot(current);
            bool changed = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                string id = entries[i]?["uuid"]?.GetValue<string>() ?? "";
                var mark = marks.Single(c => c.Id == id);
                var original = pending.Before.SingleOrDefault(c => c.Id == id && c.CertificateHash == mark.CertificateHash);
                if (original is null) { entries.RemoveAt(i); changed = true; }
                else if (original.Enabled != mark.Enabled) { entries[i]!["enabled"] = original.Enabled; changed = true; }
            }
            if (changed)
            {
                // The validated projection and exact JSON snapshot must still agree.
                var verified = Snapshot(await profiles.ReadMediaTrustAsync(profile, token));
                byte[] latest = await ProfileStore.ReadMediaFileAsync(path, 1048576, token);
                if (!verified.SequenceEqual(marks) || !before.AsSpan().SequenceEqual(latest))
                    throw new IOException("媒体授权记录发生变化，未完成配对回收。");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
                try
                {
                    await File.WriteAllTextAsync(temporary, document.ToJsonString(), new UTF8Encoding(false), token);
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        devices.ClearMediaPairing(profile);
    }
}
