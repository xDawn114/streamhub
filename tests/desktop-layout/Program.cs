using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Runtime;
internal static class Program
{
 [STAThread] static void Main(string[] args)
 {
  string root=Path.GetFullPath(args.Single());Directory.CreateDirectory(root);
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var checks=new List<string>();var calls=new List<RuntimeRequest>();
  bool paused=false,available=true,responding=false;var now=DateTimeOffset.UtcNow;
  var binding=new LocalBindingSnapshot(null,null,null);
  var bound=new ControlDeviceList(false,[new("active-device-123456","fixture-client","PA2573",ControlScope.Remote|ControlScope.Tablet,false,now,"宿舍平板","tablet",now),new("revoked-device-654321","fixture-old-client","PA2573",ControlScope.All,true,now.AddDays(-1)),new("phone-device-112233","fixture-phone","PA2573",ControlScope.Remote,false,now,null,"phone")]);
  var files=new TransferSnapshot(new(),[new("fixture-client","PA2573",new string('A',64))],[]);
  var engine=new EngineStatus("remote",true,true,null,48989,true,"等待设备连接");
  var preferences=Path.Combine(root,"preferences.json");File.WriteAllText(preferences,"{\"Theme\":\"light\",\"ReduceMotion\":true}");
  var runtime=new RuntimeClient((r,_)=>{
   calls.Add(r);if(r.Command=="status"&&!available)return Task.FromResult(new RuntimeResponse(false,"后台不可用"));
   if(r.Command=="binding-start")binding=new("123456",now.AddMinutes(3),null);
   if(r.Command=="binding-cancel")binding=new(null,null,null);
   if(r.Command=="connections-pause")paused=true;if(r.Command=="connections-resume")paused=false;
   if(r.Command=="control-device-rename")bound=bound with{Devices=bound.Devices.Select(d=>d.Id==r.RequestId?d with{Alias=r.Name}:d).ToArray()};
   string? payload=r.Command switch{
    "network-config"=>"{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
    "files-status"=>JsonSerializer.Serialize(files,RuntimeProtocol.JsonOptions),
    "control-device-list" or "control-device-rename"=>JsonSerializer.Serialize(bound,RuntimeProtocol.JsonOptions),
    "binding-start" or "binding-cancel" or "binding-status"=>JsonSerializer.Serialize(binding,RuntimeProtocol.JsonOptions),
    "host-policy-status"=>JsonSerializer.Serialize(new HostPolicySnapshot(),RuntimeProtocol.JsonOptions),
    "receiver-list"=>JsonSerializer.Serialize(new ReceiverSnapshot(responding?[new("fixture-client","PA2573",2560,1600,60,[0,90],now)]:[],null),RuntimeProtocol.JsonOptions),_=>null};
   return Task.FromResult(new RuntimeResponse(true,"隔离状态已更新",new RuntimeStatus("fixture-id","我的电脑",engine with{Running=!paused,Healthy=!paused,Desired=!paused},engine with{Profile="tablet",Running=false,Desired=false,Healthy=false},new(false,false,null,"未启用"),"fixture",root,now,ConnectionsPaused:paused),payload));
  });
  var w=new MainWindow(runtime,preferences){Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,Width=940,Height=660};
  T N<T>(string n)where T:class=>(T)w.FindName(n);
  void Invoke(string m,object? s=null)=>typeof(MainWindow).GetMethod(m,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,[s??w,new RoutedEventArgs()]);
  void Async(string m){typeof(MainWindow).GetMethod(m,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,null);Pump();}
  void Refresh(){Invoke("RefreshButton_Click");Pump();}
  void Check(bool b,string label){if(!b)throw new Exception(label);checks.Add(label);}
  object P(object r,string n)=>r.GetType().GetProperty(n)!.GetValue(r)!;
  w.Loaded+=(_,_)=>w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
   try{
    var tick=TimeSpan.Zero;var readiness=new ConnectionReadiness(()=>tick);
    var waiting=new RuntimeStatus("fixture-id","电脑",engine with{Healthy=false},engine with{Streaming=false},new(false,false,null,"未启用"),"fixture",root,now);
    Check(readiness.Observe(waiting)=="准备中","startup initially waits");
    tick=TimeSpan.FromSeconds(44);Check(readiness.Observe(waiting)=="准备中","startup still waits before timeout");
    tick=TimeSpan.FromSeconds(45);Check(readiness.Observe(waiting).Contains("引擎未启动")&&readiness.TimedOut,"startup exits preparation at 45 seconds");
    Check(readiness.Observe(waiting with{Remote=waiting.Remote with{ProcessId=999}}).Contains("引擎未启动"),"process retry does not restart timeout");
    Check(readiness.Observe(waiting with{Remote=engine})=="接受连接"&&!readiness.TimedOut,"healthy engine clears timeout");
    readiness.Observe(waiting);tick+=TimeSpan.FromSeconds(45);readiness.Observe(waiting);
    Check(readiness.Observe(waiting with{ConnectionsPaused=true})=="已暂停被控"&&!readiness.TimedOut,"pause clears startup wait");
    Check(readiness.Observe(waiting)=="准备中","resume starts a fresh wait");
    tick+=TimeSpan.FromSeconds(45);readiness.Observe(waiting);
    Check(readiness.Observe(waiting with{StartedAt=now.AddMinutes(1)})=="准备中","new service starts a fresh wait");
    Check(readiness.Observe(null)=="后台未连接"&&!readiness.TimedOut,"unavailable service clears wait");
    Pump();var pages=N<TabControl>("Pages");
    Check(pages.SelectedItem==N<TabItem>("HomePage")&&N<TextBlock>("HomeSummaryText").IsVisible,"startup selects and displays the unified homepage");
    Check(w.FindName("DevicesNavigation")==null,"local and devices navigation merged");
    Check(N<ItemsControl>("BoundDevicesList").Items.Count==2&&N<ItemsControl>("HistoricalDevicesList").Items.Count==1&&!N<Expander>("DeviceHistoryExpander").IsExpanded,"revoked duplicate stays in collapsed history");
    var row=N<ItemsControl>("BoundDevicesList").Items.Cast<object>().Single(r=>(string)P(r,"Id")=="active-device-123456");
    Check((string)P(row,"Name")=="宿舍平板"&&((string)P(row,"LastContact")).Contains("最后联系")&&(string)P(row,"TypeText")=="平板","alias, type and genuine contact shown");
    Check((Visibility)P(row,"FileActionVisibility")==Visibility.Visible,"pending file device offers contextual verification");
    Check(N<TextBlock>("HomeSummaryText").Text.Contains("接受连接")&&N<TextBlock>("HomePrivacyText").Visibility==Visibility.Collapsed,"home exposes acceptance without false privacy claim");
    Check(!N<Button>("SendFileButton").IsEnabled&&N<TextBlock>("FileTasksEmptyText").Visibility==Visibility.Visible&&N<ListBox>("FileTaskList").Visibility==Visibility.Collapsed,"disabled transfers show empty state and disable sending");
    Check(N<Button>("FileInlineAllowButton").Visibility==Visibility.Collapsed,"file page keeps permissions in device dialog");
    Invoke("OpenFileAccessSettings_Click");Pump();
    Check(N<Grid>("DevicePermissionsDialog").IsVisible&&N<Expander>("FileAccessDetails").IsExpanded,"file permissions link opens device dialog");
    Invoke("CloseDevicePermissions_Click");Pump();
    Check(N<Expander>("DeviceHistoryExpander").Header.ToString()=="历史设备（1）"&&(string)P(row,"ShortId")=="123456","history count and short id are labeled");
    files=files with{Settings=new(true)};Async("RefreshFilesAsync");
    Check(N<TextBlock>("FileTasksEmptyText").Text.Contains("暂无传输任务")&&!N<Button>("SendFileButton").IsEnabled,"enabling transfer does not grant device access");
    Invoke("OpenTabletPopover_Click");Pump();var popup=N<Popup>("TabletPopover");
    row=N<ItemsControl>("TabletReceiverList").Items[0];
    Check(!(bool)P(row,"CanConnect")&&((string)P(row,"Detail")).Contains("网络"),"missing response does not assert offline or toggle disabled");
    responding=true;Async("RefreshReceiversAsync");
    row=N<ItemsControl>("TabletReceiverList").Items.Cast<object>().Single(r=>(string)P(r,"ClientId")=="fixture-client");
    Check((bool)P(row,"CanConnect"),"authorized responding receiver enabled");
    var phone=N<ItemsControl>("TabletReceiverList").Items.Cast<object>().Single(r=>(string)P(r,"ClientId")=="fixture-phone");
    Check(!(bool)P(phone,"CanConnect")&&((string)P(phone,"Detail")).Contains("未授权"),"missing scope has distinct disabled reason");
    Save((FrameworkElement)popup.Child,Path.Combine(root,"tablet-popover.png"));
    responding=false;int before=calls.Count(c=>c.Command=="tablet-request");Invoke("ConnectReceiver_Click",new Button{Tag="fixture-client"});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request")==before,"stale receiver is refreshed before inviting");popup.IsOpen=false;
    Invoke("PauseConnections_Click");Pump();
    Check(paused&&N<TextBlock>("AcceptanceLabel").Text.Contains("暂停")&&N<Button>("PauseConnectionsButton").Content.ToString()!.Contains("恢复"),"topbar pause uses confirmed backend state");
    Invoke("PauseConnections_Click");Pump();Check(!paused,"same topbar entry resumes acceptance");
    Invoke("OpenDevices_Click");Pump();
    Check(N<Grid>("BindingDialog").Visibility==Visibility.Visible&&N<TextBlock>("BindingCodeText").Text=="123456"&&N<TextBlock>("BindingCountdownText").Text.Contains("有效期剩余"),"binding modal preserves six-digit code and countdown");
    binding=binding with{Request=new("fixture-binding","fixture-client","PA2573","246 802","pending",now.AddMinutes(1))};Refresh();
    Check(N<StackPanel>("BindingComparisonPanel").Visibility==Visibility.Visible&&N<TextBlock>("BindingComparisonText").Text=="246 802","comparison remains visible before approval");
    Save((FrameworkElement)w.Content,Path.Combine(root,"binding-dialog.png"));Invoke("CloseBinding_Click");Pump();
    Check(binding.Code==null&&N<Grid>("BindingDialog").Visibility==Visibility.Collapsed&&calls.Any(c=>c.Command=="binding-cancel"),"closing dialog invalidates invitation without changing grants");
    Invoke("OpenDeviceAlias_Click",new Button{Tag="active-device-123456"});N<TextBox>("DeviceAliasBox").Text="随身平板";Invoke("SaveDeviceAlias_Click");Pump();
    Check(bound.Devices[0].Alias=="随身平板"&&N<Grid>("AliasDialog").Visibility==Visibility.Collapsed,"alias saves by credential id, never model name");
    Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionText").Text.Contains("内置异地连接：未启用")&&N<Button>("AccountPrimaryButton").Content.ToString()=="启用异地连接","account separates live status from configured transport");
    Save((FrameworkElement)N<Popup>("AccountPopover").Child,Path.Combine(root,"account-popover.png"));N<Popup>("AccountPopover").IsOpen=false;
    foreach(int theme in new[]{1,2}){
     N<ComboBox>("ThemeChoice").SelectedIndex=theme;
     foreach(string page in new[]{"HomePage","FilesPage"}){
      pages.SelectedItem=N<TabItem>(page);w.UpdateLayout();Pump();
      Check(N<TabItem>(page).Content is Grid || ((ScrollViewer)N<TabItem>(page).Content).ScrollableWidth<1,$"{theme}/{page} has no horizontal overflow at minimum size");Save((FrameworkElement)w.Content,Path.Combine(root,$"{page}-{theme}.png"));
     }
     Invoke("OpenTabletSettings_Click");w.UpdateLayout();Pump();
     Check(((ScrollViewer)N<TabItem>("TabletSettingsCategory").Content).ScrollableWidth<1&&N<ComboBox>("OrientationBox").IsVisible,"tablet preferences reachable from settings");
     Save((FrameworkElement)w.Content,Path.Combine(root,$"tablet-settings-{theme}.png"));
    }
    available=false;Refresh();Check(!N<Button>("PauseConnectionsButton").IsEnabled&&N<TextBlock>("AcceptanceLabel").Text.Contains("未连接"),"backend failure disables readiness actions");
    Check(!calls.Any(c=>c.Command is "control-device-scope" or "control-device-revoke" or "files-allow" or "host-policy-save" or "network-provider"),"presentation never silently mutates permissions or transport");
    File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,count=checks.Count,checks,installedServiceCalls=0,operatingSystemInputSent=false},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS {checks.Count} focused desktop checks.");
   }catch(Exception ex){File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Console.Error.WriteLine(ex);Environment.ExitCode=1;}
   finally{w.DisposeWindow();w.Close();app.Shutdown();}
  }));w.Show();app.Run();
 }
 static void Pump(int ms=80){var frame=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();frame.Continue=false;};t.Start();Dispatcher.PushFrame(frame);}
 static void Save(FrameworkElement e,string path){e.UpdateLayout();var b=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(e);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(path);enc.Save(f);}
}
