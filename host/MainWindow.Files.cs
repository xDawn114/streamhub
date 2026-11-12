using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private TransferSnapshot? _fileSnapshot;
    private bool _applyingFileSnapshot, _filePermissionsDirty;
    private string? _filePermissionsDevice;
    private async Task RefreshFilesAsync()
    {
        var response = await _runtime.SendAsync(new("files-status"), _closing.Token);
        if (!response.Success || response.PayloadJson is null) { FileStateText.Text = response.Message; return; }
        _fileSnapshot = JsonSerializer.Deserialize<TransferSnapshot>(response.PayloadJson, RuntimeProtocol.JsonOptions);
        if (_fileSnapshot is null) return;
        var selectedRoot = BrowseRootList.SelectedValue as string;
        var roots = _fileSnapshot.Settings.BrowseRoots ??
            (string.IsNullOrEmpty(_fileSnapshot.Settings.ReceiveDirectory) ? [] :
                new BrowseRoot[] { new("legacy", "接收文件夹", _fileSnapshot.Settings.ReceiveDirectory, "") });
        BrowseRootList.ItemsSource = roots.Select(x => new { x.Id, Label = x.Name + " · " + x.Path }).ToArray();
        BrowseRootList.SelectedValue = selectedRoot;
        BrowseFilesCheck.IsChecked = _fileSnapshot.Settings.BrowseEnabled;
        BrowseFilesCheck.IsEnabled = _fileSnapshot.Settings.Enabled;
        FileStateText.Text = _fileSnapshot.Settings.Enabled ? "文件传输已开启 · 仅已允许设备可收发" : "文件传输已关闭";
        FileDirectoryText.Text = string.IsNullOrEmpty(_fileSnapshot.Settings.ReceiveDirectory) ? "请选择电脑接收文件夹" : _fileSnapshot.Settings.ReceiveDirectory;
        var selected = FileDeviceBox.SelectedValue as string;
        _applyingFileSnapshot = true;
        try {
            FileDeviceBox.ItemsSource = _fileSnapshot.Devices.Select(x => new { x.ClientId, Label = x.Name + " · 核对码 " + x.KeyHash[..8] + (x.Allowed ? " · 已允许" : " · 待允许") }).ToArray();
            FileDeviceBox.SelectedValue = selected;
            if (FileDeviceBox.SelectedIndex < 0 && FileDeviceBox.Items.Count > 0) FileDeviceBox.SelectedIndex = 0;
        } finally { _applyingFileSnapshot = false; }
        RenderFilePermissions();
        string? task = FileTaskList.SelectedValue as string;
        FileTaskList.ItemsSource = _fileSnapshot.Items.Select(x => new { x.Id, Label = x.Name + " · " + FileState(x.State) + " · " +
            (x.Direction == "upload" ? "收到 " : "已发送块 ") + (x.Size == 0 ? "0 B" : $"{x.Offset * 100 / x.Size}%") }).ToArray();
        FileTaskList.SelectedValue = task;
    }
    private static string FileState(string state) => state switch { "receiving" => "接收中/可续传", "staging" => "正在暂存", "ready" => "待平板接收",
        "publishing" or "copying" => "正在校验保存", "completed" => "已完成", "cancelled" => "已取消", _ => "未完成" };
    private void RefreshFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(RefreshFilesAsync);
    private void EnableFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        var picker = new OpenFolderDialog { Title = "选择电脑接收文件夹" };
        if (picker.ShowDialog(this) != true) return;
        await CommandAsync(new("files-configure", FilePath: picker.FolderName, TransferEnabled: true)); await RefreshFilesAsync();
    });
    private void BrowseFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        if (_fileSnapshot is null || !_fileSnapshot.Settings.Enabled) return;
        if (BrowseFilesCheck.IsChecked == true && System.Windows.MessageBox.Show(this,
                "已允许的设备将能浏览和下载授权目录，无需启动远控；删除权限单独控制。\n\n" + _fileSnapshot.Settings.ReceiveDirectory,
                "开启共享文件浏览？", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { BrowseFilesCheck.IsChecked = false; return; }
        await CommandAsync(new("files-configure", TransferEnabled: true,
            FilePath: _fileSnapshot.Settings.ReceiveDirectory, BrowseEnabled: BrowseFilesCheck.IsChecked == true));
        await RefreshFilesAsync();
    });
    private void DisableFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { await CommandAsync(new("files-configure", TransferEnabled: false)); await RefreshFilesAsync(); });
    private void GrantFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        if (FileDeviceBox.SelectedValue is not string clientId) { FeedbackText.Text = "请先在平板文件页面申请授权。"; return; }
        await CommandAsync(new("files-grant", ClientId: clientId, TransferEnabled: (sender as System.Windows.Controls.Button)?.Tag as string == "allow")); await RefreshFilesAsync();
    });
    private void SendFile_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        if (FileDeviceBox.SelectedValue is not string clientId) { FeedbackText.Text = "请选择已允许的设备。"; return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择发送到平板的文件", Multiselect = false, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await CommandAsync(new("files-stage", ClientId: clientId, FilePath: picker.FileName)); await RefreshFilesAsync();
    });
    private void RemoveFileDevice_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { if (FileDeviceBox.SelectedValue is string clientId) await CommandAsync(new("files-remove", ClientId: clientId)); await RefreshFilesAsync(); });
    private void CancelFile_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { if (FileTaskList.SelectedValue is string id) await CommandAsync(new("files-cancel", RequestId: id)); await RefreshFilesAsync(); });
    private void ClearFileHistory_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { await CommandAsync(new("files-clear")); await RefreshFilesAsync(); });
    private void FileDevice_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingFileSnapshot) return;
        _filePermissionsDirty = false;
        RenderFilePermissions();
    }
    private void RenderFilePermissions()
    {
        if (_filePermissionsDirty && _filePermissionsDevice == FileDeviceBox.SelectedValue as string) return;
        var device=_fileSnapshot?.Devices.FirstOrDefault(x=>x.ClientId==FileDeviceBox.SelectedValue as string);
        if(FileUploadAllowed is null || FileDeleteAllowed is null)return;
        _filePermissionsDevice = device?.ClientId;
        _filePermissionsDirty = false;
        FileUploadAllowed.IsChecked=device?.UploadAllowed==true;FileDeleteAllowed.IsChecked=device?.DeleteAllowed==true;
        FileUploadAllowed.IsEnabled=FileDeleteAllowed.IsEnabled=device is not null;
        FilePermissionStatus.Text=device is null ? "请先选择设备" : "权限已保存";
    }
    private void FilePermission_Changed(object sender,RoutedEventArgs e)
    { _filePermissionsDirty=true;FilePermissionStatus.Text="权限尚未保存"; }
    private void AddBrowseRoot_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        var picker=new OpenFolderDialog{Title="选择允许平板浏览的文件夹或磁盘"};
        if(picker.ShowDialog(this)!=true)return;
        await CommandAsync(new("files-root-add",FilePath:picker.FolderName));await RefreshFilesAsync();
    });
    private void AddCommonBrowseRoots_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        string[] paths=[Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")];
        foreach(var path in paths.Where(Directory.Exists))await CommandAsync(new("files-root-add",FilePath:path));
        await RefreshFilesAsync();
    });
    private void RemoveBrowseRoot_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        if(BrowseRootList.SelectedValue is string id)await CommandAsync(new("files-root-remove",BrowseRootId:id));
        await RefreshFilesAsync();
    });
    private void SaveFilePermissions_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        if(FileDeviceBox.SelectedValue is not string id)return;
        if(FileDeleteAllowed.IsChecked==true && System.Windows.MessageBox.Show(this,"允许此设备删除授权目录中的电脑文件？仅支持移入回收站。","文件删除权限",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        var result=await _runtime.SendAsync(new("files-permissions",ClientId:id,UploadAllowed:FileUploadAllowed.IsChecked==true,DeleteAllowed:FileDeleteAllowed.IsChecked==true),_closing.Token);
        FeedbackText.Text=result.Message;
        if(!result.Success)return;
        _filePermissionsDirty=false;
        await RefreshFilesAsync();
    });

}
