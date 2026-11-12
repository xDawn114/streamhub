using System.IO;
using System.Reflection;
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
        var root=Path.GetFullPath(args.Single());
        if(!System.Text.RegularExpressions.Regex.IsMatch(root,@"^D:\\Deepseek\\build\\stage\d+-tests\\desktop\\",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("Isolated fixture required.");
        Directory.CreateDirectory(root);
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var snapshot=new TransferSnapshot(new(true,Path.Combine(root,"receive"),true,[new("one","下载",Path.Combine(root,"示例目录","下载"),"fixture-owner")]),
            [new("fixture-device","示例平板",new string('A',64),true)],[]);
        int calls=0,checks=0;
        var runtime=new RuntimeClient((request,token)=>{
            calls++;
            if(request.Command=="files-permissions")snapshot=snapshot with { Devices=[snapshot.Devices[0] with {UploadAllowed=request.UploadAllowed==true,DeleteAllowed=request.DeleteAllowed==true}]};
            string? payload=request.Command switch {
                "files-status"=>JsonSerializer.Serialize(snapshot,RuntimeProtocol.JsonOptions),
                "network-config"=>"{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status"=>JsonSerializer.Serialize(new LocalBindingSnapshot("012345",DateTimeOffset.UtcNow.AddMinutes(3),null),RuntimeProtocol.JsonOptions),
                _=>null
            };
            var status=new RuntimeStatus("fixture","示例电脑",new("remote",true,true,1,48989,true,"待机"),new("tablet",false,false,null,49089,false,"停止"),new(false,false,null,"局域网"),"0.8.6",root,DateTimeOffset.UtcNow);
            return Task.FromResult(new RuntimeResponse(true,"隔离样本",status,payload));
        });
        var window=new MainWindow(runtime,Path.Combine(root,"ui-preferences.json")) {Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,Width=940,Height=860};
        window.Loaded+=(_,_)=>window.Dispatcher.BeginInvoke(new Action(async()=>{
            try {
                var pages=(TabControl)window.FindName("Pages");
                pages.SelectedItem=pages.Items.OfType<TabItem>().Single(x=>(string)x.Header=="文件");
                var refresh=typeof(MainWindow).GetMethod("RefreshFilesAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
                await (Task)refresh.Invoke(window,null)!;
                var upload=(CheckBox)window.FindName("FileUploadAllowed");upload.IsChecked=false;
                upload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,upload));
                await (Task)refresh.Invoke(window,null)!;
                Check(upload.IsChecked==false,"automatic refresh replaced an unsaved permission");
                var roots=(ListBox)window.FindName("BrowseRootList");Check(roots.Items.Count==1,"authorized roots missing");
                snapshot=snapshot with {Settings=new(true,Path.Combine(root,"receive"),true)};
                await (Task)refresh.Invoke(window,null)!;
                Check(roots.Items.Count==1,"legacy receive folder migration disappeared");
                var modes=new List<object>();
                foreach(int theme in new[]{1,2})foreach(int width in new[]{940,1280}) {
                    ((ComboBox)window.FindName("ThemeChoice")).SelectedIndex=theme;window.Width=width;window.UpdateLayout();
                    var card=(FrameworkElement)((FrameworkElement)window.FindName("BrowseRootList")).Parent;
                    card=(FrameworkElement)card.Parent;card.BringIntoView();window.UpdateLayout();
                    foreach(var wrap in Descendants(card).OfType<WrapPanel>())foreach(var button in wrap.Children.OfType<Button>()) {
                        var position=button.TransformToAncestor(card).Transform(new Point());
                        Check(button.ActualHeight>=40 && position.X>=0 && position.X+button.ActualWidth<=card.ActualWidth+1 && button.Margin.Right>=8,"file action clipped or spacing missing");
                    }
                    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth),(int)Math.Ceiling(card.ActualHeight),96,96,PixelFormats.Pbgra32);
                    var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())drawing.DrawRectangle(new VisualBrush(card),null,new Rect(0,0,card.ActualWidth,card.ActualHeight));bitmap.Render(visual);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(root,$"files-{theme}-{width}.png")))png.Save(output);
                    modes.Add(new {theme,width,cardWidth=card.ActualWidth,cardHeight=card.ActualHeight});
                }
                File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new {passed=true,checks,modes,fixtureCalls=calls,installedServiceCalls=0}));
            }catch(Exception error){Environment.ExitCode=1;File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new {passed=false,error=error.ToString()}));}
            finally{window.DisposeWindow();app.Shutdown();}
        }));
        window.Show();app.Run();
        void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
}
