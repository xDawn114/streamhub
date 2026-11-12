using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamHub.Service;

internal sealed record BackendCredentials(string Username, string Password);
internal sealed record MediaTrustClient(string Uuid, bool Enabled, byte[] Certificate);

internal sealed class ProfileStore(string root, string data)
{
    // Upstream also persists display recovery state relative to its executable.
    // Isolate the entire backend directory, not only each profile's config file.
    public string ExecutableFor(string profile) => Path.Combine(root, "components", "streaming", profile, "sunshine.exe");
    public string DirectoryFor(string profile) => Path.Combine(data, "profiles", profile);
    public string ConfigFor(string profile) => Path.Combine(DirectoryFor(profile), "sunshine.conf");
    public string CertificateFor(string profile) => Path.Combine(DirectoryFor(profile), "credentials", "cacert.pem");
    public static int Port(string profile) => profile == "remote" ? 48989 : 49089;

    // Service-internal projection only. The running service reads its own selected
    // profile with its normal identity; no ACL changes, impersonation or raw export.
    internal async Task<MediaTrustClient[]> ReadMediaTrustAsync(string profile, CancellationToken cancellationToken)
    {
        ValidateMediaProfile(profile);
        byte[]? bytes = null;
        try
        {
            try { bytes = await ReadMediaFileAsync(Path.Combine(DirectoryFor(profile), "state.json"), 1024 * 1024, cancellationToken); }
            catch (FileNotFoundException) { return []; } // The live API must independently agree that it is empty.
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var documentRoot = document.RootElement;
            if (documentRoot.ValueKind != JsonValueKind.Object || HasDuplicateMediaProperties(documentRoot)) throw new JsonException();
            if (!documentRoot.TryGetProperty("root", out var root)) return []; // Credentials-only, not yet paired.
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateMediaProperties(root) || root.TryGetProperty("devices", out _))
                throw new JsonException(); // Legacy records need an engine migration, not a guessed identity.
            if (!root.TryGetProperty("uniqueid", out var uniqueId) || uniqueId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(uniqueId.GetString())) throw new JsonException();
            if (!root.TryGetProperty("named_devices", out var clients)) return [];
            if (clients.ValueKind == JsonValueKind.String && clients.GetString() == "") return [];
            if (clients.ValueKind != JsonValueKind.Array || clients.GetArrayLength() > 1024) throw new JsonException();
            var result = new List<MediaTrustClient>();
            var ids = new HashSet<Guid>();
            foreach (var client in clients.EnumerateArray())
            {
                if (client.ValueKind != JsonValueKind.Object || HasDuplicateMediaProperties(client) ||
                    !client.TryGetProperty("uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String ||
                    !Guid.TryParseExact(uuid.GetString(), "D", out var id) || id == Guid.Empty || !ids.Add(id) ||
                    !client.TryGetProperty("cert", out var cert) || cert.ValueKind != JsonValueKind.String) throw new JsonException();
                var enabled = true; // nvhttp::load_state defaults a missing enabled field to true.
                if (client.TryGetProperty("enabled", out var value))
                    enabled = value.ValueKind switch
                    {
                        JsonValueKind.True => true, JsonValueKind.False => false,
                        JsonValueKind.String when value.GetString() == "true" => true,
                        JsonValueKind.String when value.GetString() == "false" => false,
                        _ => throw new JsonException()
                    };
                result.Add(new(uuid.GetString()!, enabled, ParseMediaPem(cert.GetString()!)));
            }
            return result.ToArray();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new MediaIdentityException(503, "所选串流模式的媒体身份配置不可用或格式无效，未更改媒体信任。"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task<byte[]> ReadMediaServerCertificateAsync(string profile, CancellationToken cancellationToken)
    {
        ValidateMediaProfile(profile);
        try
        {
            var bytes = await ReadMediaFileAsync(CertificateFor(profile), 16384, cancellationToken);
            return ParseMediaPem(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new MediaIdentityException(503, "所选串流模式的公开服务端证书不可用，未更改媒体信任。"); }
    }

    private static void ValidateMediaProfile(string profile)
    {
        if (profile is not ("remote" or "tablet")) throw new ArgumentException("模式只能为 remote 或 tablet。");
    }

    private static byte[] ParseMediaPem(string pem)
    {
        var text = pem.AsSpan().Trim();
        if (text.Length > 16384 || !PemEncoding.TryFind(text, out var fields) ||
            !text[fields.Label].SequenceEqual("CERTIFICATE") || fields.Location.Start.GetOffset(text.Length) != 0 ||
            fields.Location.End.GetOffset(text.Length) != text.Length) throw new CryptographicException();
        using var certificate = X509Certificate2.CreateFromPem(text);
        return StreamHub.Host.Runtime.MediaIdentityPolicy.ParseCertificate("remote", Convert.ToBase64String(certificate.RawData));
    }

    private static bool HasDuplicateMediaProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static async Task<byte[]> ReadMediaFileAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new IOException();
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                if (body.Length + count > maximumBytes) throw new IOException();
                body.Write(buffer, 0, count);
            }
            return body.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(body.GetBuffer());
        }
    }

    public async Task EnsureAsync(string profile, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(profile);
        Directory.CreateDirectory(Path.Combine(directory, "credentials"));
        if (!File.Exists(ConfigFor(profile)))
        {
            var settings = new Dictionary<string, string>
            {
                ["sunshine_name"] = profile == "remote" ? "我控制 · 远程主屏" : "我控制 · 平板副屏",
                ["port"] = Port(profile).ToString(),
                ["file_apps"] = Path.Combine(directory, "apps.json"),
                ["file_state"] = Path.Combine(directory, "state.json"),
                ["credentials_file"] = Path.Combine(directory, "state.json"),
                ["cert"] = CertificateFor(profile),
                ["pkey"] = Path.Combine(directory, "credentials", "cakey.pem"),
                ["log_path"] = Path.Combine(data, "logs", profile + ".log"),
                // Omit encoder for automatic selection. Upstream treats the
                // serialized trailing space in "encoder = " as an encoder name.
                ["upnp"] = "disabled", ["origin_web_ui_allowed"] = "pc",
                ["system_tray"] = "disabled", ["notify_pre_releases"] = "disabled",
                ["stream_audio"] = profile == "remote" ? "enabled" : "disabled",
                ["install_steam_audio_drivers"] = "disabled",
                ["dd_configuration_option"] = "disabled"
            };
            if (profile == "tablet")
            {
                // The receiver advertises its physical mode during prepare.
                // Keep the backend neutral until that negotiation completes;
                // hard-coding the original tablet's panel mode makes
                // phones and other tablets scale unnecessarily.
                settings["dd_resolution_option"] = "auto";
                settings["dd_refresh_rate_option"] = "auto";
                settings["dd_config_revert_on_disconnect"] = "disabled";
            }
            await File.WriteAllLinesAsync(ConfigFor(profile), SerializeSettings(settings), new UTF8Encoding(false), cancellationToken);
        }
        await MigrateGeneratedSettingsAsync(profile, cancellationToken);
        var apps = Path.Combine(directory, "apps.json");
        if (!File.Exists(apps))
            await File.WriteAllTextAsync(apps, JsonSerializer.Serialize(new { env = new { }, apps = new[]
            {
                new Dictionary<string, object> { ["name"] = profile == "remote" ? "远程桌面" : "平板副屏", ["image-path"] = "desktop.png" }
            } }), cancellationToken);

        var adminPath = Path.Combine(directory, "admin.json");
        if (!File.Exists(adminPath))
        {
            var credentials = new BackendCredentials("streamhub", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await File.WriteAllTextAsync(adminPath, JsonSerializer.Serialize(credentials), cancellationToken);
        }
        var state = Path.Combine(directory, "state.json");
        if (File.Exists(state))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(state, cancellationToken));
            if (json.RootElement.TryGetProperty("username", out var username) && !string.IsNullOrEmpty(username.GetString())) return;
        }
        var executable = ExecutableFor(profile);
        if (!File.Exists(executable)) throw new FileNotFoundException("内置串流组件缺失，请修复安装。", executable);
        var credentialsToSet = await ReadCredentialsAsync(profile, cancellationToken);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { ConfigFor(profile), "--creds", credentialsToSet.Username, credentialsToSet.Password }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法初始化内置串流认证。");
        var output = DiscardOutputAsync(process.StandardOutput);
        var error = DiscardOutputAsync(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("内置串流认证初始化超过 15 秒。请检查安装路径及组件完整性后修复安装。");
        }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        finally
        {
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("无法初始化内置串流认证，请查看组件是否完整。");
        // Some upstream configuration failures return exit code 0. Verify the
        // artifact rather than allowing a half-initialized backend to start.
        if (!File.Exists(state)) throw new InvalidOperationException("内置串流认证文件未生成。请使用英文安装路径并修复安装。");
        using var initialized = JsonDocument.Parse(await File.ReadAllTextAsync(state, cancellationToken));
        if (!initialized.RootElement.TryGetProperty("username", out var initializedUser) ||
            !string.Equals(initializedUser.GetString(), credentialsToSet.Username, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("内置串流认证初始化未通过校验，请修复安装。");
    }

    private async Task MigrateGeneratedSettingsAsync(string profile, CancellationToken cancellationToken)
    {
        var path = ConfigFor(profile);
        var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
        // Only migrate this product's current and former generated names.
        // There is no provenance marker distinguishing an old generated nvenc
        // default from an explicit user choice, so preserve every nonempty
        // encoder, including nvenc. Automatic selection is represented by an
        // absent key, never a whitespace value.
        var suffix = profile == "remote" ? "远程主屏" : "平板副屏";
        var expectedNames = new[] { "我控制", "串流中心", "梨子控制" }.Select(name => name + " · " + suffix);
        if (!lines.Any(line => expectedNames.Any(name => Regex.IsMatch(line,
            @"^\s*sunshine_name\s*=\s*" + Regex.Escape(name) + @"\s*$", RegexOptions.IgnoreCase)))) return;
        var changed = lines.RemoveAll(line => Regex.IsMatch(line, @"^\s*encoder\s*=\s*$", RegexOptions.IgnoreCase)) != 0;
        void Set(string key, string value)
        {
            var pattern = @"^\s*" + Regex.Escape(key) + @"\s*=";
            var index = lines.FindIndex(line => Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase));
            var line = $"{key} = {value}";
            if (index < 0) { lines.Add(line); changed = true; }
            else if (lines[index] != line) { lines[index] = line; changed = true; }
        }
        Set("stream_audio", profile == "remote" ? "enabled" : "disabled");
        Set("install_steam_audio_drivers", "disabled");
        if (profile == "tablet")
        {
            // The product display helper owns activation, rotation and cleanup.
            // A second owner can fail mode preparation, repeat encoder probes,
            // or restore the virtual desktop after the product detached it.
            Set("dd_configuration_option", "disabled");
            Set("dd_config_revert_on_disconnect", "disabled");
        }
        if (!changed) return;
        var temporary = path + ".new";
        await File.WriteAllLinesAsync(temporary, lines, new UTF8Encoding(false), cancellationToken);
        File.Move(temporary, path, true);
    }

    private static async Task DiscardOutputAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory()) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static IEnumerable<string> SerializeSettings(IEnumerable<KeyValuePair<string, string>> settings) => settings
        .Where(kv => !string.Equals(kv.Key, "encoder", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(kv.Value))
        .Select(kv => $"{kv.Key} = {kv.Value}");

    public async Task<BackendCredentials> ReadCredentialsAsync(string profile, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<BackendCredentials>(await File.ReadAllTextAsync(Path.Combine(DirectoryFor(profile), "admin.json"), cancellationToken))
        ?? throw new InvalidOperationException("串流认证配置不完整，请修复安装。");

    // Called only after a future interactive-session mapping has resolved an
    // active physical display to its unique Sunshine output identifier.
    public async Task ConfigureRemoteOutputAsync(string output, CancellationToken cancellationToken)
    {
        output = StreamHub.Host.Runtime.RemoteDisplaySelection.ValidateOutput(output);
        var path = ConfigFor("remote");
        if (!File.Exists(path)) throw new InvalidOperationException("主屏配置尚未初始化。");
        var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
        var matches = lines.Select((line, index) => (line, index))
            .Where(item => Regex.IsMatch(item.line, @"^\s*output_name\s*=", RegexOptions.IgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("主屏配置包含重复显示器标识，未更改配置。");
        if (matches.Length == 1) lines[matches[0].index] = "output_name = " + output;
        else lines.Add("output_name = " + output);
        var temporary = path + ".new";
        await File.WriteAllLinesAsync(temporary, lines, new UTF8Encoding(false), cancellationToken);
        File.Move(temporary, path, true);
    }

    public async Task ConfigureTabletAsync(int? orientation, bool? autoOrientation, string? displayOutput, CancellationToken cancellationToken,
        int? width = null, int? height = null, int? refreshRate = null)
    {
        if (orientation.HasValue && orientation is not (0 or 90 or 180 or 270))
            throw new ArgumentException("屏幕方向只能为 0、90、180 或 270 度。");
        if (displayOutput is not null && !Regex.IsMatch(displayOutput, @"^\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}$"))
            throw new ArgumentException("虚拟屏幕标识无效，请重新检测屏幕。");
        if (width.HasValue != height.HasValue || width is < 200 or > 16384 || height is < 200 or > 16384 || refreshRate is < 24 or > 240)
            throw new ArgumentException("副屏分辨率或刷新率无效，请重新检测接收设备。");
        await EnsureAsync("tablet", cancellationToken);
        var settings = File.ReadAllLines(ConfigFor("tablet")).Where(line => line.Contains('=')).Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (displayOutput is not null) settings["output_name"] = displayOutput;
        // Explicit dimensions are the actual, already-rotated negotiated mode.
        // Legacy orientation-only calls preserve the configured panel dimensions.
        if (width.HasValue && height.HasValue)
            settings["dd_manual_resolution"] = $"{width.Value}x{height.Value}";
        else if (orientation.HasValue && settings.TryGetValue("dd_manual_resolution", out var previousResolution))
        {
            var dimensions = previousResolution.Split('x');
            if (dimensions.Length == 2 && int.TryParse(dimensions[0], out var previousWidth) && int.TryParse(dimensions[1], out var previousHeight))
            {
                var longer = Math.Max(previousWidth, previousHeight); var shorter = Math.Min(previousWidth, previousHeight);
                settings["dd_manual_resolution"] = orientation is 90 or 270 ? $"{shorter}x{longer}" : $"{longer}x{shorter}";
            }
        }
        if (width.HasValue)
        {
            settings["dd_resolution_option"] = "manual";
        }
        // Keep the negotiated dimensions as metadata. The verified product
        // helper has already applied them before launching the capture engine.
        settings["dd_configuration_option"] = "disabled";
        settings["dd_config_revert_on_disconnect"] = "disabled";
        if (refreshRate.HasValue)
        {
            settings["dd_refresh_rate_option"] = "manual";
            settings["dd_manual_refresh_rate"] = refreshRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var temporary = ConfigFor("tablet") + ".new";
        await File.WriteAllLinesAsync(temporary, SerializeSettings(settings), new UTF8Encoding(false), cancellationToken);
        File.Move(temporary, ConfigFor("tablet"), true);
        if (orientation.HasValue || autoOrientation.HasValue || width.HasValue || refreshRate.HasValue)
        {
            var preferences = Path.Combine(DirectoryFor("tablet"), "display-preferences.json");
            var temporaryPreferences = preferences + ".new";
            await File.WriteAllTextAsync(temporaryPreferences, JsonSerializer.Serialize(new { orientation, autoOrientation, width, height, refreshRate }), cancellationToken);
            File.Move(temporaryPreferences, preferences, true);
        }
    }

    public bool HasTabletOutput()
    {
        var config = ConfigFor("tablet");
        return File.Exists(config) && File.ReadLines(config).Any(line => line.TrimStart().StartsWith("output_name = {", StringComparison.OrdinalIgnoreCase));
    }
}
