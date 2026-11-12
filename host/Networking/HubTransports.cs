using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamHub.Host.Networking;

/// <summary>A provider chooses account/control-plane and published addresses; it never grants streaming trust.</summary>
public interface HubTransport
{
    string Id { get; }
    string Label { get; }
    bool UsesAccount { get; }
    string[] Addresses(IEnumerable<string> local, TransportSettings settings);
}

public sealed record TransportSettings(string Provider = "tailscale", string ServerUrl = "", string ManualAddresses = "")
{
    [JsonIgnore] public HubTransport Transport => HubTransports.Get(Provider);
    [JsonIgnore] public string ControlUrl => Provider == "self-hosted" ? ServerUrl : HubTransports.OfficialUrl;
    public TransportSettings Validate() => new(Transport.Id,
        Provider == "self-hosted" ? HubTransports.ServerUrl(ServerUrl) : "",
        Provider == "manual" ? string.Join(",", HubTransports.ManualAddresses(ManualAddresses)) : "");
}

public static class HubTransports
{
    public const string OfficialUrl = "https://controlplane.tailscale.com";
    private sealed record Provider(string Id, string Label, bool UsesAccount) : HubTransport
    {
        public string[] Addresses(IEnumerable<string> local, TransportSettings settings) => Id == "manual"
            ? ManualAddresses(settings.ManualAddresses) : UsableIpv4.Filter(local.Where(address => Id != "lan" || !address.StartsWith("100.", StringComparison.Ordinal)));
    }
    public static readonly HubTransport[] All =
    [new Provider("tailscale", "Tailscale 官方", true), new Provider("self-hosted", "自建服务区", true),
     new Provider("lan", "局域网 / USB", false), new Provider("manual", "手动地址", false)];
    public static HubTransport Get(string? id) => All.FirstOrDefault(p => p.Id == id) ??
        throw new ArgumentException("连接通道配置无效，请重新选择并保存。");

    private static Uri Https(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            value.Contains('\\') || !value.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Host.Length == 0 || uri.Host.EndsWith('.') || uri.Port is <= 0 or > 65535)
            throw new ArgumentException("HTTPS 地址格式无效。");
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        if (authority.Contains('@') || uri.Host.Any(c => c > 127)) throw new ArgumentException("HTTPS 地址格式无效。");
        return uri;
    }
    public static string ServerUrl(string? value)
    {
        try
        {
            var uri = Https(value);
            // Reject a path even when System.Uri would normalize it away.
            var suffix = value![(value!.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var slash = suffix.IndexOf('/');
            if (uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || slash >= 0 && suffix[slash..] != "/")
                throw new ArgumentException();
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }
        catch (ArgumentException) { throw new ArgumentException("服务区须为 HTTPS 根地址，例如 https://vpn.example.com，不含账号、路径或查询参数。"); }
    }
    public static string AuthorizationUrl(TransportSettings settings, string? value)
    {
        if (!settings.Transport.UsesAccount) return "";
        try
        {
            var uri = Https(value);
            if (settings.Provider == "tailscale")
            {
                if (uri.Port != 443 || !(uri.Host.Equals("tailscale.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith(".tailscale.com", StringComparison.OrdinalIgnoreCase))) return "";
            }
            else
            {
                var control = Https(settings.ServerUrl);
                if (!uri.Host.Equals(control.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != control.Port) return "";
            }
            return uri.AbsoluteUri;
        }
        catch (ArgumentException) { return ""; }
    }
    public static string ControlServer(string? value)
    {
        if (string.IsNullOrEmpty(value)) return OfficialUrl;
        var normalized = ServerUrl(value);
        return normalized == "https://login.tailscale.com" ? OfficialUrl : normalized;
    }
    public static string[] ManualAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("请填写电脑可达的 IPv4 地址，最多 8 个。");
        var addresses = value.Split([',', ';', '，', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Any(address => !UsableIpv4.IsUsable(address))) throw new ArgumentException("手动地址仅支持有效 IPv4，不含端口或网址。");
        var unique = addresses.Distinct(StringComparer.Ordinal).ToArray();
        if (unique.Length is 0 or > 8) throw new ArgumentException("请填写 1 至 8 个手动地址。");
        return unique;
    }
}

/// <summary>Atomic provider preferences, separate from daemon identities and binding secrets.</summary>
public sealed class TransportSettingsStore(string dataDirectory)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(dataDirectory), "transport.json");
    public TransportSettings Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            if (new FileInfo(_path).Length > 4096) throw new JsonException();
            var text = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (var key in new[] { "Provider", "ServerUrl", "ManualAddresses" })
                if (!document.RootElement.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) throw new JsonException();
            return (JsonSerializer.Deserialize<TransportSettings>(text) ?? throw new JsonException()).Validate();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidOperationException("连接通道配置损坏，请重新选择并保存；已停止自动组网。", error); }
    }
    public void Save(TransportSettings settings)
    {
        var normalized = settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, normalized);
                file.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
