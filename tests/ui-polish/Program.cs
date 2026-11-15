using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Runtime;

internal static class Program
{
 [STAThread] static void Main(string[] args)
 {
  var root=Path.GetFullPath(args[0]);bool filesLayoutOnly=args.Contains("--files-layout-only");
  if(!System.Text.RegularExpressions.Regex.IsMatch(root,@"^D:\\Deepseek\\build\\stage\d+-tests\\desktop\\",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("Isolated fixture directory required");
  Directory.CreateDirectory(root);
  var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
  var checks=new List<string>();var calls=new List<RuntimeRequest>();var now=DateTimeOffset.UtcNow;
  var devices=new ControlDeviceList(false,[
   new("first-123456","client-one","很长的平板名称 PA2573 平板平板平板",ControlScope.All,false,now,null,"tablet",now),
   new("second-234567","client-two","手机",ControlScope.Remote,false,now,null,"phone",now),
   new("third-345678","client-three","无权限设备",ControlScope.None,false,now),
   new("old-456789","client-old","已撤销设备",ControlScope.All,true,now)]);
  bool filesAvailable=true;
  var files=new TransferSnapshot(new(false,root),[new("client-one","平板",new string('A',64),false)],[]);
  var receivers=new ReceiverSnapshot([],null);
  var engine=new EngineStatus("remote",true,true,null,48989,true,"已就绪");
  var status=new RuntimeStatus("fixture","隔离电脑",engine,engine with{Profile="tablet",Streaming=false},new(false,false,null,"未启用"),"source-only",root,now);
  var runtime=new RuntimeClient((r,_)=>{
   calls.Add(r);
   if(r.Command=="files-status"&&!filesAvailable)return Task.FromResult(new RuntimeResponse(false,"隔离文件接口已断开"));
   if(r.Command=="files-cancel")files=files with{Items=files.Items.Select(x=>x.Id==r.RequestId?x with{State="cancelled"}:x).ToArray()};
   string? payload=r.Command switch {
    "files-status"=>JsonSerializer.Serialize(files,RuntimeProtocol.JsonOptions),
    "control-device-list"=>JsonSerializer.Serialize(devices,RuntimeProtocol.JsonOptions),
    "receiver-list"=>JsonSerializer.Serialize(receivers,RuntimeProtocol.JsonOptions),
    "network-config"=>"{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
    "host-policy-status"=>JsonSerializer.Serialize(new HostPolicySnapshot(),RuntimeProtocol.JsonOptions),
    "binding-status"=>JsonSerializer.Serialize(new LocalBindingSnapshot(null,null,null),RuntimeProtocol.JsonOptions),_=>null};
   return Task.FromResult(new RuntimeResponse(true,"隔离状态",status,payload));
  });
  var w=new MainWindow(runtime,Path.Combine(root,"preferences.json")){Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,Width=940,Height=660};
  T N<T>(string name)where T:class=>(T)w.FindName(name);
  void Invoke(string name,object? sender=null){var method=typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!;method.Invoke(w,method.GetParameters().Length==0?null:[sender??w,new RoutedEventArgs()]);}
  void RefreshFiles(){typeof(MainWindow).GetMethod("RefreshFilesAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,null);Pump();}
  void RefreshReceivers(){typeof(MainWindow).GetMethod("RefreshReceiversAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,null);Pump();}
  void RefreshStatus(){Invoke("RefreshButton_Click");Pump();}
  void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
  object P(object row,string name)=>row.GetType().GetProperty(name)!.GetValue(row)!;
  object Row(string id)=>N<ItemsControl>("BoundDevicesList").Items.Cast<object>().Single(x=>(string)P(x,"Id")==id);
  w.Loaded+=(_,_)=>w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
   try {
    Pump();N<TabControl>("Pages").SelectedItem=N<TabItem>("FilesPage");Pump();
    Check(N<TabItem>("FilesPage").Content is Grid,"files use bounded grid, not nested page scrollers");
    Check(!N<Button>("SendFileButton").IsEnabled&&ToolTipService.GetShowOnDisabled(N<Button>("SendFileButton")),"disabled send explains its reason");
    Check(N<Button>("SendFileButton").ToolTip.ToString()==N<TextBlock>("FileInlineStateText").Text,"send tooltip shares the inline reason");
    Check(N<StackPanel>("FileTasksEmptyState").IsVisible,"empty tasks occupy the task area");
    files=files with{Settings=files.Settings with{Enabled=true}};RefreshFiles();
    Check(N<Button>("FileInlineAllowButton").IsVisible&&!N<Button>("SendFileButton").IsEnabled,"pending authorization has a local verification action");
    files=files with{Devices=[files.Devices[0] with{Allowed=true}]};RefreshFiles();
    Check(N<Button>("SendFileButton").IsEnabled&&N<Button>("FileEmptySendButton").IsVisible,"grant immediately enables toolbar and empty action");
    files=files with{Devices=[files.Devices[0] with{UsesDevicePermissions=true}]};RefreshFiles();
    Invoke("GrantFiles_Click",new Button{Tag="allow"});Pump();
    Check((string)N<ComboBox>("ControlDeviceBox").SelectedValue=="first-123456"&&!calls.Any(r=>r.Command=="files-grant"),"bound file action opens central device permissions without another grant");Invoke("CloseDevicePermissions_Click");
    filesAvailable=false;RefreshFiles();Check(!N<Button>("SendFileButton").IsEnabled&&N<Button>("SendFileButton").ToolTip.ToString()==N<TextBlock>("FileInlineStateText").Text,"failed file status invalidates a previous grant snapshot");filesAvailable=true;RefreshFiles();
    files=files with{Items=[new("receiving","client-one","upload","正在接收的很长文件名 中文 emoji.txt",1024,"",512),new("complete","client-one","download","完成.zip",0,"",State:"completed"),new("cancelled","client-one","upload","取消.bin",long.MaxValue,"",long.MaxValue,State:"cancelled")]};RefreshFiles();
    var list=N<ListBox>("FileTaskList");Check(list.Items.Count==3&&!N<StackPanel>("FileTasksEmptyState").IsVisible,"three task states replace empty overlay");
    var first=list.Items[0];Check((int)P(first,"Percent")==50&&(string)P(first,"Direction")=="电脑接收"&&(string)P(first,"Size")=="1 KiB"&&(bool)P(first,"CanCancel"),"task fields show receiving progress, size and direction");
    Check((int)P(list.Items[1],"Percent")==100&&!(bool)P(list.Items[1],"CanCancel")&&(int)P(list.Items[2],"Percent")==100,"zero-size completion and huge sizes avoid overflow");
    foreach(string id in new[]{"complete","cancelled"}){list.SelectedValue=id;int before=calls.Count(x=>x.Command=="files-cancel");Invoke("CancelFile_Click");Pump();Check(!N<Button>("CancelFileButton").IsEnabled&&calls.Count(x=>x.Command=="files-cancel")==before,"ended task cannot be cancelled: "+id);}
    list.SelectedValue="receiving";Check(N<Button>("CancelFileButton").IsEnabled,"active task can be selected for cancellation");Invoke("CancelFile_Click");Pump();
    Check(calls.Count(x=>x.Command=="files-cancel")==1&&calls.Single(x=>x.Command=="files-cancel").RequestId=="receiving","cancel identifies only the selected active task");
    // Rendering at these DPIs is an offline pixel check, not a change to OS display scaling.
    foreach(int theme in new[]{1,2})foreach(int width in new[]{940,1280})foreach(double dpi in new[]{120d,144d}){
     N<ComboBox>("ThemeChoice").SelectedIndex=theme;w.Width=width;w.Height=width==940?660:760;w.UpdateLayout();Pump();
     var send=N<Button>("SendFileButton");var box=N<ComboBox>("FileDeviceBox");
     Check(box.ActualWidth>=220&&box.TranslatePoint(new Point(box.ActualWidth,0),send).X<=0,$"toolbar separated theme {theme}, width {width}, dpi {dpi}");
     Check(Math.Abs(box.ActualWidth-280)<1,"file selector stays at 280 device-independent pixels");
     var directory=N<TextBlock>("FileDirectoryText");var open=N<Button>("FileOpenDirectoryButton");var gap=directory.TranslatePoint(new Point(directory.ActualWidth,0),open).X;
     Save((FrameworkElement)w.Content,Path.Combine(root,$"files-{theme}-{width}-{dpi}.png"),dpi);
     Check(gap>=-9&&gap<=0,$"receive directory and open action form one tight group (gap={gap}, directory={directory.ActualWidth}, open={open.ActualWidth})");
     Check(list.ActualHeight>250&&Descendants(list).OfType<ScrollViewer>().First().ViewportHeight>0,"task list stretches and scrolls internally");
     foreach(var button in new[]{N<Button>("FileOpenDirectoryButton"),send}){var pos=button.TranslatePoint(new Point(),(UIElement)N<TabItem>("FilesPage").Content);Check(pos.X>=0&&pos.X+button.ActualWidth<=((FrameworkElement)N<TabItem>("FilesPage").Content).ActualWidth+1,"toolbar actions inside minimum viewport");}
     Save((FrameworkElement)w.Content,Path.Combine(root,$"files-{theme}-{width}-{dpi}.png"),dpi);
    }
    w.Width=940;w.Height=660;N<TabControl>("Pages").SelectedItem=N<TabItem>("HomePage");RefreshStatus();
    Check(!(bool)P(Row("first-123456"),"Online")&&!(bool)P(Row("second-234567"),"Online"),"recent contact alone does not imply online");
    Check(((string[])P(Row("second-234567"),"Chips")).SequenceEqual(new[]{"主屏"})&&((string[])P(Row("third-345678"),"Chips")).SequenceEqual(new[]{"无功能权限"}),"single and no-scope chips accurate");
    var chips=(string[])P(Row("first-123456"),"Chips");Check(chips.Contains("主屏")&&chips.Contains("副屏")&&chips.Contains("文件"),"feature chips show permission names without duplicate approval state");
    w.UpdateLayout();Pump();
    var name=Descendants(N<ItemsControl>("BoundDevicesList")).OfType<TextBlock>().First(t=>t.Text==devices.Devices[0].Name);
    var type=Descendants((DependencyObject)name.Parent).OfType<TextBlock>().Single(t=>t.Text=="平板");
    var chipGap=name.TranslatePoint(new Point(name.ActualWidth,0),type).X;
    Check(chipGap>=-17&&chipGap<=-7,"device type follows its name instead of the right-hand actions");
    if(filesLayoutOnly){Save((FrameworkElement)w.Content,Path.Combine(root,"devices-minimum.png"),144);File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,count=checks.Count,checks,installedServiceCalls=0,operatingSystemInputSent=false},new JsonSerializerOptions{WriteIndented=true}));return;}
    receivers=new([new("client-one","平板",2560,1600,60,[0,90],now.AddSeconds(-31))],null);RefreshReceivers();Invoke("RenderUnifiedDevices");
    Check(!(bool)P(Row("first-123456"),"Online"),"expired receiver is not online");
    receivers=new([receivers.Receivers[0] with{LastSeen=DateTimeOffset.UtcNow}],null);RefreshReceivers();Invoke("RenderUnifiedDevices");
    Check((bool)P(Row("first-123456"),"Online"),"fresh receiver is online");
    receivers=new([],null);RefreshReceivers();status=status with{Remote=engine with{Streaming=true},Controller=new("second-234567","手机","fixture",true)};RefreshStatus();
    Check((bool)P(Row("second-234567"),"Online")&&!(bool)P(Row("first-123456"),"Online"),"active authenticated session marks only its own device");
    var detail=Descendants(N<ItemsControl>("BoundDevicesList")).OfType<Button>().First(b=>b.Tag as string=="first-123456"&&b.Content as string=="\uE946");Invoke("OpenBoundDeviceDetails_Click",detail);
    Check(N<TextBlock>("BoundDeviceDetailText").Text.Contains("ID first-123456"),"identity lives in device details");N<Popup>("BoundDeviceDetailsPopover").IsOpen=false;
    var menuButton=Descendants(N<ItemsControl>("BoundDevicesList")).OfType<Button>().First(b=>b.Tag as string=="first-123456"&&b.ContextMenu is not null);
    Invoke("OpenDeviceMenu_Click",menuButton);Pump();var menu=menuButton.ContextMenu!;
    var rename=(MenuItem)menu.Items[0];Check(rename.Tag as string=="first-123456","device context menu binds to its placement target identity");
    menu.IsOpen=false;rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Check(N<Grid>("AliasDialog").IsVisible,"rename accepts menu item credential id");Invoke("CloseDeviceAlias_Click");
    var permissions=(MenuItem)menu.Items[1];permissions.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Check((string)N<ComboBox>("ControlDeviceBox").SelectedValue=="first-123456","permissions menu selects the correct identity");Invoke("CloseDevicePermissions_Click");
    w.UpdateLayout();Pump();Save((FrameworkElement)w.Content,Path.Combine(root,"devices-minimum.png"),144);
    Invoke("OpenTabletPopover_Click");Pump();Save((FrameworkElement)N<Popup>("TabletPopover").Child,Path.Combine(root,"receivers.png"),144);N<Popup>("TabletPopover").IsOpen=false;
    Invoke("OpenHelp_Click");Pump();var close=N<Button>("HelpCloseButton");
    Check(close.Width==32&&close.Height==32&&AutomationProperties.GetName(close)=="关闭使用手册","manual icon has a small target and readable name");
    close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Check(!N<Grid>("HelpDialog").IsVisible,"manual close icon operates");
    Invoke("OpenHelp_Click");N<Grid>("HelpDialog").RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(w),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});Pump();Check(!N<Grid>("HelpDialog").IsVisible,"manual closes on Escape");
    // Test routed input on the fixture only; no SendInput, ADB or installed runtime.
    w.Activate();var home=N<RadioButton>("HomeNavigation");home.Focus();Pump();
    Check(home.IsKeyboardFocused,"fixture can focus the actual navigation control");
    home.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(w),0,Key.Tab){RoutedEvent=Keyboard.PreviewKeyDownEvent});
    home.ApplyTemplate();var frame=(Border)home.Template.FindName("Frame",home);Pump();
    Check(FocusInput.GetKeyboardMode(home)&&((SolidColorBrush)frame.BorderBrush).Color==((SolidColorBrush)w.FindResource("Accent")).Color,"keyboard Tab paints focus ring");
    home.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});Pump();
    Check(!FocusInput.GetKeyboardMode(home)&&((SolidColorBrush)frame.BorderBrush).Color==Colors.Transparent&&home.FocusVisualStyle is null,"pointer clears template and decorator focus rings");
    void FocusRing(Control control,string target){
     control.Focus();Pump();Check(control.IsKeyboardFocused,"keyboard focus reaches "+control.GetType().Name);
     control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(w),0,Key.Tab){RoutedEvent=Keyboard.PreviewKeyDownEvent});Pump();
     control.ApplyTemplate();var ink=(Border)control.Template.FindName(target,control);
     Check(((SolidColorBrush)ink.BorderBrush).Color==((SolidColorBrush)w.FindResource("Accent")).Color,"Tab focus is visible on "+control.GetType().Name);
     control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});Pump();
     Check(!FocusInput.GetKeyboardMode(control)&&control.FocusVisualStyle is null,"pointer removes keyboard ring and decorator from "+control.GetType().Name);
    }
    FocusRing(N<Button>("HeaderMenuButton"),"Frame");FocusRing(N<RadioButton>("SettingsNavigation"),"Frame");
    N<TabControl>("Pages").SelectedItem=N<TabItem>("SettingsPage");Pump();
    foreach(TabItem tab in N<TabControl>("SettingsCategories").Items)FocusRing(tab,"Frame");
    N<TabControl>("SettingsCategories").SelectedIndex=0;Pump();FocusRing(N<ComboBox>("ThemeChoice"),"InputFrame");
    // Enter raises the manual close command; Escape and focus return remain intact.
    Invoke("OpenHelp_Click");Pump();N<Button>("HelpCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Check(N<Button>("HeaderMenuButton").IsKeyboardFocused,"manual close returns keyboard focus to the header");
    Check(!calls.Any(r=>r.Command is "files-permissions" or "control-device-scope" or "network-provider"),"view rendering did not mutate device permissions or network");
    File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,count=checks.Count,checks,installedServiceCalls=0,operatingSystemInputSent=false,dpiMethod="RenderTargetBitmap 120/144 DPI; OS scaling unchanged"},new JsonSerializerOptions{WriteIndented=true}));
   }catch(Exception error){Environment.ExitCode=1;File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=false,error=error.ToString(),checks}));}
   finally{w.DisposeWindow();w.Close();app.Shutdown();}
  }));w.Show();app.Run();
 }
 static void Pump(int ms=65){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
 static void Save(FrameworkElement element,string path,double dpi){element.UpdateLayout();double scale=dpi/96;var image=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),dpi,dpi,PixelFormats.Pbgra32);image.Render(element);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);png.Save(file);}
}
