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
    private string? _fileTargetClientId;
    private async Task RefreshFilesAsync()
    {
        var response = await _runtime.SendAsync(new("files-status"), _closing.Token);
        if (!response.Success || response.PayloadJson is null)
        {
            _fileSnapshot = null;
            FileStateText.Text = response.Message;
            RenderFilesInlineState();
            RenderFileTaskActions();
            return;
        }
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
        FileStateText.Text = _fileSnapshot.Settings.Enabled ? "" : "文件传输未开启";
        FileDirectoryText.Text = string.IsNullOrEmpty(_fileSnapshot.Settings.ReceiveDirectory) ? "请选择电脑接收文件夹" : _fileSnapshot.Settings.ReceiveDirectory;
        ReceiveDirectoryText.Text = FileDirectoryText.Text;
        OpenReceiveDirectoryButton.IsEnabled = !string.IsNullOrEmpty(_fileSnapshot.Settings.ReceiveDirectory);
        FileOpenDirectoryButton.IsEnabled = OpenReceiveDirectoryButton.IsEnabled;
        FileOpenDirectoryButton.ToolTip = FileOpenDirectoryButton.IsEnabled ? "打开接收目录" : "请在设备权限中选择接收目录";
        var selected = _fileTargetClientId ?? FileDeviceBox.SelectedValue as string;
        _applyingFileSnapshot = true;
        try {
            FileDeviceBox.ItemsSource = _fileSnapshot.Devices.Select(x => new { x.ClientId, Label = x.Name }).ToArray();
            FileDeviceBox.SelectedValue = selected;
            if (selected is null && FileDeviceBox.SelectedIndex < 0 && FileDeviceBox.Items.Count > 0) FileDeviceBox.SelectedIndex = 0;
        } finally { _applyingFileSnapshot = false; }
        RenderFilePermissions();
        string? task = FileTaskList.SelectedValue as string;
        FileTaskList.ItemsSource = _fileSnapshot.Items.Select(x => new FileTaskRow(x.Id, x.Name, FileSizeText(x.Size),
            x.Direction == "upload" ? "电脑接收" : "电脑发送", FilePercent(x), FileState(x.State), CanCancelFile(x))).ToArray();
        FileTaskList.SelectedValue = task;
        RenderFileTaskActions();
        RenderFilesInlineState();
        RenderUnifiedDevices();
    }
    private sealed record FileTaskRow(string Id, string Name, string Size, string Direction, int Percent, string StateText, bool CanCancel);
    private static bool CanCancelFile(TransferItem item) => item.State is "receiving" or "staging" or "ready" or "publishing" or "copying";
    private static int FilePercent(TransferItem item) => item.State == "completed" ? 100 : item.Size <= 0 ? 0
        : (int)Math.Clamp((decimal)item.Offset * 100 / item.Size, 0, 100);
    private static string FileSizeText(long size)
    {
        double value = Math.Max(0, size);
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.#") + " " + units[unit];
    }
    private static string FileState(string state) => state switch { "receiving" => "接收中/可续传", "staging" => "正在暂存", "ready" => "待平板接收",
        "publishing" or "copying" => "正在校验保存", "completed" => "已完成", "cancelled" => "已取消", "failed" => "失败", _ => "未完成" };
    private void RefreshFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(RefreshFilesAsync);
    private void EnableFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        var saved = _fileSnapshot?.Settings.ReceiveDirectory;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var suggested = string.IsNullOrWhiteSpace(saved) ? Path.Combine(downloads, "我控制") : saved;
        var picker = new OpenFolderDialog { Title = "选择电脑接收文件夹", DefaultDirectory = downloads,
            FolderName = suggested, InitialDirectory = Directory.Exists(suggested) ? suggested : downloads };
        if (picker.ShowDialog(this) != true) return;
        if (System.Windows.MessageBox.Show(this, "将电脑接收目录设为：\n" + picker.FolderName + "\n\n此操作不新增浏览目录，也不授予删除权限。", "修改接收目录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await CommandAsync(new("files-configure", FilePath: picker.FolderName, TransferEnabled: true)); await RefreshFilesAsync();
    });
    private void OpenReceiveDirectory_Click(object sender, RoutedEventArgs args) => _ = RunAction(async () =>
    {
        await RefreshFilesAsync();
        string? path = _fileSnapshot?.Settings.ReceiveDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        { ShowFeedback("接收文件夹不存在，请在电脑设置重新选择。", error: true); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = path, UseShellExecute = true, Verb = "open" });
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
        if (FileDeviceBox.SelectedValue is not string clientId) { ShowFeedback("请先从平板“文件”页连接电脑。", error: true); return; }
        if (_fileSnapshot?.Devices.FirstOrDefault(x => x.ClientId == clientId)?.UsesDevicePermissions == true)
        {
            await ReadControlDevicesAsync();
            var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.ClientId == clientId && !d.Revoked);
            if (device is not null) { OpenDevicePermissions_Click(new System.Windows.Controls.Button { Tag = device.Id }, e); return; }
            ShowFeedback("设备绑定已撤销，请重新添加电脑。", error: true); return;
        }
        if ((sender as System.Windows.Controls.Button)?.Tag as string == "allow" && System.Windows.MessageBox.Show(this,"允许所选设备收发文件？请核对设备名称与核对码。删除权限仍默认关闭。","允许文件设备",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        await CommandAsync(new("files-grant", ClientId: clientId, TransferEnabled: (sender as System.Windows.Controls.Button)?.Tag as string == "allow")); await RefreshFilesAsync();
    });
    private void SendFile_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        if (FileDeviceBox.SelectedValue is not string clientId || _fileSnapshot?.Settings.Enabled != true ||
            _fileSnapshot.Devices.FirstOrDefault(x => x.ClientId == clientId)?.Allowed != true)
        { ShowFeedback("请在设备权限中开启文件访问。", error: true); return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择发送到平板的文件", Multiselect = false, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await CommandAsync(new("files-stage", ClientId: clientId, FilePath: picker.FileName)); await RefreshFilesAsync();
    });
    private void RemoveFileDevice_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { if (FileDeviceBox.SelectedValue is string clientId) await CommandAsync(new("files-remove", ClientId: clientId)); await RefreshFilesAsync(); });
    private void CancelFile_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        if (FileTaskList.SelectedValue is not string id || _fileSnapshot?.Items.FirstOrDefault(x => x.Id == id) is not { } task || !CanCancelFile(task)) return;
        await CommandAsync(new("files-cancel", RequestId: id)); await RefreshFilesAsync();
    });
    private void FileTask_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RenderFileTaskActions();
    private void RenderFileTaskActions()
    {
        if (CancelFileButton is null) return;
        var task = _fileSnapshot?.Items.FirstOrDefault(x => x.Id == FileTaskList.SelectedValue as string);
        CancelFileButton.IsEnabled = task is not null && CanCancelFile(task);
        CancelFileButton.ToolTip = task is null ? "请选择进行中的任务" : CanCancelFile(task) ? "取消所选任务，保留源文件" : "该任务已结束";
    }
    private void GrantSelectedFileDevice_Click(object sender, RoutedEventArgs e) => GrantFiles_Click(new System.Windows.Controls.Button { Tag = "allow" }, e);
    private void ClearFileHistory_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { await CommandAsync(new("files-clear")); await RefreshFilesAsync(); });
    private void FileDevice_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingFileSnapshot) return;
        if (FileDeviceBox.SelectedValue is string selected) _fileTargetClientId = selected;
        _filePermissionsDirty = false;
        RenderFilePermissions();
        RenderFilesInlineState();
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
        FileGrantButton.Content = device?.UsesDevicePermissions == true ? "设备文件权限" : "允许所选设备";
        FileRevokeButton.Visibility = FileRemoveButton.Visibility = device?.UsesDevicePermissions == true ? Visibility.Collapsed : Visibility.Visible;
    }
    private void FilePermission_Changed(object sender,RoutedEventArgs e)
    { _filePermissionsDirty=true;FilePermissionStatus.Text="权限尚未保存"; }
    private void AddBrowseRoot_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        var picker=new OpenFolderDialog{Title="选择允许平板浏览的文件夹或磁盘"};
        if(picker.ShowDialog(this)!=true)return;
        if (System.Windows.MessageBox.Show(this,"允许已授权设备浏览和下载以下目录？\n"+picker.FolderName+"\n删除权限仍单独控制。","新增浏览目录",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        await CommandAsync(new("files-root-add",FilePath:picker.FolderName));await RefreshFilesAsync();
    });
    private void AddCommonBrowseRoots_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        string[] paths=[Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")];
        if (System.Windows.MessageBox.Show(this,"允许已授权设备浏览并下载桌面和下载目录中的文件？删除权限仍单独控制。","新增浏览目录",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
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
        ShowFeedback(result.Message, error: !result.Success);
        if(!result.Success)return;
        _filePermissionsDirty=false;
        await RefreshFilesAsync();
    });

}
