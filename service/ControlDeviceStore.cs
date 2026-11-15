using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

/// <summary>Service-only authorization database. No raw bearer token is persisted.</summary>
internal static class ControlDeviceStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("iControl.ControlDevices.v1");
    internal static ControlDeviceRegistry Open(string dataDirectory)
    {
        if (!WindowsIdentity.GetCurrent().IsSystem)
            throw new InvalidOperationException("设备授权记录只能由已安装的后台服务管理。");
        string directory = Path.Combine(Path.GetFullPath(dataDirectory), "control-devices");
        string path = Path.Combine(directory, "devices.dpapi");
        void Secure()
        {
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("设备授权目录不能经过链接。");
            Directory.CreateDirectory(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var kind in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            new DirectoryInfo(directory).SetAccessControl(security);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("设备授权记录不能是链接。");
        }
        Secure();
        byte[]? saved = null;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length is <= 0 or > 2097152) throw new InvalidDataException("设备授权记录大小无效。");
            saved = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
        }
        void Save(byte[] plain)
        {
            Secure();
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".new");
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(encrypted); stream.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        try { return new ControlDeviceRegistry(saved, Save); }
        finally { if (saved is not null) CryptographicOperations.ZeroMemory(saved); }
    }
}
