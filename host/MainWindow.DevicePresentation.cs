using StreamHub.Host.Runtime;
using Brush = System.Windows.Media.Brush;

namespace StreamHub.Host;

public partial class MainWindow
{
    partial void RefreshDevicePresentation()
    {
        DeviceStateLabel.Text = _status is null ? "后台未连接" : "设备在线";
        DeviceStateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _status is null ? "Muted" : "SuccessText");
        RemoteStateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, SessionColorKey(_status?.Remote));
        TabletStateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, SessionColorKey(_status?.Tablet));
        AddressText.ToolTip = AddressText.Text;
    }
    private static string SessionColorKey(EngineStatus? engine) => 
        engine?.DesktopCleanupPending == true ? "WarningText"
        : engine?.Streaming == true ? engine.SessionVerified ? "SuccessText" : "WarningText"
        : "Muted";
}
