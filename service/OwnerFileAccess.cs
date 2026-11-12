using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

/// <summary>Network browsing opens files with the owner's interactive token, never the service token.</summary>
internal sealed class OwnerFileAccess(Func<string?> ownerSid, bool interactive, string productRoot) : ISharedFileAccess
{
    public T Run<T>(string? rootOwner,Func<T> operation)
    {
        var expected=ownerSid();
        if(string.IsNullOrEmpty(expected) || !string.IsNullOrEmpty(rootOwner) && rootOwner!=expected)
            throw new TransferError(403,"文件授权的 Windows 用户不匹配。");
        if(interactive)return new CurrentUserFileAccess().Run(expected,operation);
        var session=SessionLauncher.ActiveSession;
        if(session==uint.MaxValue || !WTSQueryUserToken(session,out var token))
            throw new TransferError(403,"请先登录授权的 Windows 用户后访问文件。");
        using(token)
        {
            using var identity=new WindowsIdentity(token.DangerousGetHandle());
            if(identity.User?.Value!=expected)throw new TransferError(403,"当前登录用户与文件授权用户不同。");
            return WindowsIdentity.RunImpersonated(token,operation);
        }
    }
    public void Recycle(string path)
    {
        var expected=ownerSid() ?? throw new TransferError(403,"文件授权用户不可用。");
        if(interactive) { new CurrentUserFileAccess().Recycle(path);return; }
        // Shell COM must run in a process whose primary token is the user,
        // not just a temporarily impersonated thread in a SYSTEM service.
        WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle,()=>{
            using var helper=SessionLauncher.Start(Path.Combine(productRoot,"service","串流中心后台.exe"),
                ["--recycle-file",path,expected],productRoot,false,expected);
            if(!helper.Process.WaitForExit(8000))throw new TransferError(409,"回收操作超时，请刷新文件列表确认状态。");
            if(helper.ExitCode!=0)throw new TransferError(409,"未能确认移入回收站，未尝试永久删除；请刷新列表。");
        });
    }
    [DllImport("wtsapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId,out SafeAccessTokenHandle token);
}
