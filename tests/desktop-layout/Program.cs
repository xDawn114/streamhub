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
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;
internal static class Program
{
 [STAThread] static void Main(string[] args)
 {
  string root=Path.GetFullPath(args.Single());Directory.CreateDirectory(root);
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var checks=new List<string>();var calls=new List<RuntimeRequest>();var typography=new List<object>();var scrollClearance=new List<object>();
  bool paused=false,available=true,responding=false,receiverAvailable=true,streaming=false,ending=false;var now=DateTimeOffset.UtcNow;
  var network=new NetworkEngineStatus(false,false,null,"未启用");
  var networkDetails=new NetworkStatus{Error="内置异地连接尚未启动，或后台服务暂时不可用。"};
  var transport=new TransportSettings("lan");bool takeoverAllowed=true;
  var binding=new LocalBindingSnapshot(null,null,null);
  TabletReceiver[] extraReceivers=[]; TabletConnectionRequest? tabletRequest=null;
  var bound=new ControlDeviceList(false,[new("active-device-123456","fixture-client","PA2573",ControlScope.Remote|ControlScope.Tablet,false,now,"宿舍平板","tablet",now),new("revoked-device-654321","fixture-old-client","PA2573",ControlScope.All,true,now.AddDays(-1)),new("phone-device-112233","fixture-phone","PA2573",ControlScope.Remote,false,now,null,"phone")]);
  var files=new TransferSnapshot(new(),[new("fixture-client","PA2573",new string('A',64))],[]);
  var engine=new EngineStatus("remote",true,true,null,48989,true,"等待设备连接");
  var preferences=Path.Combine(root,"preferences.json");File.WriteAllText(preferences,"{\"Theme\":\"light\",\"ReduceMotion\":true}");
  var runtime=new RuntimeClient((r,_)=>{
   calls.Add(r);if(r.Command=="receiver-list"&&!receiverAvailable)return Task.FromResult(new RuntimeResponse(false,"接收端不可用"));
   if((r.Command is "network-start" or "network-takeover")&&!takeoverAllowed)return Task.FromResult(new RuntimeResponse(false,"启动失败"));
   if(r.Command is "network-start" or "network-takeover")network=new(true,true,null,"已启用");
   if(r.Command=="status"&&!available)return Task.FromResult(new RuntimeResponse(false,"后台不可用"));
   if(r.Command=="binding-start")binding=new("123456",now.AddMinutes(3),null);
   if(r.Command=="binding-cancel")binding=new(null,null,null);
   if(r.Command=="connections-pause")paused=true;if(r.Command=="connections-resume")paused=false;
   if(r.Command=="control-device-rename")bound=bound with{Devices=bound.Devices.Select(d=>d.Id==r.RequestId?d with{Alias=r.Name}:d).ToArray()};
   if(r.Command=="tablet-request")tabletRequest=new("fixture-request",r.ClientId!,"waiting",0,"lan",now.AddMinutes(1),"请在设备上确认副屏邀请");
   if(r.Command=="tablet-request-cancel"&&tabletRequest is {} cancelling&&cancelling.Id==r.RequestId)tabletRequest=cancelling with{State="cancelled",Message="邀请已取消"};
   string? payload=r.Command switch{
    "network-config"=>JsonSerializer.Serialize(transport,RuntimeProtocol.JsonOptions),
    "network-status" or "network-login"=>JsonSerializer.Serialize(networkDetails,RuntimeProtocol.JsonOptions),
    "files-status"=>JsonSerializer.Serialize(files,RuntimeProtocol.JsonOptions),
    "control-device-list" or "control-device-rename"=>JsonSerializer.Serialize(bound,RuntimeProtocol.JsonOptions),
    "binding-start" or "binding-cancel" or "binding-status"=>JsonSerializer.Serialize(binding,RuntimeProtocol.JsonOptions),
    "host-policy-status"=>JsonSerializer.Serialize(new HostPolicySnapshot(),RuntimeProtocol.JsonOptions),
    "receiver-list" or "tablet-request"=>JsonSerializer.Serialize(new ReceiverSnapshot((responding?new TabletReceiver[]{new("fixture-client","PA2573",2560,1600,60,[0,90],now)}:[]).Concat(extraReceivers).ToArray(),tabletRequest),RuntimeProtocol.JsonOptions),_=>null};
   return Task.FromResult(new RuntimeResponse(true,"隔离状态已更新",new RuntimeStatus("fixture-id","我的电脑",engine with{Running=!paused,Healthy=!paused,Desired=!paused,Streaming=streaming,BackendReleasePending=ending},engine with{Profile="tablet",Running=false,Desired=false,Healthy=false},network,"fixture",root,now,ConnectionsPaused:paused),payload));
  });
  var w=new MainWindow(runtime,preferences){Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,Width=940,Height=660};
  T N<T>(string n)where T:class=>(T)w.FindName(n);
  void Invoke(string m,object? s=null)=>typeof(MainWindow).GetMethod(m,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,[s??w,new RoutedEventArgs()]);
  void Async(string m){typeof(MainWindow).GetMethod(m,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,null);Pump();}
  void Refresh(){Invoke("RefreshButton_Click");Pump();}
  void Check(bool b,string label){if(!b)throw new Exception(label);checks.Add(label);}
  void AuditScrollClearance(ScrollViewer viewer,string label){
   viewer.UpdateLayout();
   var bar=(ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar",viewer);
   if(viewer.ComputedVerticalScrollBarVisibility!=Visibility.Visible)return;
   var viewport=(ScrollContentPresenter)viewer.Template.FindName("PART_ScrollContentPresenter",viewer);
   viewer.SetValue(ScrollActivity.PointerNearProperty,true);Pump();
   double barLeft=bar.TranslatePoint(new Point(),viewer).X;
   double contentRight=viewport.TranslatePoint(new Point(viewport.ActualWidth,0),viewer).X;
   var visibleControls=Descendants(viewport).OfType<FrameworkElement>().Where(e=>e.IsVisible&&e is ButtonBase or ComboBox or TextBox)
    .Where(e=>{var p=e.TranslatePoint(new Point(),viewer);return p.Y+e.ActualHeight>0&&p.Y<viewer.ActualHeight;}).ToArray();
   scrollClearance.Add(new{label,viewerWidth=viewer.ActualWidth,contentRight,barLeft,barWidth=bar.ActualWidth,bar.Opacity,visibleControls=visibleControls.Length});
   Check(bar.Opacity==1&&bar.ActualWidth>=16,$"{label}: expanded scrollbar retains its pointer target");
   Check(contentRight<=barLeft+0.1,$"{label}: content viewport leaves a separate scrollbar gutter (content={contentRight}, bar={barLeft}, width={viewer.ActualWidth}, presenter={viewport.ActualWidth}, column={Grid.GetColumn(viewport)}, span={Grid.GetColumnSpan(viewport)})");
   Check(visibleControls.All(e=>e.TranslatePoint(new Point(e.ActualWidth,0),viewer).X<=barLeft+0.1),$"{label}: visible controls stay clear of the scrollbar");
   viewer.SetValue(ScrollActivity.PointerNearProperty,false);
  }
  void AuditTypography(DependencyObject container,string label){
   var expected=((FontFamily)w.FindResource("DesktopFont")).Source;
   string Color(Brush brush)=>((SolidColorBrush)brush).Color.ToString();
   var palette=new[]{"TextPrimary","Muted","OnAccent","AccentText","SuccessText","DangerText","WarningText","FeedbackTextBrush"}.Select(k=>Color((Brush)w.FindResource(k))).ToHashSet();
   var texts=Descendants(container).OfType<TextBlock>().Where(t=>t.IsVisible&&!string.IsNullOrWhiteSpace(t.Text)).ToArray();
   var headings=texts.Where(t=>t.Style==w.FindResource("PageTitle")||t.Style==w.FindResource("Heading")).ToArray();
   var wrongFonts=texts.Where(t=>t.FontFamily.Source!=expected&&t.FontFamily.Source is not ("Segoe MDL2 Assets" or "Consolas")).Select(t=>$"{t.Text} [{t.FontFamily.Source}]").ToArray();
   Check(texts.Length>0&&wrongFonts.Length==0,$"{label}: shared font/glyph/code (visible={texts.Length}; {string.Join("; ",wrongFonts)})");
   Check(headings.All(t=>Color(t.Foreground)==Color((Brush)w.FindResource("TextPrimary"))),$"{label}: headings retain the primary text color");
   Check(texts.All(t=>palette.Contains(Color(t.Foreground))),$"{label}: text colors belong to the current theme palette: {string.Join("; ",texts.Where(t=>!palette.Contains(Color(t.Foreground))).Select(t=>$"{t.Text}={Color(t.Foreground)}"))}");
   typography.Add(new{label,textCount=texts.Length,headings=headings.Select(t=>new{text=t.Text,color=Color(t.Foreground)}).ToArray(),fonts=texts.Select(t=>t.FontFamily.Source).Distinct().ToArray(),colors=texts.Select(t=>Color(t.Foreground)).Distinct().ToArray()});
  }
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
    Check(readiness.Observe(waiting with{Remote=engine with{BackendReleasePending=true}})=="正在结束连接"&&!readiness.TimedOut,"unconfirmed cleanup never claims readiness or startup timeout");
    Check(readiness.Observe(waiting with{Remote=engine with{BackendReleasePending=true},Tablet=engine with{Streaming=true}})=="正在被控","ending remote cleanup does not hide an active secondary stream");
    readiness.Observe(waiting);tick+=TimeSpan.FromSeconds(45);readiness.Observe(waiting);
    Check(readiness.Observe(waiting with{ConnectionsPaused=true})=="已暂停被控"&&!readiness.TimedOut,"pause clears startup wait");
    Check(readiness.Observe(waiting)=="准备中","resume starts a fresh wait");
    tick+=TimeSpan.FromSeconds(45);readiness.Observe(waiting);
    Check(readiness.Observe(waiting with{StartedAt=now.AddMinutes(1)})=="准备中","new service starts a fresh wait");
    Check(readiness.Observe(null)=="后台未连接"&&!readiness.TimedOut,"unavailable service clears wait");
    Pump();var pages=N<TabControl>("Pages");
    Check(pages.SelectedItem==N<TabItem>("HomePage")&&N<TextBlock>("HomeSummaryText").IsVisible,"startup selects and displays the unified homepage");
    Check(N<Border>("HomeLocalBadge").IsVisible,"local status is explicitly labeled as this computer");
    Check(w.FindName("TabletPopoverButton")==null&&w.FindName("TabletPopover")==null,"title bar has no duplicate secondary-display entry or hidden popup");
    Check(System.Windows.Automation.AutomationProperties.GetName(N<Button>("AccountButton"))=="网络连接","network entry keeps its accessible name");
    Invoke("OpenAcceptance_Click");Pump();var acceptance=N<Popup>("AcceptancePopover");
    Check(N<Button>("PauseConnectionsButton").Content.ToString()=="暂停接受连接"&&N<Button>("DisconnectActiveButton").Visibility==Visibility.Collapsed,"idle menu does not claim to disconnect an existing session");
    Check(((FrameworkElement)acceptance.Child).ActualHeight<=48&&N<Button>("PauseConnectionsButton").HorizontalContentAlignment==HorizontalAlignment.Left,"idle pause is a compact left-aligned menu row");
    Save((FrameworkElement)acceptance.Child,Path.Combine(root,"acceptance-idle.png"));acceptance.IsOpen=false;
    streaming=true;Refresh();Invoke("OpenAcceptance_Click");Pump();
    Check(N<Button>("PauseConnectionsButton").Content.ToString()=="断开并暂停连接"&&N<Button>("DisconnectActiveButton").IsVisible,"active menu distinguishes disconnect once from persistent pause");
    Save((FrameworkElement)acceptance.Child,Path.Combine(root,"acceptance-active.png"));acceptance.IsOpen=false;streaming=false;Refresh();
    ending=true;Refresh();Invoke("OpenAcceptance_Click");Pump();
    Check(N<TextBlock>("AcceptanceLabel").Text=="正在结束连接"&&N<Button>("DisconnectActiveButton").IsVisible&&N<Button>("PauseConnectionsButton").Content.ToString()=="断开并暂停连接","unconfirmed cleanup retains explicit local recovery actions");
    acceptance.IsOpen=false;ending=false;Refresh();
    Check(w.FindName("DevicesNavigation")==null,"local and devices navigation merged");
    Check(N<ItemsControl>("BoundDevicesList").Items.Count==2&&N<ItemsControl>("HistoricalDevicesList").Items.Count==1&&!N<Expander>("DeviceHistoryExpander").IsExpanded,"revoked duplicate stays in collapsed history");
    Check(!Descendants(N<ItemsControl>("BoundDevicesList")).OfType<TextBlock>().Any(t=>t.Text is "主屏" or "副屏" or "剪贴板" or "文件"),"device cards do not mix permission chips with connection status");
    foreach(string name in new[]{"AccountButton","HeaderMenuButton"}){
     var iconButton=N<Button>(name);var iconPath=Descendants(iconButton).OfType<System.Windows.Shapes.Path>().Single();
     var viewport=Descendants(iconButton).OfType<Grid>().First(g=>g.Width==20&&g.Height==20);var iconOrigin=viewport.TranslatePoint(new Point(),iconButton);
     Check(iconOrigin.Y>=1&&iconOrigin.Y+viewport.ActualHeight<=iconButton.ActualHeight-1&&viewport.ActualHeight==20&&iconPath.StrokeThickness==1.5,$"{name}: full icon viewport fits and stroke is shared");
    }
    var deviceMore=Descendants(N<ItemsControl>("BoundDevicesList")).OfType<Button>().First(b=>b.ContextMenu is not null);
    Invoke("OpenDeviceMenu_Click",deviceMore);Pump();var deviceMenu=deviceMore.ContextMenu!;
    Check(deviceMenu.Items.OfType<MenuItem>().Single(m=>m.Header.ToString()=="删除历史记录").Visibility==Visibility.Collapsed,"active device menu does not offer history deletion");
    var placementSize=new Size(deviceMenu.ActualWidth,deviceMenu.ActualHeight);
    var placement=deviceMenu.CustomPopupPlacementCallback!(placementSize,new Size(deviceMore.ActualWidth,deviceMore.ActualHeight),new Point()).Single().Point;
    var localMenu=deviceMore.TranslatePoint(placement,w);
    Check(localMenu.X>=7.9&&localMenu.Y>=7.9&&localMenu.X+placementSize.Width<=w.ActualWidth-7.9&&localMenu.Y+placementSize.Height<=w.ActualHeight-7.9,"device menu placement stays inside the owning window");deviceMenu.IsOpen=false;
    N<Expander>("DeviceHistoryExpander").IsExpanded=true;Pump();
    var historyMore=Descendants(N<ItemsControl>("HistoricalDevicesList")).OfType<Button>().Single(b=>b.ContextMenu is not null);
    Invoke("OpenDeviceMenu_Click",historyMore);Pump();
    Check(historyMore.ContextMenu!.Items.OfType<MenuItem>().Single(m=>m.Header.ToString()=="删除历史记录") is {Visibility:Visibility.Visible,Tag:"revoked-device-654321"},"history menu exposes deletion for the exact revoked credential");
    Check(historyMore.ContextMenu.Items.OfType<MenuItem>().Single(m=>m.Header.ToString()=="设备权限").Visibility==Visibility.Collapsed,"revoked history no longer offers active permission editing");historyMore.ContextMenu.IsOpen=false;N<Expander>("DeviceHistoryExpander").IsExpanded=false;
    var row=N<ItemsControl>("BoundDevicesList").Items.Cast<object>().Single(r=>(string)P(r,"Id")=="active-device-123456");
    Check((string)P(row,"Name")=="宿舍平板"&&((string)P(row,"LastContact")).Contains("最后联系")&&(string)P(row,"TypeText")=="平板","alias, type and genuine contact shown");
    Check(!(bool)P(row,"CanConnect")&&(bool)P(row,"CanOpenFiles"),"file shortcut remains reachable while receiver is unconfirmed");
    Check(w.FindName("BrandVersion")==null&&w.Title=="我控制","titlebar retains just the product name");
    Check(w.FindName("FileEmptySendButton")==null,"empty state does not duplicate the primary send button");
    Check(N<TextBlock>("HomeSummaryText").Text.Contains("接受连接")&&N<TextBlock>("HomePrivacyText").Visibility==Visibility.Collapsed,"home exposes acceptance without false privacy claim");
    Check(!N<Button>("SendFileButton").IsEnabled&&N<TextBlock>("FileTasksEmptyText").Visibility==Visibility.Visible&&N<ListBox>("FileTaskList").Visibility==Visibility.Collapsed,"disabled transfers show empty state and disable sending");
    Check(N<Button>("FileInlineAllowButton").Visibility==Visibility.Collapsed,"file page keeps permissions in device dialog");
    Invoke("OpenFileAccessSettings_Click");Pump();
    Check(N<Grid>("DevicePermissionsDialog").IsVisible&&N<Expander>("FileAccessDetails").IsExpanded,"file permissions link opens device dialog");
    var permissionScroller=Descendants(N<Grid>("DevicePermissionsDialog")).OfType<ScrollViewer>().First();
    foreach(int theme in new[]{1,2}){
     N<ComboBox>("ThemeChoice").SelectedIndex=theme;
     foreach(var size in new[]{(940,660),(1080,760),(1380,900)}){
      w.Width=size.Item1;w.Height=size.Item2;
      foreach(bool expanded in new[]{false,true,false}){
       N<Expander>("FileAccessDetails").IsExpanded=expanded;Pump();double? initialX=null;
       foreach(double fraction in new[]{0d,.5,1d}){
        permissionScroller.ScrollToVerticalOffset(permissionScroller.ScrollableHeight*fraction);Pump();
        AuditScrollClearance(permissionScroller,$"permissions/{theme}/{size}/{expanded}/{fraction}");
        double x=N<CheckBox>("ControlRemoteCheck").TranslatePoint(new Point(),permissionScroller).X;
        initialX??=x;Check(Math.Abs(initialX.Value-x)<.5,"permission switches do not move horizontally while scrolling");
       }
       if(size==(940,660))Save((FrameworkElement)w.Content,Path.Combine(root,$"permissions-{theme}-{expanded}.png"));
      }
     }
    }
    w.Width=940;w.Height=660;N<ComboBox>("ThemeChoice").SelectedIndex=1;
    Invoke("CloseDevicePermissions_Click");Pump();
    Check(N<Expander>("DeviceHistoryExpander").Header.ToString()=="历史设备（1）"&&(string)P(row,"ShortId")=="123456","history count and short id are labeled");
    files=files with{Settings=new(true)};Async("RefreshFilesAsync");
    var fileChoice=N<ComboBox>("FileDeviceBox").Items.Cast<object>().Single();
    Check((string)P(fileChoice,"Label")=="PA2573","legacy file selector shows a name without permission or pending suffixes");
    Check(N<TextBlock>("FileTasksEmptyText").Text.Contains("暂无传输任务")&&!N<Button>("SendFileButton").IsEnabled,"enabling transfer does not grant device access");
    object Device(string id)=>N<ItemsControl>("BoundDevicesList").Items.Cast<object>().Single(r=>(string)P(r,"Id")==id);
    row=Device("active-device-123456");
    Check(!(bool)P(row,"CanConnect")&&((string)P(row,"ConnectReason")).Contains("网络"),"missing response keeps a distinct reason on the device card");
    responding=true;Async("RefreshReceiversAsync");
    row=Device("active-device-123456");
    Check((bool)P(row,"CanConnect")&&(string)P(row,"OnlineText")=="在线","authorized responding tablet connects from its card");
    var phone=Device("phone-device-112233");
    Check((string)P(phone,"TypeText")=="手机"&&!(bool)P(phone,"CanConnect")&&((string)P(phone,"ConnectReason")).Contains("未授权"),"phone shares the card template and respects its own permissions");
    int requestsBefore=calls.Count(c=>c.Command=="tablet-request");
    receiverAvailable=false;Invoke("ConnectListedReceiver_Click",new Button{Tag=row});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request")==requestsBefore,"failed receiver refresh cannot invite from a stale device row");
    receiverAvailable=true;responding=true;Async("RefreshReceiversAsync");
    Invoke("ConnectListedReceiver_Click",new Button{Tag=phone});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request")==requestsBefore,"device without secondary scope never sends an invitation");
    Invoke("ConnectListedReceiver_Click",new Button{Tag=Device("active-device-123456")});Pump();
    Check(calls.Last(c=>c.Command=="tablet-request").ClientId=="fixture-client","device shortcut routes to its exact receiver");
    row=Device("active-device-123456");
    Check((string)P(row,"ConnectLabel")=="取消邀请"&&(string)P(row,"RequestId")=="fixture-request","pending invitation can be cancelled on the same device card");
    Invoke("ConnectListedReceiver_Click",new Button{Tag=row});Pump();
    Check(calls.Last(c=>c.Command=="tablet-request-cancel").RequestId=="fixture-request","card cancellation uses the exact request id");
    tabletRequest=new("connected-request","fixture-client","connected",0,"lan",now.AddMinutes(1),"副屏已连接");Async("RefreshReceiversAsync");
    row=Device("active-device-123456");
    Check((string)P(row,"ConnectLabel")=="断开副屏","connected device retains its disconnect action without a popup");
    tabletRequest=new("new-request","fixture-client","waiting",0,"lan",now.AddMinutes(1),"等待确认");
    int cancelsBefore=calls.Count(c=>c.Command=="tablet-request-cancel");
    Invoke("ConnectListedReceiver_Click",new Button{Tag=row});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request-cancel")==cancelsBefore,"stale disconnect never cancels a newer request");
    tabletRequest=null;Async("RefreshReceiversAsync");
    extraReceivers=[new("legacy-phone","兼容手机",1080,2400,60,[0,90],now),new("fixture-old-client","撤销设备",1080,2400,60,[0],now),new("fixture-client","同一平板",2560,1600,60,[0],now)];
    Async("RefreshReceiversAsync");Check(N<ItemsControl>("BoundDevicesList").Items.Count==2,"receiver discovery does not grant legacy access");
    bound=bound with{LegacyAllowed=true};Async("ReadControlDevicesAsync");
    var cards=N<ItemsControl>("BoundDevicesList").Items.Cast<object>().ToArray();
    Check(cards.Length==3&&cards.Count(r=>(string)P(r,"ClientId")=="fixture-client")==1,"bound and receiver data merge into one card per device");
    var legacy=cards.Single(r=>(string)P(r,"ClientId")=="legacy-phone");
    Check((bool)P(legacy,"CanConnect")&&!(bool)P(legacy,"IndividuallyBound")&&(string)P(legacy,"TypeText")=="兼容设备","legacy phone moves to the homepage with a truthful binding label");
    Check(!cards.Any(r=>(string)P(r,"ClientId")=="fixture-old-client"),"revoked device is never resurrected through legacy discovery");
    bound=bound with{LegacyAllowed=false};Async("ReadControlDevicesAsync");
    requestsBefore=calls.Count(c=>c.Command=="tablet-request");Invoke("ConnectListedReceiver_Click",new Button{Tag=legacy});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request")==requestsBefore,"stale legacy card rechecks permission before inviting");
    extraReceivers=[];Async("RefreshReceiversAsync");
    var detailMenu=new ContextMenu();var menuAnchor=new Button{Tag="active-device-123456"};
    var detailItem=new MenuItem{Tag="active-device-123456"};detailMenu.Items.Add(detailItem);detailMenu.PlacementTarget=menuAnchor;
    Invoke("OpenBoundDeviceDetails_Click",detailItem);Pump();
    Check(N<Popup>("BoundDeviceDetailsPopover").PlacementTarget==menuAnchor&&N<TextBlock>("BoundDeviceNameText").Text=="宿舍平板","menu details retain the clicked device and popup anchor");
    N<Popup>("BoundDeviceDetailsPopover").IsOpen=false;
    files=files with{Devices=[files.Devices[0],new("fixture-phone","手机",new string('B',64),true)]};
    Invoke("OpenListedFiles_Click",new Button{Tag=Device("phone-device-112233")});Pump();
    Check(pages.SelectedItem==N<TabItem>("FilesPage")&&N<ComboBox>("FileDeviceBox").SelectedValue as string=="fixture-phone","file shortcut selects its clicked device");
    files=files with{Devices=[files.Devices[0]]};Async("RefreshFilesAsync");
    Check(N<ComboBox>("FileDeviceBox").SelectedIndex<0&&!N<Button>("SendFileButton").IsEnabled,"missing target cannot silently fall back to another device");
    Invoke("OpenListedFiles_Click",new Button{Tag=Device("active-device-123456")});Pump();
    Check(N<ComboBox>("FileDeviceBox").SelectedValue as string=="fixture-client","another device shortcut explicitly replaces the target");
    pages.SelectedItem=N<TabItem>("HomePage");
    responding=false;int before=calls.Count(c=>c.Command=="tablet-request");Invoke("ConnectListedReceiver_Click",new Button{Tag=Device("active-device-123456")});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request")==before,"stale receiver is refreshed before inviting");
    Invoke("PauseConnections_Click");Pump();
    Check(paused&&N<TextBlock>("AcceptanceLabel").Text.Contains("暂停")&&N<Button>("PauseConnectionsButton").Content.ToString()!.Contains("恢复"),"topbar pause uses confirmed backend state");
    Invoke("PauseConnections_Click");Pump();Check(!paused,"same topbar entry resumes acceptance");
    Invoke("OpenDevices_Click");Pump();
    Check(N<Grid>("BindingDialog").Visibility==Visibility.Visible&&N<TextBlock>("BindingCodeText").Text=="123456"&&N<TextBlock>("BindingCountdownText").Text.Contains("有效期剩余"),"binding modal preserves six-digit code and countdown");
    typeof(MainWindow).GetField("_addresses",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(w,new[]{"192.168.1.8","100.89.1.2","172.20.10.3"});
    Async("RefreshLocalBindingAsync");
    Check(N<TextBlock>("BindingAddressText").Text=="192.168.1.8"+Environment.NewLine+"172.20.10.3","binding shows addresses accepted by the tablet manual form, excluding tunnel addresses");
    Save((FrameworkElement)w.Content,Path.Combine(root,"binding-address.png"));
    binding=binding with{Request=new("fixture-binding","fixture-client","PA2573","246 802","pending",now.AddMinutes(1))};Refresh();
    Check(N<StackPanel>("BindingComparisonPanel").Visibility==Visibility.Visible&&N<TextBlock>("BindingComparisonText").Text=="246 802","comparison remains visible before approval");
    Check(N<StackPanel>("BindingEntryPanel").Visibility==Visibility.Collapsed&&N<Button>("BindingGenerateButton").Visibility==Visibility.Collapsed&&N<Button>("BindingApproveButton").Style==w.FindResource("Primary"),"pending approval hides the spent code and regeneration and highlights allow binding");
    Save((FrameworkElement)w.Content,Path.Combine(root,"binding-dialog.png"));Invoke("CloseBinding_Click");Pump();
    Check(binding.Code==null&&N<Grid>("BindingDialog").Visibility==Visibility.Collapsed&&calls.Any(c=>c.Command=="binding-cancel"),"closing dialog invalidates invitation without changing grants");
    Invoke("OpenDeviceAlias_Click",new Button{Tag="active-device-123456"});N<TextBox>("DeviceAliasBox").Text="随身平板";Invoke("SaveDeviceAlias_Click");Pump();
    Check(bound.Devices[0].Alias=="随身平板"&&N<Grid>("AliasDialog").Visibility==Visibility.Collapsed,"alias saves by credential id, never model name");
    Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionText").Text.Contains("内置异地连接：未启用")&&N<Button>("AccountPrimaryButton").Content.ToString()=="连接设置","network popup shows status and one settings entry");
    N<Button>("AccountPrimaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
    Check(pages.SelectedItem==N<TabItem>("SettingsPage")&&N<TabControl>("SettingsCategories").SelectedItem==N<TabItem>("NetworkSettingsCategory")&&!calls.Any(c=>c.Command=="network-takeover"),"network entry opens settings without enabling a connection");
    pages.SelectedItem=N<TabItem>("HomePage");Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionDetailText").Visibility==Visibility.Collapsed,"normal disabled network has no repeated explanatory paragraph");
    Save((FrameworkElement)N<Popup>("AccountPopover").Child,Path.Combine(root,"account-popover.png"));N<Popup>("AccountPopover").IsOpen=false;
    network=new(false,true,null,"测试：异地进程未启动");Refresh();Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionText").Text.Contains("异常")&&N<TextBlock>("AccountConnectionDetailText").IsVisible&&N<TextBlock>("AccountConnectionDetailText").Text==network.Message,"simplifying network text retains genuine failure reasons");
    Save((FrameworkElement)N<Popup>("AccountPopover").Child,Path.Combine(root,"network-error.png"));N<Popup>("AccountPopover").IsOpen=false;network=new(false,false,null,"未启用");Refresh();
    network=new(true,true,null,"内核已运行");networkDetails=new NetworkStatus{Available=true,Error="测试：连接服务不可达"};Async("RefreshDetailsAsync");Refresh();Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionDetailText").IsVisible&&N<TextBlock>("AccountConnectionDetailText").Text==networkDetails.Error,"running network retains a genuine detailed error");N<Popup>("AccountPopover").IsOpen=false;
    network=new(false,false,null,"未启用");Refresh();Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionDetailText").Visibility==Visibility.Collapsed,"stopped network hides generic or cached backend error");N<Popup>("AccountPopover").IsOpen=false;
    transport=new("tailscale");networkDetails=new NetworkStatus{Available=true,State="NeedsLogin"};int loginBefore=calls.Count(c=>c.Command=="network-login");
    Async("EnableNetworkAsync");
    Check(calls.Count(c=>c.Command=="network-login")==loginBefore+1&&pages.SelectedItem==N<TabItem>("SettingsPage")&&N<Button>("NetworkLoginButton").IsVisible,"enabling remote networking automatically starts account authorization from the visible login entry");
    networkDetails=new NetworkStatus{Available=true,State="Running",LoggedIn=true};Async("EnableNetworkAsync");
    Check(calls.Count(c=>c.Command=="network-login")==loginBefore+2&&N<Button>("NetworkLoginButton").Content.ToString()=="已登录","already logged-in users retain their confirmed login state");
    takeoverAllowed=false;network=new(false,false,null,"未启用");networkDetails=new NetworkStatus{Available=true,State="NeedsLogin"};Async("EnableNetworkAsync");
    Check(calls.Count(c=>c.Command=="network-login")==loginBefore+2,"failed startup never starts account authorization");
    takeoverAllowed=true;transport=new("lan");network=new(false,false,null,"未启用");Refresh();
    foreach(int theme in new[]{1,2}){
     N<ComboBox>("ThemeChoice").SelectedIndex=theme;
     foreach(string page in new[]{"HomePage","FilesPage"}){
      pages.SelectedItem=N<TabItem>(page);w.UpdateLayout();Pump();
      AuditTypography((FrameworkElement)w.Content,$"{theme}/{page}");
      Check(page=="FilesPage" ? N<Button>("SendFileButton").TranslatePoint(new Point(N<Button>("SendFileButton").ActualWidth,0),N<Grid>("FilesContent")).X<=N<Grid>("FilesContent").ActualWidth+0.1 : ((ScrollViewer)N<TabItem>(page).Content).ScrollableWidth<1,$"{theme}/{page} has no horizontal overflow at minimum size");Save((FrameworkElement)w.Content,Path.Combine(root,$"{page}-{theme}.png"));
     }
     Invoke("OpenTabletSettings_Click");w.UpdateLayout();Pump();
     foreach(TabItem category in N<TabControl>("SettingsCategories").Items){
      N<TabControl>("SettingsCategories").SelectedItem=category;Pump();w.UpdateLayout();
      AuditTypography((FrameworkElement)w.Content,$"{theme}/settings/{category.Header}");
      var settingsScroller=(ScrollViewer)category.Content;settingsScroller.ScrollToBottom();Pump();
      AuditScrollClearance(settingsScroller,$"settings/{theme}/{category.Header}");settingsScroller.ScrollToTop();Pump();
      Check(category.Foreground is SolidColorBrush body&&body.Color==((SolidColorBrush)w.FindResource("TextPrimary")).Color,$"{theme}/{category.Header}: selected category does not tint its content");
     }
     N<TabControl>("SettingsCategories").SelectedItem=N<TabItem>("TabletSettingsCategory");Pump();
     Check(((ScrollViewer)N<TabItem>("TabletSettingsCategory").Content).ScrollableWidth<1&&N<ComboBox>("OrientationBox").IsVisible,"tablet preferences reachable from settings");
     Save((FrameworkElement)w.Content,Path.Combine(root,$"tablet-settings-{theme}.png"));
     foreach(string page in new[]{"NetworkSettingsCategory","DiagnosticsPage"}){
      if(page=="NetworkSettingsCategory")Invoke("OpenAccount_Click");else pages.SelectedItem=N<TabItem>(page);Pump();w.UpdateLayout();AuditTypography((FrameworkElement)w.Content,$"{theme}/{page}");
      if(page=="NetworkSettingsCategory") {
       var content=(FrameworkElement)N<TabItem>(page).Content;
       Check(new[]{"连接方式","连接状态","账号内设备"}.All(title=>Descendants(content).OfType<TextBlock>().Count(t=>t.Text==title&&t.IsVisible)==1),$"{theme}/account: section titles are unique and distinct");
       Check(N<TextBlock>("NetworkStateText").FontSize<N<TextBlock>("ConnectionStatusHeading").FontSize&&N<TextBlock>("ProviderSavedText").Visibility==Visibility.Collapsed,$"{theme}/account: state and saved selection do not become duplicate headings");
       Save((FrameworkElement)w.Content,Path.Combine(root,$"account-page-{theme}.png"));
      }
     }
     Invoke("OpenHelp_Click");Pump();AuditTypography(N<Grid>("HelpDialog"),$"{theme}/help");
     var help=N<ScrollViewer>("HelpScroller");var helpFrame=N<Border>("HelpFrame");
     var helpOrigin=helpFrame.TranslatePoint(new Point(),w);
     Check(helpOrigin.X>=23.9&&helpOrigin.Y>=23.9&&helpOrigin.X+helpFrame.ActualWidth<=w.ActualWidth-23.9&&helpOrigin.Y+helpFrame.ActualHeight<=w.ActualHeight-23.9,$"{theme}/help frame stays within minimum window");
     Check(help.ViewportHeight>0&&help.ScrollableWidth<1,$"{theme}/help has a bounded wrapping viewport");help.ScrollToEnd();Pump();
     var helpViewport=Descendants(help).OfType<ScrollContentPresenter>().First(p=>p.TemplatedParent==help);
     Check(Descendants(N<StackPanel>("HelpContent")).OfType<TextBlock>().All(t=>t.TranslatePoint(new Point(t.ActualWidth,0),helpViewport).X<=helpViewport.ActualWidth+0.1),$"{theme}/help text width reserves the scrollbar and stays within its viewport");
     var helpChildren=N<StackPanel>("HelpContent").Children;var lastHelp=(TextBlock)helpChildren[helpChildren.Count-1];var lastHelpPoint=lastHelp.TranslatePoint(new Point(0,lastHelp.ActualHeight),help);
     Check(lastHelpPoint.Y<=help.ActualHeight+0.1,$"{theme}/last handbook paragraph is reachable by scrolling");
     foreach(int dpi in new[]{96,120,144}){var e=N<Grid>("HelpDialog");var bitmap=new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth*dpi/96),(int)Math.Ceiling(e.ActualHeight*dpi/96),dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(e);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bitmap));using var f=File.Create(Path.Combine(root,$"help-{theme}-{dpi}dpi.png"));enc.Save(f);}
     Invoke("CloseHelp_Click");
     pages.SelectedItem=N<TabItem>("HomePage");Pump();
     Invoke("OpenAcceptance_Click");Pump();AuditTypography(N<Popup>("AcceptancePopover").Child,$"{theme}/acceptance");Save((FrameworkElement)N<Popup>("AcceptancePopover").Child,Path.Combine(root,$"acceptance-{theme}.png"));N<Popup>("AcceptancePopover").IsOpen=false;
     foreach(string opener in new[]{"OpenAccountPopover_Click","OpenDeviceDetails_Click"}){
      Invoke(opener);Pump();
      string popupName=opener=="OpenAccountPopover_Click"?"AccountPopover":"DeviceDetailsPopover";
      var themedPopup=N<Popup>(popupName);AuditTypography(themedPopup.Child,$"{theme}/{popupName}");themedPopup.IsOpen=false;
     }
     var themedMenu=new ContextMenu{Style=(Style)w.FindResource("DesktopMenu"),PlacementTarget=N<Button>("HeaderMenuButton")};
     themedMenu.Items.Add(new MenuItem{Header="使用手册",Style=(Style)w.FindResource("DesktopMenuItem")});themedMenu.IsOpen=true;Pump();
     Check(themedMenu.FontFamily.Source==((FontFamily)w.FindResource("DesktopFont")).Source,$"{theme}/menu: popup font matches the main window");
     themedMenu.IsOpen=false;
    }
    w.Width=1800;w.Height=1000;pages.SelectedItem=N<TabItem>("HomePage");Pump();w.UpdateLayout();
    Check(N<StackPanel>("HomeContent").ActualWidth<=960.1&&N<StackPanel>("HomeContent").TranslatePoint(new Point(),w).Y<100,"large desktop home is width-limited and top aligned");
    Save((FrameworkElement)w.Content,Path.Combine(root,"home-large.png"));
    pages.SelectedItem=N<TabItem>("SettingsPage");Pump();w.UpdateLayout();
    Check(N<Grid>("SettingsContent").ActualWidth<=960.1,"settings also keep a readable width");
    available=false;Refresh();Check(!N<Button>("PauseConnectionsButton").IsEnabled&&N<TextBlock>("AcceptanceLabel").Text.Contains("未连接"),"backend failure disables readiness actions");
    Check(!calls.Any(c=>c.Command is "control-device-scope" or "control-device-revoke" or "files-allow" or "host-policy-save" or "network-provider"),"presentation never silently mutates permissions or transport");
    File.WriteAllText(Path.Combine(root,"typography.json"),JsonSerializer.Serialize(typography,new JsonSerializerOptions{WriteIndented=true}));
    File.WriteAllText(Path.Combine(root,"scroll-clearance.json"),JsonSerializer.Serialize(scrollClearance,new JsonSerializerOptions{WriteIndented=true}));
    File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,count=checks.Count,checks,installedServiceCalls=0,operatingSystemInputSent=false},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS {checks.Count} focused desktop checks.");
   }catch(Exception ex){File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Console.Error.WriteLine(ex);Environment.ExitCode=1;}
   finally{w.DisposeWindow();w.Close();app.Shutdown();}
  }));w.Show();app.Run();
 }
 static void Pump(int ms=80){var frame=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();frame.Continue=false;};t.Start();Dispatcher.PushFrame(frame);}
 static IEnumerable<DependencyObject> Descendants(DependencyObject node){yield return node;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(node,i)))yield return child;}
 static void Save(FrameworkElement e,string path){e.UpdateLayout();var b=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(e);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(path);enc.Save(f);}
}
