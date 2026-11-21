using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;

namespace StreamHub.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = InstallOptions.Parse(args);
        try { return Run(options, args); }
        catch (Exception ex) { Deployment.WriteFailure(options, ex); return 1; }
    }

    private static int Run(InstallOptions options, string[] args)
    {
        if (options.VerifyNoPayload) return System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("StreamHub.Payload.zip") ? 3 : 0;
        if (options.CleanupParent > 0) return Deployment.Cleanup(options);
        ApplicationConfiguration.Initialize();
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("安装或卸载需要管理员权限，请允许 Windows 的权限确认。", "我控制", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 5;
        }
        if (!options.VerifyOnly)
            options.InstallRoot = InstallationLocation.Read().SelectRoot(options.InstallRoot, options.InstallRootExplicit);
        if (options.Rollback && Deployment.LaunchRollbackWorker(options, args)) return 0;
        var trace = new Deployment(options, _ => { });
        if(options.WaitParent>0)
        {
            try { using var parent=Process.GetProcessById(options.WaitParent); if(!parent.WaitForExit(10000))return 17; }
            catch(ArgumentException) { }
        }
        using var transaction = InstallTransactionLock.TryAcquire(@"Global\StreamHub.InstallTransaction");
        if (transaction is null)
        {
            if (!options.Quiet) MessageBox.Show("另一项安装或恢复正在进行，请等待它完成。", "我控制");
            return 16;
        }
        options.OwnsTransaction = true;
        // Flush entry timing only after acquiring the lock. ProtectData changes
        // ACLs/owner metadata and must never race an active installation.
        trace.Stage("entry");
        trace.Stage("locked");
        if (options.Quiet)
        {
            try
            {
                var deployment = new Deployment(options, _ => { });
                if (options.VerifyOnly) deployment.VerifyPackage();
                else if (options.Rollback) deployment.Rollback();
                else if (options.Uninstall) deployment.Uninstall(); else deployment.Install();
                deployment.Stage("complete", 0);
                return 0;
            }
            catch (Exception ex)
            {
                Deployment.WriteFailure(options, ex);
                return 1;
            }
        }
        Application.Run(new InstallWindow(options));
        return 0;
    }
}

internal sealed class InstallWindow : Form
{
    private readonly InstallOptions _options;
    private readonly TextBox _directory;
    private readonly TextBox _log;
    private readonly CheckBox _startup;
    private readonly CheckBox _driver;
    private readonly Button _execute;
    private readonly Button _browse;
    private readonly Label _locationStatus;
    private readonly LinkLabel _useExisting;
    private readonly Label _title;
    private InstallationLocation _existing;
    private bool _validDestination;
    private bool _showUseExisting;
    private bool _busy;
    private bool _completed;

