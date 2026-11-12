using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHub.Host;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        var root = Path.GetFullPath(args.Single());
        if (!System.Text.RegularExpressions.Regex.IsMatch(root,@"^D:\\Deepseek\\build\\stage\d+-tests\\desktop\\",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new Exception("Isolated preview path required.");
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
        var runtime = new RuntimeClient((request,token) =>
        {
            token.ThrowIfCancellationRequested();
            var status = new RuntimeStatus("fixture", "示例电脑", new("remote",true,true,1,48989,true,"远程待机"),
                new("tablet",false,false,null,49089,false,"副屏未启用"),new(false,false,null,"仅局域网"),"0.8.2",root,DateTimeOffset.UtcNow);
            string? payload = request.Command switch
            {
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(),[],[]),RuntimeProtocol.JsonOptions),
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot("012345",DateTimeOffset.UtcNow.AddMinutes(3),null),RuntimeProtocol.JsonOptions),
                "control-info" => JsonSerializer.Serialize(new ControlApiStatus(true,true,"fixture-preview-invitation",null),RuntimeProtocol.JsonOptions),
                _ => null
            };
            return Task.FromResult(new RuntimeResponse(true,"本地界面预览 · 不连接已安装服务",status,payload));
        });
        var window = new MainWindow(runtime,Path.Combine(root,"ui-preferences.json")) { Title="我控制 · 本地界面预览", Left=-20000, Top=-20000, ShowActivated=false, ShowInTaskbar=false };
        window.Loaded += (_,_) => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                window.Width=940; window.Height=660; window.UpdateLayout();
                var pages=(TabControl)window.FindName("Pages");
                var navigation=new List<object>(); double previousBottom=double.NaN;
                foreach(TabItem item in pages.Items)
                {
                    item.IsSelected=true; window.UpdateLayout();
                    var frame=(Border)item.Template.FindName("TabFrame",item);
                    if(frame.ActualHeight<48 || item.ActualHeight+0.5<frame.ActualHeight) throw new Exception("Navigation capsule clipped: "+item.Header);
                    double top=frame.TranslatePoint(new Point(),pages).Y;
                    double gap=double.IsNaN(previousBottom)?0:top-previousBottom;
                    if(!double.IsNaN(previousBottom) && (gap<7.5 || gap>8.5)) throw new Exception("Inconsistent navigation gap: "+item.Header+" "+gap);
                    navigation.Add(new {label=item.Header,top,height=frame.ActualHeight,gap});
                    previousBottom=top+frame.ActualHeight;
                }
                var pair=(ComboBox)window.FindName("PairMode");
                pages.SelectedIndex=1; window.UpdateLayout(); pair.ApplyTemplate();
                var toggle=(ToggleButton)pair.Template.FindName("DropDownButton",pair);
                if(toggle.ActualWidth < pair.ActualWidth-4)throw new Exception("ComboBox click target does not cover field");
                foreach(int theme in new[]{1,2,0}) { ((ComboBox)window.FindName("ThemeChoice")).SelectedIndex=theme; window.UpdateLayout(); }
                pages.SelectedItem=window.FindName("SettingsPage"); window.UpdateLayout();
                void Capture(string name) {
                    window.UpdateLayout(); var content=(FrameworkElement)window.Content;
                    var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);
                    var backdrop=new DrawingVisual();using(var drawing=backdrop.RenderOpen())drawing.DrawRectangle(window.Background,null,new Rect(0,0,content.ActualWidth,content.ActualHeight));
                    bitmap.Render(backdrop);
                    bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output=File.Create(Path.Combine(root,name));encoder.Save(output);
                }
                foreach(int theme in new[]{1,2}) {
                    ((ComboBox)window.FindName("ThemeChoice")).SelectedIndex=theme; window.UpdateLayout();
                    pages.SelectedIndex=1;window.UpdateLayout();Capture("pc-binding-"+(theme==1?"light":"dark")+".png");
                    pages.SelectedItem=window.FindName("SettingsPage");window.UpdateLayout();
                    var state=(TextBlock)window.FindName("UpdateStateText");
                    ((FrameworkElement)((FrameworkElement)state.Parent).Parent).BringIntoView();window.UpdateLayout();Capture("pc-settings-"+(theme==1?"light":"dark")+".png");
                }
                File.WriteAllText(Path.Combine(root,"layout-checks.json"),JsonSerializer.Serialize(new { passed=true, navigation,
                    fieldWidth=pair.ActualWidth, hitWidth=toggle.ActualWidth, minWindow="940x660", themes="light,dark,system", installedServiceCalls=0 }));
            }
            catch(Exception error) { File.WriteAllText(Path.Combine(root,"layout-checks.json"),JsonSerializer.Serialize(new { passed=false, error=error.ToString() })); }
            finally { app.Shutdown(); }
        }));
        window.Closed += (_,_) => window.DisposeWindow();
        app.Run(window);
    }
}
