using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StreamHub.Host.Runtime;
using StreamHub.Service;

static class AssistanceMediaInterop
{
    public static async Task RunAsync(string receiverSource, string engine, string fixtureRoot)
    {
        string fixture = Path.GetFullPath(fixtureRoot);
        if (Directory.Exists(fixture)) throw new IOException("Use a fresh media fixture directory.");
        Directory.CreateDirectory(fixture);
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=isolated-media-" + Guid.NewGuid().ToString("N"), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string id = Guid.NewGuid().ToString("D");
        var media = new AssistanceMediaProfile(fixture);
        var session = new AssistanceJournal(1, id, Convert.ToBase64String(certificate.RawData), "media-fixture", "control");
        await media.PrepareAsync(session, default);
        string config = await File.ReadAllTextAsync(media.ConfigPath);
        await File.WriteAllTextAsync(media.ConfigPath, config.Replace("port = 48989", "port = 52989") + "\nmin_log_level = 1\n");
        var hostStart = new ProcessStartInfo(Path.GetFullPath(engine)) { WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(engine)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        hostStart.ArgumentList.Add(media.ConfigPath);
        using var host = Process.Start(hostStart)!;
        Task hostOut = Capture(host.StandardOutput, Path.Combine(fixture, "host-stdout.log"));
        Task hostErr = Capture(host.StandardError, Path.Combine(fixture, "host-stderr.log"));
        Process? receiver = null;
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            bool ready = false;
            for (int i = 0; i < 100 && !host.HasExited; i++)
            {
                try { ready = (await http.GetStringAsync("http://127.0.0.1:52989/serverinfo")).Contains(id); }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                if (ready) break;
                await Task.Delay(250);
            }
            if (!ready) throw new IOException("Isolated media host failed to start; inspect host logs.");
            string root = Path.Combine(fixture, "receiver"); Directory.CreateDirectory(root);
            AssistanceClient.CopyRuntime(receiverSource, root);
            string settings = Path.Combine(root, "Moonlight Game Streaming Project"); Directory.CreateDirectory(settings);
            using var hostCert = new X509Certificate2(Convert.FromBase64String(media.ServerCertificate));
            await File.WriteAllTextAsync(Path.Combine(settings, "Moonlight.ini"), AssistanceClient.BuildSettings(
                new(id, "active", "MEDIA-TEST", "control", MediaHostId: id), "127.0.0.1", certificate.ExportCertificatePem(),
                key.ExportRSAPrivateKeyPem(), hostCert.ExportCertificatePem(), 52989), new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "portable.dat"), "");
            var start = new ProcessStartInfo(Path.Combine(root, "Moonlight.exe")) { WorkingDirectory = root,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "stream", id, "远程协助桌面", "--720", "--fps", "30", "--bitrate", "5000",
                "--display-mode", "windowed", "--no-quit-after", "--no-multi-controller" }) start.ArgumentList.Add(argument);
            receiver = Process.Start(start)!;
            output = Capture(receiver.StandardOutput, Path.Combine(fixture, "receiver-stdout.log"));
            error = Capture(receiver.StandardError, Path.Combine(fixture, "receiver-stderr.log"));
            Console.WriteLine($"MEDIA_FIXTURE_READY hostPid={host.Id} receiverPid={receiver.Id}");
            using var duration = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await receiver.WaitForExitAsync(duration.Token); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            if (receiver is not null && !receiver.HasExited) { receiver.Kill(true); await receiver.WaitForExitAsync(); }
            if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); }
            await Task.WhenAll(hostOut, hostErr, output, error);
            await media.CleanupAsync(session, default);
            receiver?.Dispose();
        }
    }
    private static async Task Capture(StreamReader reader, string path)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
    }
}
