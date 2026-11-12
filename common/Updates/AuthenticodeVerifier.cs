using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StreamHub.Updates;

public static class AuthenticodeVerifier
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr Callback, Sip; public uint Ui, Revocation, Choice; public IntPtr File;
        public uint Action; public IntPtr State, Url; public uint Flags, Context;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    public static Task VerifyAsync(string path, string[] allowedPublishers, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (allowedPublishers.Length == 0 || allowedPublishers.Any(p => !UpdatePolicy.IsHash(p)))
            throw new InvalidOperationException("尚未配置可信更新发布者。");
        var info = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Revocation = 1, Choice = 1, File = memory, Action = 1, Flags = 0x80 };
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0)
                throw new InvalidOperationException("Windows 未确认更新包的受信代码签名，已停止安装。");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            if (!allowedPublishers.Any(p => p.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("更新包发布者与内置身份不符，已停止安装。");
        }
        finally
        {
            data.Action = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<FileInfo>(memory); Marshal.FreeHGlobal(memory);
        }
    }, token);
}
