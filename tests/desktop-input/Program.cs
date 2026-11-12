using StreamHub.Host.Runtime;

int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
Check(DesktopInputPolicy.CanFill(true,true,true,true,true,true,"密码"),"Current user's focused Windows password permits filling.");
Check(DesktopInputPolicy.CanFill(true,true,true,true,true,true,"Password"),"English password control permits filling.");
foreach(string name in new[]{"PIN","Windows Hello PIN","用户名","","Other"})
    Check(!DesktopInputPolicy.CanFill(true,true,true,true,true,true,name),"Unsupported credential controls are rejected.");
for(int denied=0;denied<6;denied++) {
    bool[] states=Enumerable.Repeat(true,6).ToArray();states[denied]=false;
    Check(!DesktopInputPolicy.CanFill(states[0],states[1],states[2],states[3],states[4],states[5],"密码"),"Each independent desktop/focus/identity guard denies input.");
}
foreach(string? invalid in new[]{null,"","a\nb","a\rb","a\0b",new string('a',257)}) {
    bool rejected=false;try{DesktopInputPolicy.ValidatePassword(invalid);}catch(ArgumentException){rejected=true;}
    Check(rejected,"Malformed password rejected before helper launch.");
}
DesktopInputPolicy.ValidatePassword("test-中文-🙂");checks++;
Check(SessionExitPolicy.ShouldLock(true,"user",true),"Explicit opted-in user exit locks.");
Check(!SessionExitPolicy.ShouldLock(true,"internal",true),"Rotation/resolution reconnect never locks.");
Check(!SessionExitPolicy.ShouldLock(false,"user",true),"A stale session cannot lock a new one.");
Check(!SessionExitPolicy.ShouldLock(true,"user",false),"Default exit remains unlocked.");
Console.WriteLine($"{checks} desktop input and exit-lock checks passed; no desktop input or locking executed.");
if(args.Length==1) {
    var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    var assembly=System.Reflection.Assembly.LoadFrom(args[0]);
    var runtimeType=assembly.GetType("StreamHub.Service.HostRuntime",true)!;
    var requestType=assembly.GetType("StreamHub.Host.Runtime.RuntimeRequest",true)!;
    var sessionType=assembly.GetType("StreamHub.Host.Runtime.LiveSessionState",true)!;
    var runtime=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(runtimeType);
    using var gate=new SemaphoreSlim(1,1);
    runtimeType.GetField("_gate",flags)!.SetValue(runtime,gate);
    runtimeType.GetField("_maintenance",flags)!.SetValue(runtime,Activator.CreateInstance(assembly.GetType("StreamHub.Host.Runtime.MaintenanceWindow",true)!,new object?[]{null}));
    var session=Activator.CreateInstance(sessionType)!;
    runtimeType.GetField("_remoteSession",flags)!.SetValue(runtime,session);
    var now=DateTimeOffset.UtcNow;
    sessionType.GetMethod("Prepare")!.Invoke(session,["lease-owner","client-owner",now]);
    sessionType.GetMethod("Heartbeat")!.Invoke(session,["lease-owner","client-owner",true,now,true,true,false]);
    var handle=runtimeType.GetMethod("HandleAsync",flags)!;
    foreach(var scenario in new[]{("tablet","lease-owner","client-owner"),("remote","lease-other","client-owner"),("remote","lease-owner","client-other")})
    foreach(var action in new[]{"status","login-fill"}) {
        var parameters=requestType.GetConstructors().Single().GetParameters();
        var values=parameters.Select(x=>x.HasDefaultValue?x.DefaultValue:null).ToArray();
        for(int i=0;i<parameters.Length;i++)values[i]=parameters[i].Name switch {
            "Command"=>"remote-desktop","Profile"=>scenario.Item1,"LeaseId"=>scenario.Item2,"ClientId"=>scenario.Item3,
            "ClipboardAction"=>action,"LoginSecret"=>"synthetic-test-only",_=>values[i]};
        var pending=(Task)handle.Invoke(runtime,[Activator.CreateInstance(requestType,values),CancellationToken.None])!;
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        var response=pending.GetType().GetProperty("Result")!.GetValue(pending)!;
        Check(!(bool)response.GetType().GetProperty("Success")!.GetValue(response)! && gate.CurrentCount==1,
            "Production desktop status/password endpoint must reject secondary-display, stale-lease and foreign-device requests before helper access.");
    }
    Console.WriteLine("6 production desktop-input authorization checks passed; no active service, helper, password or lock operation executed.");
}
