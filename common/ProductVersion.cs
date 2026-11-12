using System.Reflection;

namespace StreamHub;

public static class ProductVersion
{
    private static readonly Assembly ProductAssembly = typeof(ProductVersion).Assembly;
    public static string Name => ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? ProductAssembly.GetName().Version?.ToString(3) ?? "未知版本";
    public static int Code => int.TryParse(ProductAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(x => x.Key == "StreamHubVersionCode")?.Value, out var code) ? code : 0;
    public static bool IsInternal => ProductAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(x => x.Key == "StreamHubReleaseChannel")?.Value == "internal";
}
