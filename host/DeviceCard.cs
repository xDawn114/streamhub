using System.Windows;

namespace StreamHub.Host;

/// <summary>Shared presentation contract for bound devices and compatible receivers.</summary>
internal sealed record DeviceCard(
    string Id, string ClientId, string Name, string TypeText, string Icon,
    bool IndividuallyBound, bool Revoked, bool Online, string OnlineText, string LastContact,
    bool CanConnect, string ConnectReason, bool CanOpenFiles, string FileReason,
    string? RequestId = null, string RequestState = "", string RequestMessage = "")
{
    public string? CredentialId => IndividuallyBound ? Id : null;
    public string ShortId => Id.Length > 6 ? Id[^6..] : Id;
    public string ConnectLabel => RequestId is null ? "连接副屏" : RequestState == "connected" ? "断开副屏" : "取消邀请";
    public Visibility ActionVisibility => Revoked ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ManagementVisibility => IndividuallyBound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BindingVisibility => !IndividuallyBound && !Revoked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryActionVisibility => Revoked ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RequestVisibility => string.IsNullOrWhiteSpace(RequestMessage) ? Visibility.Collapsed : Visibility.Visible;
    public bool RequestFailed => RequestState is "failed" or "rejected" or "expired";
}
