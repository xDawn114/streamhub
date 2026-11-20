using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using StreamHub.Host.Runtime;

static class ReceiverInterop
{
    public static async Task RunAsync(string receiverRoot, string fixtureRoot)
    {
        string fixture = Path.GetFullPath(fixtureRoot);
        if (Directory.Exists(fixture)) throw new IOException("Use a fresh fixture directory.");
        Directory.CreateDirectory(fixture);
        string receiver = Path.Combine(fixture, "receiver"); Directory.CreateDirectory(receiver);
        AssistanceClient.CopyRuntime(Path.GetFullPath(receiverRoot), receiver);
        using var clientKey = RSA.Create(2048); using var serverKey = RSA.Create(2048);
        string identity = "isolated-native-peer-" + Guid.NewGuid().ToString("N");
        using var client = new CertificateRequest("CN=" + identity, clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var server = new CertificateRequest("CN=isolated-native-host", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string id = Guid.NewGuid().ToString("D");
        // Node's OpenSSL keeps fixture keys in memory, independent of Windows' persisted-key permissions.
        string script = Path.Combine(fixture, "server.mjs");
        await File.WriteAllTextAsync(script, ServerScript);
        var serverStart = new ProcessStartInfo("node", script) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var host = Process.Start(serverStart)!;
        try
        {
            await host.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, certificate = server.ExportCertificatePem(),
                key = serverKey.ExportPkcs8PrivateKeyPem(), client = Convert.ToBase64String(client.RawData),
                result = Path.Combine(fixture, "server-result.json") }));
            host.StandardInput.Close();
            string ready = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                ?? throw new IOException("Isolated TLS fixture did not start: " + await host.StandardError.ReadToEndAsync());
            using var ports = JsonDocument.Parse(ready);
            int httpPort = ports.RootElement.GetProperty("httpPort").GetInt32();
            string settings = Path.Combine(receiver, "Moonlight Game Streaming Project"); Directory.CreateDirectory(settings);
            string ini = AssistanceClient.BuildSettings(new(id, "active", "FIXT-URE0", "view", MediaHostId: id), "127.0.0.1",
                client.ExportCertificatePem(), clientKey.ExportRSAPrivateKeyPem(), server.ExportCertificatePem(), httpPort);
            await File.WriteAllTextAsync(Path.Combine(settings, "Moonlight.ini"), ini, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(receiver, "portable.dat"), "");
            var start = new ProcessStartInfo(Path.Combine(receiver, "Moonlight.exe")) {
                WorkingDirectory = receiver, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            start.StandardOutputEncoding = Encoding.GetEncoding((int)GetACP());
            bool diagnostic = Environment.GetEnvironmentVariable("ASSISTANCE_TLS_DIAGNOSTIC") == "1";
            start.ArgumentList.Add(diagnostic ? "stream" : "list"); start.ArgumentList.Add(id);
            if (diagnostic) start.ArgumentList.Add("FixtureDoesNotExist");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            int timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("ASSISTANCE_TLS_TIMEOUT_SECONDS"), out int seconds)
                && seconds is >= 5 and <= 40 ? seconds : diagnostic ? 12 : 40;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            string stdout = await output; string stderr = await error;
            if (CngKey.Exists(identity, CngProvider.MicrosoftSoftwareKeyStorageProvider))
            {
                using var imported = CngKey.Open(identity, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                imported.Delete();
            }
            await File.WriteAllTextAsync(Path.Combine(fixture, "diagnostic.txt"), stdout + "\n" + stderr);
            using var observed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "server-result.json")));
            var data = observed.RootElement;
            var result = new { checkedAt = DateTimeOffset.Now, exitCode = process.ExitCode,
                privateKeyImportFailure = (stdout + stderr).Contains("Failed to import private key:", StringComparison.Ordinal),
                pinnedClientCertificateObserved = data.GetProperty("matchedClient").GetInt32() > 0,
                unexpectedRequests = data.GetProperty("unexpected").GetInt32(),
                plainRequests = data.GetProperty("plainRequests").GetInt32(), secureRequests = data.GetProperty("secureRequests").GetInt32(),
                appListed = stdout.Contains("远程协助桌面"), automaticDiscoveryDisabled = ini.Contains("mdns=false"),
                realMediaStreamStarted = false, keyboardOrMouseInputSent = false };
            await File.WriteAllTextAsync(Path.Combine(fixture, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(result));
            if (process.ExitCode != 0 || !result.pinnedClientCertificateObserved || result.unexpectedRequests != 0 || !result.appListed)
                throw new Exception("Real pinned receiver did not complete isolated mutual-TLS listing; see fixture result.");
        }
        finally { if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); } }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private const string ServerScript = """
        import http from 'node:http';
        import https from 'node:https';
        import fs from 'node:fs';
        let input = '';
        for await (const chunk of process.stdin) input += chunk;
        const config = JSON.parse(input);
        const expected = Buffer.from(config.client, 'base64');
        const result = { matchedClient: 0, unexpected: 0, plainRequests: 0, secureRequests: 0 };
        let tlsPort;
        const record = () => fs.writeFileSync(config.result, JSON.stringify(result));
        function request(req, res) {
            const secure = req.socket.encrypted === true;
            if (secure) {
                const cert = req.socket.getPeerCertificate().raw;
                if (!cert?.equals(expected)) { result.unexpected++; record(); req.socket.destroy(); return; }
                result.matchedClient++; result.secureRequests++;
            } else result.plainRequests++;
            const path = new URL(req.url, 'http://localhost').pathname;
            if (req.method !== 'GET' || !['/serverinfo', '/applist'].includes(path) || (!secure && path !== '/serverinfo')) {
                result.unexpected++; record(); res.writeHead(403); res.end(); return;
            }
            record();
            res.setHeader('Content-Type', 'application/xml');
            res.end(path === '/serverinfo'
                ? `<root status_code="200"><hostname>isolated-assistance</hostname><uniqueid>${config.id}</uniqueid><appversion>7.1.431.0</appversion><GfeVersion>3.23.0.74</GfeVersion><HttpsPort>${tlsPort}</HttpsPort><PairStatus>1</PairStatus><currentgame>0</currentgame><state>SUNSHINE_SERVER_FREE</state><ServerCodecModeSupport>1</ServerCodecModeSupport><MaxLumaPixelsHEVC>0</MaxLumaPixelsHEVC></root>`
                : '<root status_code="200"><App><ID>1</ID><AppTitle>远程协助桌面</AppTitle><IsHdrSupported>0</IsHdrSupported></App></root>');
        }
        const secure = https.createServer({ key: config.key, cert: config.certificate, requestCert: true, rejectUnauthorized: false }, request);
        secure.on('secureConnection', socket => {
            const cert = socket.getPeerCertificate().raw;
            if (!cert?.equals(expected)) { result.unexpected++; record(); socket.destroy(); }
        });
        await new Promise(resolve => secure.listen(0, '127.0.0.1', resolve));
        tlsPort = secure.address().port;
        const plain = http.createServer(request);
        await new Promise(resolve => plain.listen(0, '127.0.0.1', resolve));
        record();
        console.log(JSON.stringify({ httpPort: plain.address().port, tlsPort }));
        """;
}
