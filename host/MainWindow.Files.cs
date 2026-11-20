using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private TransferSnapshot? _fileSnapshot;
    private bool _applyingFileSnapshot, _savingFilePermissions;
    private int _filePermissionVersion;
    private string? _fileTargetClientId;
    private sealed record FileDeviceChoice(string ClientId, string Label);
    private async Task RefreshFilesAsync()
    {
        var permissionVersion = _filePermissionVersion;
        var response = await _runtime.SendAsync(new("files-status"), _closing.Token);
        if (permissionVersion != _filePermissionVersion || _savingFilePermissions) return;
        if (!response.Success || response.PayloadJson is null)
        {
            _fileSnapshot = null;
            RenderFilePermissions();
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
        FileOpenDirectoryButton.IsEnabled = true;
        var selected = _fileTargetClientId ?? FileDeviceBox.SelectedValue as string;
        var devices = _fileSnapshot.Devices.Select(x => new FileDeviceChoice(x.ClientId, x.Name)).ToArray();
        selected ??= devices.FirstOrDefault()?.ClientId;
        _applyingFileSnapshot = true;
        try {
            // Keep an open device menu intact when the periodic snapshot is unchanged.
            if (FileDeviceBox.ItemsSource is not FileDeviceChoice[] current || !current.SequenceEqual(devices))
            {
                FileDeviceBox.ItemsSource = devices;
                SettingsFileDeviceBox.ItemsSource = devices;
            }
            FileDeviceBox.SelectedValue = selected;
            SettingsFileDeviceBox.SelectedValue = selected;
            FileDeviceBox.IsEnabled = SettingsFileDeviceBox.IsEnabled = devices.Length > 0;
            _fileTargetClientId = selected;
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
    private void EnableFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(() => ChooseReceiveDirectoryAsync(enableFiles: true));
    private void ChooseReceiveDirectory_Click(object sender, RoutedEventArgs e) => _ = RunAction(() => ChooseReceiveDirectoryAsync());
    private async Task ChooseReceiveDirectoryAsync(bool enableFiles = false)
    {
        if (_fileSnapshot is null) await RefreshFilesAsync();
        if (_fileSnapshot is null) return;
        var saved = _fileSnapshot?.Settings.ReceiveDirectory;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var suggested = string.IsNullOrWhiteSpace(saved) ? Path.Combine(downloads, "我控制") : saved;
        var picker = new OpenFolderDialog { Title = "选择电脑接收文件夹", DefaultDirectory = downloads,
            FolderName = suggested, InitialDirectory = Directory.Exists(suggested) ? suggested : downloads };
        if (picker.ShowDialog(this) != true) return;
        if (System.Windows.MessageBox.Show(this, "将电脑接收目录设为：\n" + picker.FolderName + "\n\n此操作不新增浏览目录，也不授予删除权限。", "修改接收目录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await CommandAsync(new("files-configure", FilePath: picker.FolderName, TransferEnabled: enableFiles || _fileSnapshot?.Settings.Enabled == true));
        await RefreshFilesAsync();
    }
    private void OpenReceiveDirectory_Click(object sender, RoutedEventArgs args) => _ = RunAction(async () =>
    {
        await RefreshFilesAsync();
        string? path = _fileSnapshot?.Settings.ReceiveDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        { ShowFeedback("接收文件夹不存在，请在电脑设置重新选择。", error: true); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = path, UseShellExecute = true, Verb = "open" });
    });
    private void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { BrowseFilesCheck.IsChecked = _fileSnapshot?.Settings.BrowseEnabled == true; return; }
        _ = RunAction(async () =>
        {
            if (_fileSnapshot is null || !_fileSnapshot.Settings.Enabled) return;
            bool previous = _fileSnapshot.Settings.BrowseEnabled;
            bool enabled = BrowseFilesCheck.IsChecked == true;
            if (enabled && System.Windows.MessageBox.Show(this,
                    "允许已信任的文件设备浏览和下载已授权目录？上传和删除权限不变。",
                    "开启共享文件浏览？", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { BrowseFilesCheck.IsChecked = previous; return; }
            try
            {
                var result = await _runtime.SendAsync(new("files-configure", TransferEnabled: true,
                    FilePath: _fileSnapshot.Settings.ReceiveDirectory, BrowseEnabled: enabled), _closing.Token);
                ShowFeedback(result.Message, error: !result.Success);
                if (!result.Success) { BrowseFilesCheck.IsChecked = previous; return; }
                await RefreshFilesAsync();
            }
            catch { BrowseFilesCheck.IsChecked = previous; throw; }
        });
    }
    private void DisableFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { await CommandAsync(new("files-configure", TransferEnabled: false)); await RefreshFilesAsync(); });
    private void GrantFiles_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        if (FileDeviceBox.SelectedValue is not string clientId) { ShowFeedback("请先从平板“文件”页连接电脑。", error: true); return; }
        bool allow = (sender as System.Windows.Controls.Button)?.Tag as string == "allow";
        if (_fileSnapshot?.Devices.FirstOrDefault(x => x.ClientId == clientId)?.UsesDevicePermissions == true)
        {
            await ReadControlDevicesAsync();
            var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.ClientId == clientId && !d.Revoked);
            if (device is null) { ShowFeedback("设备绑定已撤销，请重新添加电脑。", error: true); return; }
            var scope = allow ? device.Scope | ControlScope.Files : device.Scope & ~ControlScope.Files;
            var result = await _runtime.SendAsync(new("control-device-scope", RequestId: device.Id, DeviceScope: (int)scope), _closing.Token);
            ShowFeedback(result.Message, error: !result.Success);
            await ReadControlDevicesAsync();
            await RefreshFilesAsync();
            return;
        }
        if (allow && System.Windows.MessageBox.Show(this,"信任所选设备访问文件？请核对设备名称与核对码。上传和删除权限单独控制。","信任文件设备",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        await CommandAsync(new("files-grant", ClientId: clientId, TransferEnabled: allow)); await RefreshFilesAsync();
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
    }
    private void GrantSelectedFileDevice_Click(object sender, RoutedEventArgs e) => GrantFiles_Click(new System.Windows.Controls.Button { Tag = "allow" }, e);
    private void ClearFileHistory_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    { await CommandAsync(new("files-clear")); await RefreshFilesAsync(); });
    private void FileDevice_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_applyingFileSnapshot) return;
        var selected = ((System.Windows.Controls.ComboBox)sender).SelectedValue as string;
        _applyingFileSnapshot = true;
        try
        {
            _fileTargetClientId = selected;
            FileDeviceBox.SelectedValue = selected;
            SettingsFileDeviceBox.SelectedValue = selected;
        }
        finally { _applyingFileSnapshot = false; }
        RenderFilePermissions();
        RenderFilesInlineState();
    }
    private void RenderFilePermissions()
    {
        if (_savingFilePermissions) return;
        var device=_fileSnapshot?.Devices.FirstOrDefault(x=>x.ClientId==FileDeviceBox.SelectedValue as string);
        if(FileUploadAllowed is null || FileDeleteAllowed is null)return;
        FileUploadAllowed.IsChecked=device?.UploadAllowed==true;FileDeleteAllowed.IsChecked=device?.DeleteAllowed==true;
        FileUploadAllowed.IsEnabled=FileDeleteAllowed.IsEnabled=device is not null;
        FilePermissionStatus.Text=device is null ? "请先选择设备" : "权限已保存";
        FileGrantButton.Content = "信任此设备";
        FileGrantButton.IsEnabled = device is { Allowed: false };
        FileGrantButton.Visibility = device?.Allowed == true ? Visibility.Collapsed : Visibility.Visible;
        FileRevokeButton.IsEnabled = device?.Allowed == true;
        FileRevokeButton.Visibility = device?.Allowed == true ? Visibility.Visible : Visibility.Collapsed;
        FileRemoveButton.Visibility = device?.UsesDevicePermissions == true ? Visibility.Collapsed : Visibility.Visible;
        FileTrustStatus.Text = device is null ? "请先选择设备" : device.Allowed ? "已信任文件访问，授权目录内无需逐次确认" : "尚未信任文件访问";
    }
    private void FilePermission_Changed(object sender,RoutedEventArgs e)
    {
        if (_savingFilePermissions || _applyingFileSnapshot) return;
        var device = _fileSnapshot?.Devices.FirstOrDefault(x => x.ClientId == FileDeviceBox.SelectedValue as string);
        if (_busy || device is null) { RenderFilePermissions(); return; }
        bool upload = FileUploadAllowed.IsChecked == true, delete = FileDeleteAllowed.IsChecked == true;
        _ = RunAction(() => SaveFilePermissionsAsync(device, upload, delete));
    }
    private void AddBrowseRoot_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        var picker=new OpenFolderDialog{Title="选择允许平板浏览的文件夹或磁盘"};
        if(picker.ShowDialog(this)!=true)return;
        if (System.Windows.MessageBox.Show(this,"允许已授权设备浏览和下载以下目录？\n"+picker.FolderName+"\n删除权限仍单独控制。","新增浏览目录",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        await CommandAsync(new("files-root-add",FilePath:picker.FolderName));await RefreshFilesAsync();
    });
    private void OpenBrowseRootMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { Style = (Style)FindResource("DesktopMenu"), PlacementTarget = (UIElement)sender };
        foreach (var (label, action) in new (string, RoutedEventHandler)[] {
            ("选择文件夹或磁盘", AddBrowseRoot_Click), ("桌面与下载", AddCommonBrowseRoots_Click), ("全部磁盘", AddAllBrowseRoots_Click) })
        {
            var item = new System.Windows.Controls.MenuItem { Header = label, Style = (Style)FindResource("DesktopMenuItem") };
            item.Click += action; menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void AddCommonBrowseRoots_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        string[] paths=[Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")];
        if (System.Windows.MessageBox.Show(this,"允许已授权设备浏览并下载桌面和下载目录中的文件？删除权限仍单独控制。","新增浏览目录",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        foreach(var path in paths.Where(Directory.Exists))await CommandAsync(new("files-root-add",FilePath:path));
        await RefreshFilesAsync();
    });
    private void AddAllBrowseRoots_Click(object sender, RoutedEventArgs e) => _ = RunAction(async () =>
    {
        var settings = _fileSnapshot?.Settings;
        var existingPaths = settings?.BrowseRoots?.Select(r => r.Path) ?? [settings?.ReceiveDirectory ?? ""];
        var existing = existingPaths.Select(Path.TrimEndingDirectorySeparator)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = ReadyLocalDiskRoots().Where(path => !existing.Contains(Path.TrimEndingDirectorySeparator(path))).ToArray();
        if (roots.Length == 0) { ShowFeedback("没有待添加的本地磁盘。"); return; }
        if (System.Windows.MessageBox.Show(this, "允许已信任的文件设备浏览和下载以下磁盘？\n\n" +
                string.Join("\n", roots) + "\n\n上传和删除权限不变。", "添加全部磁盘",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var path in roots)
            {
                var result = await _runtime.SendAsync(new("files-root-add", FilePath: path), _closing.Token);
                if (!result.Success) { ShowFeedback(result.Message, error: true); return; }
            }
            ShowFeedback("本地磁盘已添加，可分别撤销目录授权。");
        }
        finally { await RefreshFilesAsync(); }
    });
    private static string[] ReadyLocalDiskRoots()
    {
        var paths = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                    paths.Add(drive.RootDirectory.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private void RemoveBrowseRoot_Click(object sender,RoutedEventArgs e)=>_=RunAction(async()=>{
        if(BrowseRootList.SelectedValue is string id)await CommandAsync(new("files-root-remove",BrowseRootId:id));
        await RefreshFilesAsync();
    });
    private async Task SaveFilePermissionsAsync(TransferDevice device, bool upload, bool delete)
    {
        if (upload == device.UploadAllowed && delete == device.DeleteAllowed) return;
        if (delete && !device.DeleteAllowed && System.Windows.MessageBox.Show(this,
                "允许此设备将授权目录中的电脑文件移到回收站？", "文件删除权限",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { RenderFilePermissions(); return; }
        _savingFilePermissions = true;
        _filePermissionVersion++;
        FileUploadAllowed.IsEnabled = FileDeleteAllowed.IsEnabled = false;
        FilePermissionStatus.Text = "正在保存权限…";
        bool saved = false;
        try
        {
            var result = await _runtime.SendAsync(new("files-permissions", ClientId: device.ClientId,
                UploadAllowed: upload, DeleteAllowed: delete), _closing.Token);
            ShowFeedback(result.Message, error: !result.Success);
            if (!result.Success) return;
            saved = true;
            if (_fileSnapshot is not null)
                _fileSnapshot = _fileSnapshot with { Devices = _fileSnapshot.Devices.Select(x => x.ClientId == device.ClientId
                    ? x with { UploadAllowed = upload, DeleteAllowed = delete } : x).ToArray() };
        }
        finally
        {
            _savingFilePermissions = false;
            _filePermissionVersion++;
            RenderFilePermissions();
            if (!saved) FilePermissionStatus.Text = "保存失败，已恢复原权限，请重试";
        }
    }

}
