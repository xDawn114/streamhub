using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal interface IHostLoginSecretStore
{
    bool Configured { get; }
    void Save(string password);
    string Read();
    void Clear();
}

/// <summary>Service-only DPAPI ciphertext, never an API payload or a backup of Android credentials.</summary>
internal sealed class HostLoginSecretStore : IHostLoginSecretStore
{
    private readonly string _directory, _path;
    private readonly byte[] _entropy;
    public HostLoginSecretStore(string dataDirectory, string ownerSid)
    {
        _ = new SecurityIdentifier(ownerSid);
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "host-login");
        _path = Path.Combine(_directory, "password.dpapi");
        _entropy = Encoding.UTF8.GetBytes("StreamHub.HostLogin.v1:" + ownerSid);
    }

    public bool Configured => File.Exists(_path);

    private void SecureDirectory()
    {
        // Machine DPAPI alone does not isolate Windows users. Only SYSTEM/admins
        // can access this directory; the owner configures it through authenticated IPC.
        if (!WindowsIdentity.GetCurrent().IsSystem)
            throw new InvalidOperationException("电脑密码只能由已安装的后台服务保存。");
        for (var parent = new DirectoryInfo(_directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("电脑密码目录不能经过链接或重解析点。");
        Directory.CreateDirectory(_directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        new DirectoryInfo(_directory).SetAccessControl(security);
        if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("电脑密码文件不能是链接。");
    }

    public void Save(string password)
    {
        DesktopInputPolicy.ValidatePassword(password);
        SecureDirectory();
        byte[] plain = Encoding.UTF8.GetBytes(password), encrypted = [];
        string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".new");
        try
        {
            encrypted = ProtectedData.Protect(plain, _entropy, DataProtectionScope.LocalMachine);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(encrypted); stream.Flush(true); }
            File.Move(temporary, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string Read()
    {
        SecureDirectory();
        if (!Configured || new FileInfo(_path).Length is <= 0 or > 4096)
            throw new InvalidOperationException("请在电脑端重新保存 Windows 密码。");
        byte[] encrypted = File.ReadAllBytes(_path), plain = [];
        try
        {
            plain = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.LocalMachine);
            string password = Encoding.UTF8.GetString(plain);
            DesktopInputPolicy.ValidatePassword(password);
            return password;
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); CryptographicOperations.ZeroMemory(plain); }
    }

    public void Clear()
    {
        SecureDirectory();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
