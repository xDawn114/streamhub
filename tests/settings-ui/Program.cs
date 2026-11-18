using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
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
        string root = Path.GetFullPath(args.Single()); Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        void Check(bool passed, string label) { if (!passed) throw new Exception(label); checks.Add(label); }
        var fixture = new ControlDeviceList(true,[new("fixture-device","fixture-client","示例平板",ControlScope.All,false,DateTimeOffset.UtcNow)]);
        var runtime = new RuntimeClient((request, _) =>
        {
            var status = new RuntimeStatus("fixture","示例电脑",new("remote",false,true,null,48989,false,"待机"),
                new("tablet",false,false,null,49089,false,"停止"),new(false,false,null,"停止"),"0.8.13",root,DateTimeOffset.UtcNow);
            string? payload = request.Command switch
            {
                "control-device-list" => JsonSerializer.Serialize(fixture,RuntimeProtocol.JsonOptions),
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(),[],[]),RuntimeProtocol.JsonOptions),
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot(null,null,null),RuntimeProtocol.JsonOptions),
                _ => null
            };
            return Task.FromResult(new RuntimeResponse(true,"本机隔离夹具",status,payload));
        });
        var window = new MainWindow(runtime,Path.Combine(root,"preferences.json"))
            { Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,Width=940,Height=660 };
        window.Loaded += (_,_) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() =>
        {
            try
            {
                var pages=(TabControl)window.FindName("Pages"); pages.SelectedItem=window.FindName("SettingsPage");
                var categories=(TabControl)window.FindName("SettingsCategories");
                Check(categories.Items.Count==6,"six distinct settings categories");
                foreach (int theme in new[]{1,2})
                {
                    ((ComboBox)window.FindName("ThemeChoice")).SelectedIndex=theme;
                    foreach(int category in Enumerable.Range(0,6))
                    {
                        categories.SelectedIndex=category; window.UpdateLayout(); Pump();
                        var item=(TabItem)categories.SelectedItem;
                        var viewer=(ScrollViewer)item.Content; viewer.ScrollToTop(); window.UpdateLayout();
                        Check(item.ActualWidth>30 && item.ActualWidth<240,"category header has intrinsic width");
                        Check(viewer.ActualWidth>500 && viewer.ActualWidth<window.ActualWidth,"settings page fits minimum desktop width");
                        foreach(var button in Descendants<Button>(viewer))
                        {
                            if(!button.IsVisible || button.ActualWidth==0) continue;
                            var point=button.TransformToAncestor(viewer).Transform(new Point());
                            Check(point.X>=-1 && point.X+button.ActualWidth<=viewer.ActualWidth+1,"settings action fits available width: "+button.Content);
                        }
                        if(theme==1 || category==1) Save(window,Path.Combine(root,$"settings-{theme}-{category}.png"));
                    }
                }
                categories.SelectedIndex=0;
                typeof(MainWindow).GetMethod("OpenFileAccessSettings_Click",BindingFlags.Instance|BindingFlags.NonPublic)!
                    .Invoke(window,[window,new RoutedEventArgs()]); window.UpdateLayout();Pump();
                Check(((Grid)window.FindName("DevicePermissionsDialog")).Visibility==Visibility.Visible && ((Expander)window.FindName("FileAccessDetails")).IsExpanded,"file settings shortcut opens device permission dialog");
                Check(((TextBlock)window.FindName("ReceiveDirectoryText")).Text=="请选择电脑接收文件夹","receive-directory entry follows file state");

                // Real bubbling event: the child handles MouseMove, the viewer must still receive it.
                var dictionary=new ResourceDictionary{Source=new Uri("pack://application:,,,/我控制;component/DesktopChrome.xaml")};
                var child=new Border{Width=24,Height=900};
                var viewerFixture=new ScrollViewer{Width=24,Height=160,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=child};
                viewerFixture.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/我控制;component/Theme.Light.xaml")});
                viewerFixture.Resources.MergedDictionaries.Add(dictionary);
                viewerFixture.Measure(new Size(24,160));viewerFixture.Arrange(new Rect(0,0,24,160));viewerFixture.UpdateLayout();
                child.AddHandler(Mouse.MouseMoveEvent,new MouseEventHandler((_,e)=>e.Handled=true));
                child.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseMoveEvent});
                Check(ScrollActivity.GetPointerNear(viewerFixture),"handled child MouseMove reaches real viewer behavior");
                var bar=(ScrollBar)viewerFixture.Template.FindName("PART_VerticalScrollBar",viewerFixture);
                Check(bar.Opacity==1,"near-edge state makes scrollbar visible");
                bar.Style=null;bar.Style=(Style)dictionary[typeof(ScrollBar)];
                Check(bar.Opacity==1,"style reapplication retains visible hover state");
                Pump(1300);Check(bar.Opacity==1 && ScrollActivity.GetPointerNear(viewerFixture),"hover survives idle timer");

                // Switch actual, loaded WPF tabs; do not fabricate a Loaded notification.
                categories.SelectedIndex=1; window.UpdateLayout();Pump();
                var securityViewer=(ScrollViewer)((TabItem)categories.SelectedItem).Content;
                int loaded=0,unloaded=0;securityViewer.Loaded+=(_,_)=>loaded++;securityViewer.Unloaded+=(_,_)=>unloaded++;
                securityViewer.SetValue(ScrollActivity.PointerNearProperty,true);
                categories.SelectedIndex=0;window.UpdateLayout();Pump();
                Check(!ScrollActivity.GetPointerNear(securityViewer),"actual tab unload clears stale hover");
                categories.SelectedIndex=1;window.UpdateLayout();Pump();
                var pointer=Mouse.GetPosition(securityViewer);
                bool inside=pointer.X>=0 && pointer.Y>=0 && pointer.X<=securityViewer.ActualWidth && pointer.Y<=securityViewer.ActualHeight;
                bool expected=inside && (securityViewer.ComputedVerticalScrollBarVisibility==Visibility.Visible && pointer.X>=securityViewer.ActualWidth-24 || securityViewer.ComputedHorizontalScrollBarVisibility==Visibility.Visible && pointer.Y>=securityViewer.ActualHeight-24);
                Check(loaded>0 && unloaded>0 && ScrollActivity.GetPointerNear(securityViewer)==expected,"actual tab reload recalculates pointer proximity");
                var noticeType=typeof(MainWindow).Assembly.GetType("StreamHub.Host.SessionNoticeWindow",true)!;
                var describe=noticeType.GetMethod("Describe",BindingFlags.Static|BindingFlags.NonPublic)!;
                string identity=(string)describe.Invoke(null,[new ControllerSession("fixture","已核对平板","局域网 / USB",true)])!;
                Check(identity.Contains("已核对平板") && identity.Contains("独立设备授权"),"notice uses server-authenticated controller label");
                string oldIdentity=(string)describe.Invoke(null,[new ControllerSession("legacy","unverified-name","局域网 / USB",false)])!;
                Check(oldIdentity.Contains("无法区分") && !oldIdentity.Contains("unverified-name"),"legacy notice never claims individual device identity");
                var noticeRuntime=new RuntimeClient((_,_)=>Task.FromResult(new RuntimeResponse(true,"fixture",new RuntimeStatus("fixture","fixture",
                    new("remote",true,true,null,48989,true,"串流",Streaming:true),new("tablet",false,false,null,49089,false,"停止"),
                    new(false,false,null,"停止"),"fixture",root,DateTimeOffset.UtcNow,Controller:new("fixture","已核对平板","局域网 / USB",true)))));
                var notice=(Window)Activator.CreateInstance(noticeType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[noticeRuntime],null)!;
                ((Task)noticeType.GetMethod("RefreshAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(notice,[])!).GetAwaiter().GetResult();
                Check(notice.Topmost && !notice.ShowActivated && !notice.ShowInTaskbar,"notice avoids activation and taskbar clutter");
                var content=(FrameworkElement)notice.Content;content.Width=460;content.Measure(new Size(460,200));content.Arrange(new Rect(0,0,460,content.DesiredSize.Height));content.UpdateLayout();
                Save(content,Path.Combine(root,"session-notice.png"));
                File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,count=checks.Count,checks,installedServiceCalls=0,operatingSystemInputSent=false},new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"PASS {checks.Count} isolated settings/scroll checks.");
            }
            catch(Exception ex)
            { File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=false,error=ex.ToString()}));Console.Error.WriteLine(ex);Environment.ExitCode=1; }
            finally { window.DisposeWindow(); app.Shutdown(); }
        }));
        window.Show(); app.Run();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        { var child=VisualTreeHelper.GetChild(parent,i);if(child is T result)yield return result;foreach(var descendant in Descendants<T>(child))yield return descendant; }
    }
    static void Pump(int milliseconds=30)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    static void Save(FrameworkElement element,string path)
    {
        var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
