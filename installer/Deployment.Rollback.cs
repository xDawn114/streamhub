using System.IO;
using System.Diagnostics;
using System.ServiceProcess;
using StreamHub.Host.Runtime;

namespace StreamHub.Installer;

internal sealed partial class Deployment
{
    internal static bool LaunchRollbackWorker(InstallOptions options, string[] args)
    {
        var deployment = new Deployment(options, _ => { });
        var executable = Environment.ProcessPath ?? throw new IOException("无法取得恢复程序路径。");
        if (!Path.GetFullPath(executable).StartsWith(deployment._root + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return false;
        deployment.ValidateExisting(true); deployment.ValidateServiceOwnership(); deployment.EnsureProtectedData();
        var directory=Path.Combine(deployment._dataRoot,"installer","runner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var worker=Path.Combine(directory,"StreamHub-rollback.exe"); File.Copy(executable,worker);
        var start=new ProcessStartInfo(worker) { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory };
        foreach(var arg in args)start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--wait-parent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        _=Process.Start(start) ?? throw new IOException("无法启动独立恢复程序。"); return true;
    }
    private sealed class MaintenanceLease(RuntimeClient client, string id) : IDisposable
    {
        public void Dispose() { try { _ = client.SendAsync(new("maintenance-end", RequestId:id)).GetAwaiter().GetResult(); } catch { } }
    }
    private IDisposable AcquireMaintenance()
    {
        if (!ServiceRunning()) return new MaintenanceLease(new RuntimeClient(),Guid.NewGuid().ToString("N"));
        var client=new RuntimeClient();var id=Guid.NewGuid().ToString("N");
        var response=client.SendAsync(new("maintenance-begin",RequestId:id)).GetAwaiter().GetResult();
        if(!response.Success) throw new IOException("尚不能安全升级或恢复："+response.Message);
        return new MaintenanceLease(client,id);
    }
    public void Rollback()
    {
        ValidateExisting(true); ValidateServiceOwnership(); EnsureProtectedData();
        var catalog=new RollbackCatalog(Path.Combine(_dataRoot,"installer"),_root);
        var record=catalog.Read();var backup=catalog.Verify(record);
        using var maintenance=AcquireMaintenance();
        var transaction=Guid.NewGuid().ToString("N");
        var current=Path.Combine(_dataRoot,"installer",transaction,"backup");
        RollbackRecord? forward=null;
        Log("正在校验并恢复上个版本……");
        RollbackTransaction.Execute(_root,backup,current,StopOwnedProcesses,
            ()=> { forward=catalog.Capture(transaction);_=catalog.Verify(record); },
            restored=>
            {
                RegisterService(); ConfigureFirewall(true); RegisterShortcuts(); RegisterUninstall(); StartRestoredService();
                if(restored)catalog.Commit(forward!);
            });
        Log("已恢复上个版本，绑定和个人设置保留。");
    }
    private static void StartRestoredService()
    {
        using var service=new ServiceController(ServiceName);
        if(service.Status!=ServiceControllerStatus.Running)service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(35));
    }
}
