using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace StreamHub.Service;

/// <summary>Dedicated state file. No trusted-device state is ever copied here.</summary>
internal sealed class AssistanceMediaProfile(string data)
{
    public string DirectoryPath => Path.Combine(data, "assistance", "media");
    public string ConfigPath => Path.Combine(DirectoryPath, "sunshine.conf");
    public string ServerCertificate { get; private set; } = "";
    public async Task PrepareAsync(StreamHub.Host.Runtime.AssistanceJournal session, CancellationToken token)
    {
        if (session.SessionId is null || session.Mode is not ("view" or "control")) throw new InvalidOperationException("协助尚未授权。");
        Directory.CreateDirectory(DirectoryPath);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=iControl temporary assistance", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(3));
        ServerCertificate = Convert.ToBase64String(cert.RawData);
        using var client = new X509Certificate2(Convert.FromBase64String(session.Certificate!));
        string certPath = Path.Combine(DirectoryPath, "cacert.pem"), keyPath = Path.Combine(DirectoryPath, "cakey.pem");
        await File.WriteAllTextAsync(certPath, cert.ExportCertificatePem(), token);
        await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem(), token);
        await WriteStateAsync(new { root = new { uniqueid = session.SessionId,
            named_devices = new[] { new { name = "assistance:" + session.SessionId, uuid = session.SessionId,
                cert = client.ExportCertificatePem(), enabled = true } } } }, token);
        string apps = Path.Combine(DirectoryPath, "apps.json");
        await File.WriteAllTextAsync(apps, JsonSerializer.Serialize(new { env = new { },
            apps = new[] { new Dictionary<string, object> { ["name"] = "远程协助桌面", ["image-path"] = "desktop.png" } } }), token);
        bool control = session.Mode == "control";
        var settings = new Dictionary<string, string> {
            ["sunshine_name"] = "我控制 · 临时协助", ["port"] = "48989", ["channels"] = "1",
            ["file_apps"] = apps, ["file_state"] = Path.Combine(DirectoryPath, "state.json"),
            ["credentials_file"] = Path.Combine(DirectoryPath, "state.json"), ["cert"] = certPath, ["pkey"] = keyPath,
            ["log_path"] = Path.Combine(DirectoryPath, "media.log"), ["upnp"] = "disabled",
            ["origin_web_ui_allowed"] = "pc", ["system_tray"] = "disabled", ["notify_pre_releases"] = "disabled",
            ["dd_configuration_option"] = "disabled", ["install_steam_audio_drivers"] = "disabled",
            ["keyboard"] = control ? "enabled" : "disabled", ["mouse"] = control ? "enabled" : "disabled",
            ["native_pen_touch"] = control ? "enabled" : "disabled", ["controller"] = "disabled",
            ["streamhub_probe_plan"] = "sdr-auto", ["streamhub_persist_probe"] = "enabled"
        };
        await File.WriteAllLinesAsync(ConfigPath, settings.Select(p => p.Key + " = " + p.Value), new UTF8Encoding(false), token);
    }
    public async Task CleanupAsync(StreamHub.Host.Runtime.AssistanceJournal session, CancellationToken token)
    {
        string state = Path.Combine(DirectoryPath, "state.json");
        if (File.Exists(state))
        {
            // A foreign certificate is an error, never an excuse for unpair-all.
            using var json = JsonDocument.Parse(await ProfileStore.ReadMediaFileAsync(state, 1048576, token));
            var devices = json.RootElement.GetProperty("root").GetProperty("named_devices");
            foreach (var entry in devices.EnumerateArray())
            {
                using var cert = X509Certificate2.CreateFromPem(entry.GetProperty("cert").GetString()!);
                if (entry.GetProperty("uuid").GetString() != session.SessionId ||
                    !cert.RawData.AsSpan().SequenceEqual(Convert.FromBase64String(session.Certificate!)))
                    throw new InvalidDataException("临时协助媒体身份与清理记录不一致，已阻止新会话。");
            }
            await WriteStateAsync(new { root = new { uniqueid = session.SessionId, named_devices = Array.Empty<object>() } }, token);
            using var after = JsonDocument.Parse(await ProfileStore.ReadMediaFileAsync(state, 1048576, token));
            if (after.RootElement.GetProperty("root").GetProperty("named_devices").GetArrayLength() != 0)
                throw new IOException("协助配对清理回读未通过。");
        }
        foreach (string name in new[] { "cakey.pem", "cacert.pem", "sunshine.conf", "apps.json", "media.log" })
        {
            string path = Path.Combine(DirectoryPath, name);
            if (File.Exists(path)) File.Delete(path);
        }
        ServerCertificate = "";
    }
    private async Task WriteStateAsync(object state, CancellationToken token)
    {
        var path = Path.Combine(DirectoryPath, "state.json");
        var temporary = path + ".new";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), new UTF8Encoding(false), token);
        File.Move(temporary, path, true);
    }
}
