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
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var checks=new List<string>();var calls=new List<RuntimeRequest>();var typography=new List<object>();
  bool paused=false,available=true,responding=false,receiverAvailable=true,streaming=false;var now=DateTimeOffset.UtcNow;
  var network=new NetworkEngineStatus(false,false,null,"未启用");
  var networkDetails=new NetworkStatus{Error="内置异地连接尚未启动，或后台服务暂时不可用。"};
  var binding=new LocalBindingSnapshot(null,null,null);
  var bound=new ControlDeviceList(false,[new("active-device-123456","fixture-client","PA2573",ControlScope.Remote|ControlScope.Tablet,false,now,"宿舍平板","tablet",now),new("revoked-device-654321","fixture-old-client","PA2573",ControlScope.All,true,now.AddDays(-1)),new("phone-device-112233","fixture-phone","PA2573",ControlScope.Remote,false,now,null,"phone")]);
  var files=new TransferSnapshot(new(),[new("fixture-client","PA2573",new string('A',64))],[]);
  var engine=new EngineStatus("remote",true,true,null,48989,true,"等待设备连接");
  var preferences=Path.Combine(root,"preferences.json");File.WriteAllText(preferences,"{\"Theme\":\"light\",\"ReduceMotion\":true}");
  var runtime=new RuntimeClient((r,_)=>{
   calls.Add(r);if(r.Command=="receiver-list"&&!receiverAvailable)return Task.FromResult(new RuntimeResponse(false,"接收端不可用"));
   if(r.Command=="status"&&!available)return Task.FromResult(new RuntimeResponse(false,"后台不可用"));
   if(r.Command=="binding-start")binding=new("123456",now.AddMinutes(3),null);
   if(r.Command=="binding-cancel")binding=new(null,null,null);
   if(r.Command=="connections-pause")paused=true;if(r.Command=="connections-resume")paused=false;
   if(r.Command=="control-device-rename")bound=bound with{Devices=bound.Devices.Select(d=>d.Id==r.RequestId?d with{Alias=r.Name}:d).ToArray()};
   string? payload=r.Command switch{
    "network-config"=>"{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
    "network-status"=>JsonSerializer.Serialize(networkDetails,RuntimeProtocol.JsonOptions),
    "files-status"=>JsonSerializer.Serialize(files,RuntimeProtocol.JsonOptions),
    "control-device-list" or "control-device-rename"=>JsonSerializer.Serialize(bound,RuntimeProtocol.JsonOptions),
    "binding-start" or "binding-cancel" or "binding-status"=>JsonSerializer.Serialize(binding,RuntimeProtocol.JsonOptions),
    "host-policy-status"=>JsonSerializer.Serialize(new HostPolicySnapshot(),RuntimeProtocol.JsonOptions),
    "receiver-list"=>JsonSerializer.Serialize(new ReceiverSnapshot(responding?[new("fixture-client","PA2573",2560,1600,60,[0,90],now)]:[],null),RuntimeProtocol.JsonOptions),_=>null};
   return Task.FromResult(new RuntimeResponse(true,"隔离状态已更新",new RuntimeStatus("fixture-id","我的电脑",engine with{Running=!paused,Healthy=!paused,Desired=!paused,Streaming=streaming},engine with{Profile="tablet",Running=false,Desired=false,Healthy=false},network,"fixture",root,now,ConnectionsPaused:paused),payload));
  });
  var w=new MainWindow(runtime,preferences){Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,Width=940,Height=660};
  T N<T>(string n)where T:class=>(T)w.FindName(n);
  void Invoke(string m,object? s=null)=>typeof(MainWindow).GetMethod(m,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,[s??w,new RoutedEventArgs()]);
  void Async(string m){typeof(MainWindow).GetMethod(m,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,null);Pump();}
  void Refresh(){Invoke("RefreshButton_Click");Pump();}
  void Check(bool b,string label){if(!b)throw new Exception(label);checks.Add(label);}
  void AuditTypography(DependencyObject container,string label){
   var expected=((FontFamily)w.FindResource("DesktopFont")).Source;
   string Color(Brush brush)=>((SolidColorBrush)brush).Color.ToString();
   var palette=new[]{"TextPrimary","Muted","OnAccent","AccentText","SuccessText","DangerText","WarningText","FeedbackTextBrush"}.Select(k=>Color((Brush)w.FindResource(k))).ToHashSet();
   var texts=Descendants(container).OfType<TextBlock>().Where(t=>t.IsVisible&&!string.IsNullOrWhiteSpace(t.Text)).ToArray();
   var headings=texts.Where(t=>t.Style==w.FindResource("PageTitle")||t.Style==w.FindResource("Heading")).ToArray();
   var wrongFonts=texts.Where(t=>t.FontFamily.Source!=expected&&t.FontFamily.Source is not ("Segoe MDL2 Assets" or "Consolas")).Select(t=>$"{t.Text} [{t.FontFamily.Source}]").ToArray();
   Check(texts.Length>0&&wrongFonts.Length==0,$"{label}: shared font/glyph/code (visible={texts.Length}; {string.Join("; ",wrongFonts)})");
   Check(headings.All(t=>Color(t.Foreground)==Color((Brush)w.FindResource("TextPrimary"))),$"{label}: headings retain the primary text color");
   Check(texts.All(t=>palette.Contains(Color(t.Foreground))),$"{label}: text colors belong to the current theme palette");
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
    readiness.Observe(waiting);tick+=TimeSpan.FromSeconds(45);readiness.Observe(waiting);
    Check(readiness.Observe(waiting with{ConnectionsPaused=true})=="已暂停被控"&&!readiness.TimedOut,"pause clears startup wait");
    Check(readiness.Observe(waiting)=="准备中","resume starts a fresh wait");
    tick+=TimeSpan.FromSeconds(45);readiness.Observe(waiting);
    Check(readiness.Observe(waiting with{StartedAt=now.AddMinutes(1)})=="准备中","new service starts a fresh wait");
    Check(readiness.Observe(null)=="后台未连接"&&!readiness.TimedOut,"unavailable service clears wait");
    Pump();var pages=N<TabControl>("Pages");
    Check(pages.SelectedItem==N<TabItem>("HomePage")&&N<TextBlock>("HomeSummaryText").IsVisible,"startup selects and displays the unified homepage");
    Check(N<Border>("HomeLocalBadge").IsVisible,"local status is explicitly labeled as this computer");
    Check(System.Windows.Automation.AutomationProperties.GetName(N<Button>("TabletPopoverButton"))=="连接副屏"&&N<Button>("TabletPopoverButton").ActualWidth<=44&&System.Windows.Automation.AutomationProperties.GetName(N<Button>("AccountButton"))=="网络连接","compact icon controls have display and network names");
    Invoke("OpenAcceptance_Click");Pump();var acceptance=N<Popup>("AcceptancePopover");
    Check(N<Button>("PauseConnectionsButton").Content.ToString()=="暂停接受连接"&&N<Button>("DisconnectActiveButton").Visibility==Visibility.Collapsed,"idle menu does not claim to disconnect an existing session");
    Check(((FrameworkElement)acceptance.Child).ActualHeight<=48&&N<Button>("PauseConnectionsButton").HorizontalContentAlignment==HorizontalAlignment.Left,"idle pause is a compact left-aligned menu row");
    Save((FrameworkElement)acceptance.Child,Path.Combine(root,"acceptance-idle.png"));acceptance.IsOpen=false;
    streaming=true;Refresh();Invoke("OpenAcceptance_Click");Pump();
    Check(N<Button>("PauseConnectionsButton").Content.ToString()=="断开并暂停连接"&&N<Button>("DisconnectActiveButton").IsVisible,"active menu distinguishes disconnect once from persistent pause");
    Save((FrameworkElement)acceptance.Child,Path.Combine(root,"acceptance-active.png"));acceptance.IsOpen=false;streaming=false;Refresh();
    Check(w.FindName("DevicesNavigation")==null,"local and devices navigation merged");
    Check(N<ItemsControl>("BoundDevicesList").Items.Count==2&&N<ItemsControl>("HistoricalDevicesList").Items.Count==1&&!N<Expander>("DeviceHistoryExpander").IsExpanded,"revoked duplicate stays in collapsed history");
    Check(!Descendants(N<ItemsControl>("BoundDevicesList")).OfType<TextBlock>().Any(t=>t.Text is "主屏" or "副屏" or "剪贴板" or "文件"),"device cards do not mix permission chips with connection status");
    foreach(string name in new[]{"TabletPopoverButton","AccountButton","HeaderMenuButton"}){
     var iconButton=N<Button>(name);var iconPath=Descendants(iconButton).OfType<System.Windows.Shapes.Path>().Single();
     var viewport=(Grid)iconButton.Content;var iconOrigin=viewport.TranslatePoint(new Point(),iconButton);
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
    Invoke("CloseDevicePermissions_Click");Pump();
    Check(N<Expander>("DeviceHistoryExpander").Header.ToString()=="历史设备（1）"&&(string)P(row,"ShortId")=="123456","history count and short id are labeled");
    files=files with{Settings=new(true)};Async("RefreshFilesAsync");
    var fileChoice=N<ComboBox>("FileDeviceBox").Items.Cast<object>().Single();
    Check((string)P(fileChoice,"Label")=="PA2573","legacy file selector shows a name without permission or pending suffixes");
    Check(N<TextBlock>("FileTasksEmptyText").Text.Contains("暂无传输任务")&&!N<Button>("SendFileButton").IsEnabled,"enabling transfer does not grant device access");
    Invoke("OpenTabletPopover_Click");Pump();var popup=N<Popup>("TabletPopover");
    row=N<ItemsControl>("TabletReceiverList").Items[0];
    Check(!(bool)P(row,"CanConnect")&&((string)P(row,"Detail")).Contains("网络"),"missing response does not assert offline or toggle disabled");
    responding=true;Async("RefreshReceiversAsync");
    row=N<ItemsControl>("TabletReceiverList").Items.Cast<object>().Single(r=>(string)P(r,"ClientId")=="fixture-client");
    Check((bool)P(row,"CanConnect"),"authorized responding receiver enabled");
    var receiverRow=Descendants(popup.Child).OfType<Button>().Single(b=>b.Tag as string=="fixture-client");
    Check(receiverRow.ActualHeight<=56&&Descendants(receiverRow).OfType<TextBlock>().Any(t=>t.Text=="连接"&&t.IsVisible),"ready receiver is a single compact row with an explicit connect action");
    Check(!Descendants(receiverRow).OfType<TextBlock>().Any(t=>t.IsVisible&&t.Text.Contains("发送副屏邀请"))&&N<TextBlock>("TabletRequestStateText").Visibility==Visibility.Collapsed,"ready row and idle popover omit repeated invitation explanations");
    var phone=N<ItemsControl>("TabletReceiverList").Items.Cast<object>().Single(r=>(string)P(r,"ClientId")=="fixture-phone");
    Check(!(bool)P(phone,"CanConnect")&&((string)P(phone,"Detail")).Contains("未授权"),"missing scope has distinct disabled reason");
    Save((FrameworkElement)popup.Child,Path.Combine(root,"tablet-popover.png"));
    var deviceRow=N<ItemsControl>("BoundDevicesList").Items.Cast<object>().Single(r=>(string)P(r,"Id")=="active-device-123456");
    Check((bool)P(deviceRow,"CanConnect")&&(string)P(deviceRow,"OnlineText")=="在线","device shortcut and dot use fresh receiver evidence");
    int requestsBefore=calls.Count(c=>c.Command=="tablet-request");
    receiverAvailable=false;Invoke("ConnectListedReceiver_Click",new Button{Tag="active-device-123456"});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request")==requestsBefore,"failed receiver refresh cannot invite from a stale device row");
    receiverAvailable=true;responding=true;Async("RefreshReceiversAsync");
    Invoke("ConnectListedReceiver_Click",new Button{Tag="phone-device-112233"});Pump();
    Check(calls.Count(c=>c.Command=="tablet-request")==requestsBefore,"device without secondary scope never sends an invitation");
    Invoke("ConnectListedReceiver_Click",new Button{Tag="active-device-123456"});Pump();
    Check(calls.Last(c=>c.Command=="tablet-request").ClientId=="fixture-client","device shortcut routes to its exact receiver");
    var detailMenu=new ContextMenu();var menuAnchor=new Button{Tag="active-device-123456"};
    var detailItem=new MenuItem{Tag="active-device-123456"};detailMenu.Items.Add(detailItem);detailMenu.PlacementTarget=menuAnchor;
    Invoke("OpenBoundDeviceDetails_Click",detailItem);Pump();
    Check(N<Popup>("BoundDeviceDetailsPopover").PlacementTarget==menuAnchor&&N<TextBlock>("BoundDeviceNameText").Text=="宿舍平板","menu details retain the clicked device and popup anchor");
    N<Popup>("BoundDeviceDetailsPopover").IsOpen=false;
    files=files with{Devices=[files.Devices[0],new("fixture-phone","手机",new string('B',64),true)]};
    Invoke("OpenListedFiles_Click",new Button{Tag="phone-device-112233"});Pump();
    Check(pages.SelectedItem==N<TabItem>("FilesPage")&&N<ComboBox>("FileDeviceBox").SelectedValue as string=="fixture-phone","file shortcut selects its clicked device");
    files=files with{Devices=[files.Devices[0]]};Async("RefreshFilesAsync");
    Check(N<ComboBox>("FileDeviceBox").SelectedIndex<0&&!N<Button>("SendFileButton").IsEnabled,"missing target cannot silently fall back to another device");
    Invoke("OpenListedFiles_Click",new Button{Tag="active-device-123456"});Pump();
    Check(N<ComboBox>("FileDeviceBox").SelectedValue as string=="fixture-client","another device shortcut explicitly replaces the target");
    pages.SelectedItem=N<TabItem>("HomePage");
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
    Check(N<TextBlock>("AccountConnectionDetailText").Visibility==Visibility.Collapsed,"normal disabled network has no repeated explanatory paragraph");
    Save((FrameworkElement)N<Popup>("AccountPopover").Child,Path.Combine(root,"account-popover.png"));N<Popup>("AccountPopover").IsOpen=false;
    network=new(false,true,null,"测试：异地进程未启动");Refresh();Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionText").Text.Contains("异常")&&N<TextBlock>("AccountConnectionDetailText").IsVisible&&N<TextBlock>("AccountConnectionDetailText").Text==network.Message,"simplifying network text retains genuine failure reasons");
    Save((FrameworkElement)N<Popup>("AccountPopover").Child,Path.Combine(root,"network-error.png"));N<Popup>("AccountPopover").IsOpen=false;network=new(false,false,null,"未启用");Refresh();
    network=new(true,true,null,"内核已运行");networkDetails=new NetworkStatus{Available=true,Error="测试：连接服务不可达"};Async("RefreshDetailsAsync");Refresh();Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionDetailText").IsVisible&&N<TextBlock>("AccountConnectionDetailText").Text==networkDetails.Error,"running network retains a genuine detailed error");N<Popup>("AccountPopover").IsOpen=false;
    network=new(false,false,null,"未启用");Refresh();Invoke("OpenAccountPopover_Click");Pump();
    Check(N<TextBlock>("AccountConnectionDetailText").Visibility==Visibility.Collapsed,"stopped network hides generic or cached backend error");N<Popup>("AccountPopover").IsOpen=false;
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
      Check(category.Foreground is SolidColorBrush body&&body.Color==((SolidColorBrush)w.FindResource("TextPrimary")).Color,$"{theme}/{category.Header}: selected category does not tint its content");
     }
     N<TabControl>("SettingsCategories").SelectedItem=N<TabItem>("TabletSettingsCategory");Pump();
     Check(((ScrollViewer)N<TabItem>("TabletSettingsCategory").Content).ScrollableWidth<1&&N<ComboBox>("OrientationBox").IsVisible,"tablet preferences reachable from settings");
     Save((FrameworkElement)w.Content,Path.Combine(root,$"tablet-settings-{theme}.png"));
     foreach(string page in new[]{"AccountPage","DiagnosticsPage"}){
      pages.SelectedItem=N<TabItem>(page);Pump();w.UpdateLayout();AuditTypography((FrameworkElement)w.Content,$"{theme}/{page}");
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
     foreach(string opener in new[]{"OpenAccountPopover_Click","OpenTabletPopover_Click","OpenDeviceDetails_Click"}){
      Invoke(opener);Pump();
      string popupName=opener=="OpenAccountPopover_Click"?"AccountPopover":opener=="OpenTabletPopover_Click"?"TabletPopover":"DeviceDetailsPopover";
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
    File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,count=checks.Count,checks,installedServiceCalls=0,operatingSystemInputSent=false},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PASS {checks.Count} focused desktop checks.");
   }catch(Exception ex){File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Console.Error.WriteLine(ex);Environment.ExitCode=1;}
   finally{w.DisposeWindow();w.Close();app.Shutdown();}
  }));w.Show();app.Run();
 }
 static void Pump(int ms=80){var frame=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=(_,_)=>{t.Stop();frame.Continue=false;};t.Start();Dispatcher.PushFrame(frame);}
 static IEnumerable<DependencyObject> Descendants(DependencyObject node){yield return node;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(node,i)))yield return child;}
 static void Save(FrameworkElement e,string path){e.UpdateLayout();var b=new RenderTargetBitmap((int)e.ActualWidth,(int)e.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(e);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(path);enc.Save(f);}
}
