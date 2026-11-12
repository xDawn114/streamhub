using System.Reflection;
using System.Runtime.CompilerServices;

internal static class SessionControlChecks
{
    internal static async Task RunAsync(string assemblyPath)
    {
        var assembly=Assembly.LoadFrom(assemblyPath);
        var runtimeType=assembly.GetType("StreamHub.Service.HostRuntime",true)!;
        var requestType=assembly.GetType("StreamHub.Host.Runtime.RuntimeRequest",true)!;
        var sessionType=assembly.GetType("StreamHub.Host.Runtime.LiveSessionState",true)!;
        var runtime=RuntimeHelpers.GetUninitializedObject(runtimeType);
        runtimeType.GetField("_maintenance",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(runtime,
            Activator.CreateInstance(assembly.GetType("StreamHub.Host.Runtime.MaintenanceWindow",true)!,new object?[]{null}));
        using var gate=new SemaphoreSlim(1,1);
        runtimeType.GetField("_gate",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(runtime,gate);
        var session=Activator.CreateInstance(sessionType)!;
        runtimeType.GetField("_remoteSession",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(runtime,session);
        var now=DateTimeOffset.UtcNow;
        sessionType.GetMethod("Prepare")!.Invoke(session,["lease-owner","client-owner",now]);
        sessionType.GetMethod("Heartbeat")!.Invoke(session,["lease-owner","client-owner",true,now,true,true,false]);
        var handle=runtimeType.GetMethod("HandleAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        foreach(var command in new[]{"remote-privacy","remote-reconfigure","remote-desktop"})
        foreach(var scenario in new[]{("tablet","lease-owner","client-owner"),("remote","lease-other","client-owner"),("remote","lease-owner","client-other")})
        {
            var parameters=requestType.GetConstructors().Single().GetParameters();
            var values=parameters.Select(x=>x.HasDefaultValue?x.DefaultValue:null).ToArray();
            for(int i=0;i<parameters.Length;i++) values[i]=parameters[i].Name switch {
                "Command"=>command,"Profile"=>scenario.Item1,"LeaseId"=>scenario.Item2,"ClientId"=>scenario.Item3,"PrivacyEnabled"=>true,_=>values[i] };
            var request=Activator.CreateInstance(requestType,values)!;
            var pending=(Task)handle.Invoke(runtime,[request,CancellationToken.None])!;
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            var response=pending.GetType().GetProperty("Result")!.GetValue(pending)!;
            if((bool)response.GetType().GetProperty("Success")!.GetValue(response)! ||
                (bool)runtimeType.GetField("_privacyScreenEnabled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime)! || gate.CurrentCount!=1)
                throw new Exception("Unowned session changed privacy or held the media gate.");
        }
        Console.WriteLine("Production session controls reject secondary-display, stale-lease and different-device requests (9 cases).");
        foreach(bool live in new[]{false,true})
        foreach(string? reason in new string?[]{null,"internal","network","user"})
        foreach(bool? enabled in new bool?[]{null,false,true})
            if(StreamHub.Host.Runtime.SessionExitPolicy.ShouldLock(live,reason,enabled)!=(live && reason=="user" && enabled==true))
                throw new Exception("Exit lock must require explicit user exit from a matching live remote session.");
        Console.WriteLine("Exit lock rejects internal reconnect, lost connection, stale lease and disabled preference (24 cases); no real lock requested.");
        object IdentityRequest()
        {
            var parameters=requestType.GetConstructors().Single().GetParameters();
            var values=parameters.Select(x=>x.HasDefaultValue?x.DefaultValue:null).ToArray();
            for(int i=0;i<parameters.Length;i++) values[i]=parameters[i].Name switch {
                "Command"=>"media-identity","Profile"=>"remote","ClientCertificate"=>"invalid-certificate",_=>values[i] };
            return Activator.CreateInstance(requestType,values)!;
        }
        int ResponseStatus(Task pending)
        {
            var response=pending.GetType().GetProperty("Result")!.GetValue(pending)!;
            return (int)response.GetType().GetProperty("ErrorStatus")!.GetValue(response)!;
        }
        await gate.WaitAsync();
        var shortContention=(Task)handle.Invoke(runtime,[IdentityRequest(),CancellationToken.None])!;
        await Task.Delay(100);
        gate.Release();
        await shortContention.WaitAsync(TimeSpan.FromSeconds(2));
        if(ResponseStatus(shortContention)!=400 || gate.CurrentCount!=1)
            throw new Exception("A short status probe prevented media identity validation.");
        await gate.WaitAsync();
        var longContention=(Task)handle.Invoke(runtime,[IdentityRequest(),CancellationToken.None])!;
        try {
            await longContention.WaitAsync(TimeSpan.FromSeconds(2));
            if(ResponseStatus(longContention)!=409 || gate.CurrentCount!=0)
                throw new Exception("Busy media identity escaped the bounded gate or released another owner's lock.");
        } finally {gate.Release();}
        Console.WriteLine("Media identity tolerates short lock contention and rejects long contention without releasing another owner's gate (2 cases).");
        // A display operation may own the gate for seconds. Receiver proof
        // must still reach its own live generation within the 1.5s HTTP budget.
        var heartbeatParameters=requestType.GetConstructors().Single().GetParameters();
        object HeartbeatRequest(string client)
        {
            var values=heartbeatParameters.Select(p=>p.HasDefaultValue?p.DefaultValue:null).ToArray();
            for(int i=0;i<values.Length;i++) values[i]=heartbeatParameters[i].Name switch {
                "Command"=>"remote-heartbeat", "LeaseId"=>"lease-owner", "ClientId"=>client,
                "StreamReady"=>true, "FastPrivacyHeartbeat"=>true, "BoundedPrivacyHeartbeat"=>true, _=>values[i] };
            return Activator.CreateInstance(requestType,values)!;
        }
        await gate.WaitAsync();
        try
        {
            foreach(var client in new[]{"client-owner","client-other"})
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                var heartbeat=(Task)handle.Invoke(runtime,[HeartbeatRequest(client),CancellationToken.None])!;
                await heartbeat.WaitAsync(TimeSpan.FromMilliseconds(500));
                var response=heartbeat.GetType().GetProperty("Result")!.GetValue(heartbeat)!;
                var success=(bool)response.GetType().GetProperty("Success")!.GetValue(response)!;
                if(success!=(client=="client-owner") || gate.CurrentCount!=0 || watch.ElapsedMilliseconds>=500)
                    throw new Exception("Heartbeat identity or independence from media gate failed.");
            }
        }
        finally {gate.Release();}
        Console.WriteLine("Ready heartbeats bypass a blocked media gate; stale devices remain rejected (2 cases).");
    }
}
