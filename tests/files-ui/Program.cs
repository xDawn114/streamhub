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
            [new("fixture-device","示例平板",new string('A',64),true,DeleteAllowed:true)],[]);
        int calls=0,checks=0,permissionWrites=0,controlWrites=0;
        bool failPermissionSave=false;
        bool failControlSave=false;
        bool failNetworkStatus=false;
        TaskCompletionSource<RuntimeResponse>? pendingPermission=null;
        TaskCompletionSource<RuntimeResponse>? pendingControl=null;
        ControlScope scope=ControlScope.Remote;
        var runtime=new RuntimeClient((request,token)=>{
            calls++;
            if(request.Command=="network-status"&&failNetworkStatus)throw new IOException("fixture network unavailable");
            if(request.Command=="files-permissions") {
                permissionWrites++;
                if(pendingPermission is not null)return pendingPermission.Task;
                if(failPermissionSave)return Task.FromResult(new RuntimeResponse(false,"fixture save failed"));
                snapshot=snapshot with { Devices=[snapshot.Devices[0] with {UploadAllowed=request.UploadAllowed==true,DeleteAllowed=request.DeleteAllowed==true}]};
            }
            if(request.Command=="control-device-scope") {
                controlWrites++;
                if(pendingControl is not null)return pendingControl.Task;
                if(failControlSave)return Task.FromResult(new RuntimeResponse(false,"fixture scope save failed"));
                scope=(ControlScope)(request.DeviceScope??0);
                snapshot=snapshot with {Devices=[snapshot.Devices[0] with {Allowed=(scope&ControlScope.Files)!=0}]};
            }
            string? payload=request.Command switch {
                "files-status"=>JsonSerializer.Serialize(snapshot,RuntimeProtocol.JsonOptions),
                "control-device-list"=>JsonSerializer.Serialize(new ControlDeviceList(false,[new("fixture-binding","fixture-device","示例平板",scope,false,DateTimeOffset.UtcNow)]),RuntimeProtocol.JsonOptions),
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
                var deviceBox=(ComboBox)window.FindName("FileDeviceBox");
                var permissionsBox=(ComboBox)window.FindName("SettingsFileDeviceBox");
                failNetworkStatus=true;deviceBox.ItemsSource=null;
                await (Task)typeof(MainWindow).GetMethod("RefreshDetailsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
                Check(deviceBox.Items.Count==1&&(string?)deviceBox.SelectedValue=="fixture-device","file devices load even when network status throws");
                failNetworkStatus=false;
                Check((string?)deviceBox.SelectedValue=="fixture-device"&&(string?)permissionsBox.SelectedValue=="fixture-device","both selectors initially choose the registered device");
                await CheckDevicePopup(deviceBox,"fixture-device","示例平板","file-selector");
                var deviceSource=deviceBox.ItemsSource;
                deviceBox.IsDropDownOpen=true;
                await (Task)refresh.Invoke(window,null)!;
                Check(ReferenceEquals(deviceSource,deviceBox.ItemsSource)&&deviceBox.IsDropDownOpen&&(string?)deviceBox.SelectedValue=="fixture-device","unchanged periodic refresh retains the open menu and selection");
                deviceBox.IsDropDownOpen=false;
                typeof(MainWindow).GetMethod("OpenFileAccessSettings_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[window,new RoutedEventArgs()]);
                window.UpdateLayout();
                await CheckDevicePopup(permissionsBox,"fixture-device","示例平板","permissions-selector");
                var originalDevice=snapshot.Devices[0];
                snapshot=snapshot with {Devices=[originalDevice,originalDevice with {ClientId="second-device",Name="第二台平板"}]};
                await (Task)refresh.Invoke(window,null)!;
                await CheckDevicePopup(permissionsBox,"second-device","第二台平板","permissions-second-device");
                Check((string?)deviceBox.SelectedValue=="second-device","permission selector updates the file target");
                deviceBox.SelectedValue="fixture-device";
                Check((string?)permissionsBox.SelectedValue=="fixture-device","file target updates the permission selector");
                deviceBox.SelectedValue="second-device";
                snapshot=snapshot with {Devices=[originalDevice]};
                await (Task)refresh.Invoke(window,null)!;
                Check(deviceBox.SelectedItem is null&&permissionsBox.SelectedItem is null&&!((Button)window.FindName("SendFileButton")).IsEnabled,"removing the selected device does not substitute another recipient");
                var configuredSettings=snapshot.Settings;
                snapshot=snapshot with {Devices=[],Settings=new(false,"",false)};
                await (Task)refresh.Invoke(window,null)!;
                Check(!deviceBox.IsEnabled&&!permissionsBox.IsEnabled&&deviceBox.SelectedItem is null,"empty device list has no blank enabled menu or stale selection");
                Check(((Button)window.FindName("FileOpenDirectoryButton")).IsEnabled,"receive directory can be chosen before enabling files or registering a device");
                snapshot=snapshot with {Devices=[originalDevice],Settings=configuredSettings};
                await (Task)refresh.Invoke(window,null)!;
                Check(deviceBox.IsEnabled&&permissionsBox.IsEnabled&&deviceBox.SelectedItem is null&&permissionsBox.SelectedItem is null,"device arrival restores selectors without substituting a missing recipient");
                deviceBox.SelectedValue="fixture-device";
                Check((string?)permissionsBox.SelectedValue=="fixture-device","user can explicitly select the remaining device after removal");
                var upload=(CheckBox)window.FindName("FileUploadAllowed");upload.IsChecked=false;
                upload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,upload));
                await (Task)refresh.Invoke(window,null)!;
                Check(upload.IsChecked==false&&permissionWrites==1&&!snapshot.Devices[0].UploadAllowed,"upload changes persist immediately without a save button");
                failPermissionSave=true;upload.IsChecked=true;
                upload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,upload));
                Check(upload.IsChecked==false&&!snapshot.Devices[0].UploadAllowed&&((TextBlock)window.FindName("FilePermissionStatus")).Text.Contains("保存失败"),"failed save rolls the checkbox back and reports failure");
                failPermissionSave=false;
                pendingPermission=new(TaskCreationOptions.RunContinuationsAsynchronously);
                upload.IsChecked=true;upload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,upload));
                await (Task)refresh.Invoke(window,null)!;
                Check(upload.IsChecked==true&&!upload.IsEnabled,"refresh cannot replace a permission while its save is pending");
                snapshot=snapshot with {Devices=[snapshot.Devices[0] with {UploadAllowed=true}]};
                var pending=pendingPermission;pendingPermission=null;pending.SetResult(new RuntimeResponse(true,"saved"));
                while(!upload.IsEnabled)await System.Windows.Threading.Dispatcher.Yield();
                Check(upload.IsChecked==true,"successful asynchronous save retains the requested permission");
                upload.IsChecked=false;upload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,upload));
                var delete=(CheckBox)window.FindName("FileDeleteAllowed");delete.IsChecked=false;
                delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,delete));
                Check(!snapshot.Devices[0].DeleteAllowed,"delete revocation persists immediately without another confirmation");
                snapshot=snapshot with {Devices=[snapshot.Devices[0] with {UsesDevicePermissions=true,Allowed=false}]};
                await (Task)refresh.Invoke(window,null)!;
                Check(((Button)window.FindName("FileGrantButton")).Content.ToString()=="信任此设备","trust action is visible for the selected device");
                var grant=typeof(MainWindow).GetMethod("GrantFiles_Click",BindingFlags.Instance|BindingFlags.NonPublic)!;
                grant.Invoke(window,[new Button{Tag="allow"},new RoutedEventArgs()]);
                Check(scope==(ControlScope.Remote|ControlScope.Files)&&snapshot.Devices[0].Allowed&&!snapshot.Devices[0].UploadAllowed,"trust grants only the file scope and preserves remote and upload permissions");
                grant.Invoke(window,[new Button{Tag="deny"},new RoutedEventArgs()]);
                Check(scope==ControlScope.Remote&&!snapshot.Devices[0].Allowed,"revoking file trust preserves the existing remote permission");
                Check(window.FindName("AddBrowseRootButton") is Button,"directory actions share a single discoverable menu");
                await (Task)typeof(MainWindow).GetMethod("ReadControlDevicesAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
                var tabletCheck=(CheckBox)window.FindName("ControlTabletCheck");
                tabletCheck.IsChecked=true; tabletCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,tabletCheck));
                Check(scope==(ControlScope.Remote|ControlScope.Tablet)&&tabletCheck.IsChecked==true,"device permissions commit directly from the switch");
                var clipboardCheck=(CheckBox)window.FindName("ControlClipboardCheck");
                failControlSave=true;clipboardCheck.IsChecked=true;
                clipboardCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,clipboardCheck));
                Check(clipboardCheck.IsChecked==false&&(scope&ControlScope.Clipboard)==0&&((TextBlock)window.FindName("FeedbackText")).Text.Contains("保存失败"),"failed device permission write rolls back and shows an error");
                failControlSave=false;pendingControl=new(TaskCreationOptions.RunContinuationsAsynchronously);
                clipboardCheck.IsChecked=true;clipboardCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,clipboardCheck));
                await (Task)typeof(MainWindow).GetMethod("ReadControlDevicesAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
                Check(clipboardCheck.IsChecked==true&&!((ComboBox)window.FindName("ControlDeviceBox")).IsEnabled,"refresh cannot overwrite a pending device permission or switch its target");
                scope|=ControlScope.Clipboard;var controlReply=pendingControl;pendingControl=null;controlReply.SetResult(new RuntimeResponse(true,"saved"));
                while(!((ComboBox)window.FindName("ControlDeviceBox")).IsEnabled)await System.Windows.Threading.Dispatcher.Yield();
                Check(clipboardCheck.IsChecked==true&&controlWrites>=3,"completed asynchronous permission write retains the requested value");
                foreach(int height in new[]{660,860,1000}){
                    window.Height=height;window.UpdateLayout();
                    var scroller=(FrameworkElement)window.FindName("DevicePermissionsScroller");
                    var footer=(FrameworkElement)window.FindName("DevicePermissionsFooter");
                    Check(scroller.TranslatePoint(new Point(0,scroller.ActualHeight),window).Y<=footer.TranslatePoint(new Point(),window).Y+.1,"permission content never overlaps its fixed footer at height "+height);
                }
                window.Height=860;
                var diskRoots=(string[])typeof(MainWindow).GetMethod("ReadyLocalDiskRoots",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
                Check(diskRoots.All(path=>{var drive=new DriveInfo(path);return drive.IsReady&&drive.DriveType is DriveType.Fixed or DriveType.Removable;}),"all-disk candidates contain only ready local fixed/removable drives");
                var roots=(ListBox)window.FindName("BrowseRootList");Check(roots.Items.Count==1,"authorized roots missing");
                snapshot=snapshot with {Settings=new(true,Path.Combine(root,"receive"),true)};
                await (Task)refresh.Invoke(window,null)!;
                Check(roots.Items.Count==1,"legacy receive folder migration disappeared");
                var modes=new List<object>();
                foreach(int theme in new[]{1,2})foreach(int width in new[]{940,1280}) {
                    ((ComboBox)window.FindName("ThemeChoice")).SelectedIndex=theme;window.Width=width;window.UpdateLayout();
                    await CheckDevicePopup(permissionsBox,"fixture-device","示例平板",$"permissions-{theme}-{width}");
                    var card=(FrameworkElement)((FrameworkElement)window.FindName("BrowseRootList")).Parent;
                    card=(FrameworkElement)card.Parent;card.BringIntoView();window.UpdateLayout();
                    foreach(var wrap in Descendants(card).OfType<WrapPanel>())foreach(var button in wrap.Children.OfType<Button>().Where(b=>b.Visibility==Visibility.Visible)) {
                        var position=button.TransformToAncestor(card).Transform(new Point());
                        Check(button.ActualHeight>=40 && position.X>=0 && position.X+button.ActualWidth<=card.ActualWidth+1 && button.Margin.Right>=8,
                            $"file action clipped or spacing missing: {button.Content}; height={button.ActualHeight}, x={position.X}, width={button.ActualWidth}, parent={card.ActualWidth}");
                    }
                    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth),(int)Math.Ceiling(card.ActualHeight),96,96,PixelFormats.Pbgra32);
                    var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()) {
                        drawing.DrawRectangle((Brush)window.FindResource("Surface"),null,new Rect(0,0,card.ActualWidth,card.ActualHeight));
                        drawing.DrawRectangle(new VisualBrush(card),null,new Rect(0,0,card.ActualWidth,card.ActualHeight));
                    }bitmap.Render(visual);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(root,$"files-{theme}-{width}.png")))png.Save(output);
                    modes.Add(new {theme,width,cardWidth=card.ActualWidth,cardHeight=card.ActualHeight});
                }
                File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new {passed=true,checks,modes,fixtureCalls=calls,installedServiceCalls=0}));
            }catch(Exception error){Environment.ExitCode=1;File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new {passed=false,error=error.ToString()}));}
            finally{window.DisposeWindow();app.Shutdown();}
        }));
        window.Show();app.Run();
        void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
        async Task CheckDevicePopup(ComboBox box,string clientId,string label,string filename)
        {
            box.IsDropDownOpen=true;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            box.UpdateLayout();
            var item=box.Items.Cast<object>().First(x=>(string?)x.GetType().GetProperty("ClientId")!.GetValue(x)==clientId);
            var container=(ComboBoxItem?)box.ItemContainerGenerator.ContainerFromItem(item);
            Check(container is not null&&container.ActualHeight>=36&&Descendants(container).OfType<TextBlock>().Any(t=>t.Text==label&&t.ActualWidth>0),"popup shows a sized, readable device item: "+filename);
            var peer=System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(box)!;
            var choices=peer.GetChildren().Select(x=>x.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem))
                .OfType<System.Windows.Automation.Provider.ISelectionItemProvider>().ToArray();
            Check(choices.Length==box.Items.Count,"each device exposes its selection action: "+filename);
            choices[box.Items.IndexOf(item)].Select();
            Check((string?)box.SelectedValue==clientId,"device item is selectable: "+filename);
            var popup=(Popup)box.Template.FindName("PART_Popup",box);
            var child=(FrameworkElement)popup.Child;child.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth),(int)Math.Ceiling(child.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(child);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(root,filename+".png")))encoder.Save(stream);
            box.IsDropDownOpen=false;
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
}
