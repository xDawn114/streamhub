using System.Net;
using System.Net.Sockets;

namespace StreamHub.Host.Networking;

public static class UsableIpv4
{
    public static bool IsUsable(string? value)
    {
        if (value is null || value.Split('.') is not { Length: 4 } parts ||
            parts.Any(p => p.Length is < 1 or > 3 || p.Any(c => c < '0' || c > '9') || (p.Length > 1 && p[0] == '0'))) return false;
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] is > 0 and < 224 && b[0] != 127 && !(b[0] == 169 && b[1] == 254);
    }
    public static string[] Filter(IEnumerable<string> values) => values.Select(v => v.Trim()).Where(IsUsable)
        .Distinct(StringComparer.Ordinal).ToArray();
}