    public InstallWindow(InstallOptions options)
    {
        _options = options;
        _existing = InstallationLocation.Read();
        options.InstallRoot = _existing.SelectRoot(options.InstallRoot, options.InstallRootExplicit);
        Text = options.Rollback ? "恢复我控制上个版本" : options.Uninstall ? "卸载我控制" : "我控制 · 电脑端安装";
        ClientSize = new Size(650, 580);
        MinimumSize = new Size(666, 619);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(247, 249, 252);
        _title = new Label { Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true, Location = new Point(28, 24) };
        var intro = new Label { Text = options.Rollback ? "设备绑定和个人设置保留。" : options.Uninstall ? "移除程序，保留设备绑定和设置。" : "选择安装位置。更新或修复会保留设备绑定和设置。", AutoSize = false, Size = new Size(592, 28), Location = new Point(30, 76), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var locationLabel = new Label { Text = "安装位置", AutoSize = true, Location = new Point(30, 112) };
        _directory = new TextBox { Text = options.InstallRoot, Location = new Point(30, 140), Size = new Size(482, 30), ReadOnly = options.Uninstall || options.Rollback, AccessibleName = "安装位置", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _browse = new Button { Text = "选择位置", Location = new Point(524, 138), Size = new Size(98, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = "选择我控制的安装文件夹", SelectedPath = _directory.Text }; if (picker.ShowDialog(this) == DialogResult.OK) _directory.Text = picker.SelectedPath; };
        _locationStatus = new Label { AutoSize = true, MaximumSize = new Size(592, 0), Location = new Point(30, 182), AccessibleName = "安装位置状态" };
        _useExisting = new LinkLabel { Text = "使用原位置修复", AutoSize = true, Location = new Point(30, 213), LinkColor = Color.FromArgb(47, 89, 214), ActiveLinkColor = Color.FromArgb(35, 67, 170) };
        _useExisting.LinkClicked += (_, _) => UseExistingLocation();
        _startup = new CheckBox { Text = "登录 Windows 后显示后台托盘", Checked = options.AutoStart, AutoSize = true, Location = new Point(30, 253), Visible = !options.Uninstall && !options.Rollback };
        _driver = new CheckBox { Text = "安装副屏驱动（已安装时保留现有驱动）", Checked = options.InstallDriver, AutoSize = true, Location = new Point(30, 286), Visible = !options.Uninstall && !options.Rollback };
        _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Location = new Point(30, 329), Size = new Size(592, 172), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "安装进度" };
        _execute = new Button { Size = new Size(145, 39), Location = new Point(477, 520), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, BackColor = Color.FromArgb(47, 89, 214), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        _execute.FlatAppearance.BorderSize = 0;
        _execute.Click += async (_, _) => await ExecuteAsync();
        Controls.AddRange([_title, intro, locationLabel, _directory, _browse, _locationStatus, _useExisting, _startup, _driver, _log, _execute]);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        AcceptButton = _execute;
        _directory.TextChanged += (_, _) => RefreshDestination();
        ClientSizeChanged += (_, _) => { _locationStatus.MaximumSize = new Size(_log.Width, 0); LayoutOptions(); };
        RefreshDestination();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private void UseExistingLocation()
    {
        if (_existing.CanRepair) _directory.Text = _existing.Root!;
    }

    private void RefreshDestination()
    {
        _validDestination = true;
        _showUseExisting = false;
        _locationStatus.ForeColor = Color.FromArgb(87, 97, 112);
        var repair = _existing.CanRepair && InstallationLocation.Same(_directory.Text, _existing.Root!);
        _title.Text = _options.Rollback ? "恢复上个版本" : _options.Uninstall ? "卸载我控制" : _existing.CanRepair ? "更新或修复我控制" : "安装我控制";
        if (!_completed) _execute.Text = _options.Rollback ? "恢复上个版本" : _options.Uninstall ? "开始卸载" : _existing.CanRepair ? "更新 / 修复" : "开始安装";
        try
        {
            var root = Deployment.ValidateRoot(_directory.Text);
            if (!_options.Uninstall && !_options.Rollback)
            {
                if (root.Any(c => c > 127)) throw new IOException("请选择英文安装路径。");
                _existing.ValidateDestination(root);
            }
            _locationStatus.Text = repair
                ? _existing.Incomplete ? "将补齐缺失的程序文件，保留账号、绑定和设置。" : "已找到现有安装，将在此位置更新。"
                : "";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            _validDestination = false;
            _locationStatus.Text = ex.Message;
            _locationStatus.ForeColor = Color.FromArgb(170, 45, 45);
            _showUseExisting = _existing.CanRepair && !repair;
        }
        _useExisting.Visible = _showUseExisting;
        LayoutOptions();
        UpdateControls();
    }

    private void LayoutOptions()
    {
        int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        _useExisting.Top = _locationStatus.Bottom + Scale(8);
        _startup.Top = Math.Max(Scale(253), (_showUseExisting ? _useExisting.Bottom : _locationStatus.Bottom) + Scale(20));
        _driver.Top = _startup.Bottom + Scale(8);
        var top = _driver.Bottom + Scale(20);
        MinimumSize = new Size(MinimumSize.Width, Math.Max(Scale(580), top + Scale(220)) + Height - ClientSize.Height);
        _log.SetBounds(_log.Left, top, _log.Width, Math.Max(Scale(140), _execute.Top - Scale(20) - top));
    }

    private void UpdateControls()
    {
        _directory.Enabled = !_busy && !_completed;
        _browse.Enabled = !_busy && !_completed && !_options.Uninstall && !_options.Rollback;
        _useExisting.Enabled = !_busy && !_completed;
        _startup.Enabled = _driver.Enabled = !_busy && !_completed;
        _execute.Enabled = !_busy && (_completed || _validDestination);
        _execute.BackColor = _execute.Enabled ? Color.FromArgb(47, 89, 214) : Color.FromArgb(226, 231, 238);
        _execute.ForeColor = _execute.Enabled ? Color.White : Color.FromArgb(87, 97, 112);
    }

    private async Task ExecuteAsync()
    {
        if (_completed)
        {
            Close();
            return;
        }
        _existing = InstallationLocation.Read();
        RefreshDestination();
        if (!_validDestination) return;
        _options.InstallRoot = _directory.Text;
        _options.AutoStart = _startup.Checked;
        _options.InstallDriver = _driver.Checked;
        _busy = true;
        UpdateControls();
        try
        {
            await Task.Run(() =>
            {
                var deployment = new Deployment(_options, message => BeginInvoke(() => _log.AppendText(message + Environment.NewLine)));
                if (_options.Rollback) deployment.Rollback(); else if (_options.Uninstall) deployment.Uninstall(); else deployment.Install();
            });
            _log.AppendText((_options.Rollback ? "上个版本已恢复。请重新打开我控制。" : _options.Uninstall ? "卸载完成。配置与共享驱动已保留。" : "安装完成，请从桌面或开始菜单打开我控制。") + Environment.NewLine);
            _completed = true;
            _execute.Text = "完成";
            new Deployment(_options, _ => { }).Stage("complete", 0);
        }
        catch (Exception ex)
        {
            Deployment.WriteFailure(_options, ex);
            _log.AppendText(Environment.NewLine + "操作失败：" + ex.Message + Environment.NewLine + "详细记录位于 C:\\ProgramData\\StreamHub\\logs\\安装.log" + Environment.NewLine);
            _existing = InstallationLocation.Read();
            RefreshDestination();
        }
        finally { _busy = false; UpdateControls(); }
    }
}

internal sealed class InstallOptions
{
    public string InstallRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StreamHub");
    public bool InstallRootExplicit { get; private set; }
    public bool Quiet { get; set; }
    public bool Uninstall { get; set; }
    public bool AutoStart { get; set; } = true;
    public bool InstallDriver { get; set; } = true;
    public bool VerifyOnly { get; set; }
    public bool VerifyNoPayload { get; set; }
    public bool Rollback { get; set; }
    public bool RequireIdle { get; set; }
    public int WaitParent { get; set; }
    public int CleanupParent { get; set; }
    public string AttemptId { get; private set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    internal bool DataProtected { get; set; }
    internal bool OwnsTransaction { get; set; }

    public static InstallOptions Parse(string[] args)
    {
        var result = new InstallOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--quiet": result.Quiet = true; break;
                case "--rollback": result.Rollback = true; result.RequireIdle = true; result.InstallDriver = false; break;
                case "--require-idle": result.RequireIdle = true; break;
                case "--wait-parent" when i + 1 < args.Length: result.WaitParent = int.Parse(args[++i]); break;
                case "--uninstall": result.Uninstall = true; break;
                case "--no-startup": result.AutoStart = false; break;
                case "--skip-driver": result.InstallDriver = false; break;
                case "--verify-payload": result.VerifyOnly = true; result.Quiet = true; break;
                case "--verify-no-payload": result.VerifyNoPayload = true; result.Quiet = true; break;
                case "--install-dir" when i + 1 < args.Length: result.InstallRoot = args[++i]; result.InstallRootExplicit = true; break;
                case "--update-attempt" when i + 1 < args.Length:
                    var id = args[++i];
                    if (!StreamHub.Updates.InstallTrace.ValidId(id)) throw new ArgumentException("Invalid update attempt ID.");
                    result.AttemptId = id; break;
                case "--cleanup-parent" when i + 1 < args.Length: result.CleanupParent = int.Parse(args[++i]); break;
            }
        }
        return result;
    }
}
