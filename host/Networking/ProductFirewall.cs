using System.Collections;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StreamHub.Host.Networking;

/// <summary>Only this adapter talks to Firewall COM. Tests inject objects instead of activating COM.</summary>
public sealed class WindowsProductFirewallAdapter : IProductFirewallAdapter, IDisposable
{
    private readonly object _policy;
    private readonly Func<object> _newRule;

    public WindowsProductFirewallAdapter() : this(Create("HNetCfg.FwPolicy2"), () => Create("HNetCfg.FWRule")) { }
    internal WindowsProductFirewallAdapter(object policy, Func<object> newRule) => (_policy, _newRule) = (policy, newRule);
    private static object Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)!)
        ?? throw new IOException("Windows 防火墙组件不可用。");

    public IReadOnlyList<ProductFirewallRule> ReadRules()
    {
        var result = new List<ProductFirewallRule>();
        object rulesObject = ((dynamic)_policy).Rules;
        try
        {
            foreach (object item in (IEnumerable)rulesObject)
            {
                try
                {
                    dynamic rule = item;
                    string name = rule.Name;
                    if (!NetworkAccessPolicy.IsOwnedName(name)) continue;
                    object? interfaces = rule.Interfaces;
                    result.Add(new(name, rule.Description ?? "", rule.ApplicationName ?? "", rule.Protocol,
                        rule.LocalPorts ?? "*", rule.RemoteAddresses ?? "*", rule.Profiles,
                        rule.LocalAddresses ?? "*", interfaces is Array values ? values.Cast<object>().Select(v => v.ToString()!).ToArray() : [],
                        rule.Grouping ?? "", rule.Direction, rule.Action, rule.Enabled, rule.RemotePorts ?? "*",
                        rule.InterfaceTypes ?? "All", rule.ServiceName ?? "", rule.EdgeTraversal));
                }
                finally { Release(item); }
            }
        }
        finally { Release(rulesObject); }
        return result;
    }

    public void Add(ProductFirewallRule definition)
    {
        if (!NetworkAccessPolicy.IsOwnedName(definition.Name)) throw new ArgumentException("拒绝创建非产品防火墙规则。");
        object item = _newRule();
        object rulesObject = ((dynamic)_policy).Rules;
        try
        {
            dynamic rule = item;
            rule.Name = definition.Name;
            rule.Description = definition.Description;
            rule.Grouping = definition.Grouping;
            rule.ApplicationName = definition.ApplicationName;
            rule.Protocol = definition.Protocol;
            rule.LocalPorts = definition.LocalPorts;
            rule.RemotePorts = definition.RemotePorts;
            rule.RemoteAddresses = definition.RemoteAddresses;
            rule.LocalAddresses = definition.LocalAddresses;
            rule.Direction = definition.Direction;
            rule.Action = definition.Action;
            rule.Profiles = definition.Profiles;
            rule.InterfaceTypes = definition.InterfaceTypes;
            rule.EdgeTraversal = definition.EdgeTraversal;
            if (definition.Interfaces is { Length: > 0 }) rule.Interfaces = definition.Interfaces.Cast<object>().ToArray();
            if (definition.ServiceName.Length > 0) rule.ServiceName = definition.ServiceName;
            // Enable only after every boundary is assigned. A setter failure cannot leave a broad rule installed.
            rule.Enabled = definition.Enabled;
            ((dynamic)rulesObject).Add(rule);
        }
        finally { Release(item); Release(rulesObject); }
    }

    public void Remove(string name)
    {
        if (!NetworkAccessPolicy.IsOwnedName(name)) throw new ArgumentException("拒绝移除非产品防火墙规则。");
        object rulesObject = ((dynamic)_policy).Rules;
        try { ((dynamic)rulesObject).Remove(name); }
        finally { Release(rulesObject); }
    }

    private static void Release(object value) { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public void Dispose() => Release(_policy);
}

/// <summary>Protected machine policy, separate from UI preferences and installation rollback snapshots.</summary>
public sealed class ProtectedNetworkAccessStore(string dataRoot) : INetworkAccessStore
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(dataRoot), "network-access");
    private string ConfigurationPath => Path.Combine(_directory, "policy.json");

    public NetworkAccessConfiguration? Read()
    {
        if (!Directory.Exists(_directory))
        {
            // GetAttributes distinguishes truly absent paths from ACL/I/O failures and dangling reparse points.
            try { _ = File.GetAttributes(_directory); }
            catch (DirectoryNotFoundException) { return null; }
            catch (FileNotFoundException) { return null; }
            throw new IOException("网络访问配置目录无效。");
        }
        ValidateDirectory();
        try
        {
            RequireNormal(ConfigurationPath, directory: false);
            ValidateFile(ConfigurationPath);
            using var input = new FileStream(ConfigurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > NetworkAccessConfiguration.MaximumBytes) throw new IOException("网络访问配置大小无效。");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            return NetworkAccessConfiguration.Decode(bytes);
        }
        catch (FileNotFoundException) { return null; }
    }

    public void Write(NetworkAccessConfiguration configuration)
    {
        EnsureDirectory();
        string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                output.Write(configuration.Encode());
                output.Flush(true);
            }
            // The protected parent prevents unprivileged replace/delete. Set file owner/ACL explicitly as well.
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            foreach (var sid in TrustedSids()) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(temporary).SetAccessControl(security);
            if (File.Exists(ConfigurationPath))
            {
                RequireNormal(ConfigurationPath, false);
                ValidateFile(ConfigurationPath);
                File.Replace(temporary, ConfigurationPath, null);
            }
            else File.Move(temporary, ConfigurationPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_directory))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            foreach (var sid in TrustedSids()) security.AddAccessRule(new FileSystemAccessRule(sid,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_directory).Create(security);
        }
        ValidateDirectory();
    }

    private static SecurityIdentifier[] TrustedSids() =>
    [new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)];

    private void ValidateDirectory()
    {
        RequireNormal(_directory, true);
        // InstallationSecurity protects dataRoot. Validate its parent cannot be replaced by an ordinary user too.
        var parent = Directory.GetParent(_directory) ?? throw new IOException("产品数据目录无效。");
        RequireNormal(parent.FullName, true);
        ValidateSecurity(parent.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        ValidateSecurity(new DirectoryInfo(_directory).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }

    private static void ValidateFile(string path) => ValidateSecurity(new FileInfo(path).GetAccessControl(
        AccessControlSections.Access | AccessControlSections.Owner));

    internal static void ValidateSecurity(FileSystemSecurity security)
    {
        var trusted = TrustedSids();
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!trusted.Any(sid => sid.Equals(owner))) throw new IOException("网络访问配置所有者无效。");
        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        // Explicit read access for the local owner is harmless; any unprivileged writer invalidates the document.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !trusted.Any(sid => sid.Equals(rule.IdentityReference)) &&
                (rule.FileSystemRights & mutation) != 0) throw new IOException("网络访问配置权限过宽，已拒绝读取或覆盖。");
    }

    private static void RequireNormal(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new IOException("网络访问配置不允许重解析点或异常路径。");
    }
}

public static class FirewallInterfaceInventory
{
    public static FirewallInterface[] Read() => NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
        new FirewallInterface(adapter.Id, adapter.Name, adapter.OperationalStatus == OperationalStatus.Up,
            adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback,
            UsableIpv4.Filter(adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address.ToString())))).ToArray();
}
