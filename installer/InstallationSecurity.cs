using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StreamHub.Installer;

internal static class InstallationSecurity
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static string InteractiveOwnerSid()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var user = ReadSessionString(session, 5);
        var domain = ReadSessionString(session, 7);
        if (!string.IsNullOrWhiteSpace(user))
        {
            try { return ((SecurityIdentifier)new NTAccount(domain, user).Translate(typeof(SecurityIdentifier))).Value; }
            catch (IdentityNotMappedException) { }
        }
        return WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法确认安装账户，请在已登录的桌面会话中安装。");
    }

    internal static void ProtectData(string data, string ownerSid)
    {
        Deployment.ValidateRoot(data);
        Directory.CreateDirectory(data);
        VerifyNoLinks(data);
        SetDirectoryAcl(data, null);
        var logs = Path.Combine(data, "logs");
        Directory.CreateDirectory(logs);
        var owner = new SecurityIdentifier(ownerSid);
        ApplyRecursive(data, logs, owner);
        File.WriteAllText(Path.Combine(data, "owner.sid"), owner.Value, new System.Text.UTF8Encoding(false));
        SetFileAcl(Path.Combine(data, "owner.sid"), null);
    }

    private static void ApplyRecursive(string directory, string logs, SecurityIdentifier owner)
    {
        var isLog = directory.Equals(logs, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(logs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        SetDirectoryAcl(directory, isLog ? owner : null);
        foreach (var file in Directory.EnumerateFiles(directory)) SetFileAcl(file, isLog ? owner : null);
        foreach (var child in Directory.EnumerateDirectories(directory)) ApplyRecursive(child, logs, owner);
    }

    private static void SetDirectoryAcl(string path, SecurityIdentifier? logReader)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(AdminSid);
        foreach (var sid in new[] { SystemSid, AdminSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (logReader is not null)
            acl.AddAccessRule(new FileSystemAccessRule(logReader, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    private static void SetFileAcl(string path, SecurityIdentifier? logReader)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(AdminSid);
        foreach (var sid in new[] { SystemSid, AdminSid }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        if (logReader is not null) acl.AddAccessRule(new FileSystemAccessRule(logReader, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    private static void VerifyNoLinks(string directory)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(child);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("产品数据目录包含链接，权限修复已停止。");
            if (attributes.HasFlag(FileAttributes.Directory)) VerifyNoLinks(child);
        }
    }

    private static string ReadSessionString(int session, int informationClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, informationClass, out var buffer, out _)) return "";
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int informationClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
}
