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
        if (options.VerifyNoPayload) return System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("StreamHub.Payload.zip") ? 3 : 0;
        if (options.CleanupParent > 0) return Deployment.Cleanup(options);
        ApplicationConfiguration.Initialize();
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("安装或卸载需要管理员权限，请允许 Windows 的权限确认。", "我控制", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 5;
        }
        if (options.Rollback && Deployment.LaunchRollbackWorker(options, args)) return 0;
        if(options.WaitParent>0)
        {
            try { using var parent=Process.GetProcessById(options.WaitParent); if(!parent.WaitForExit(10000))return 17; }
            catch(ArgumentException) { }
        }
        using var transaction = new Mutex(true, @"Global\StreamHub.InstallTransaction", out var ownsTransaction);
        if (!ownsTransaction)
        {
            if (!options.Quiet) MessageBox.Show("另一项安装或恢复正在进行，请等待它完成。", "我控制");
            return 16;
        }
        if (options.Quiet)
        {
            try
            {
                var deployment = new Deployment(options, _ => { });
                if (options.VerifyOnly) deployment.VerifyPackage();
                else if (options.Rollback) deployment.Rollback();
                else if (options.Uninstall) deployment.Uninstall(); else deployment.Install();
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
    private bool _busy;

    public InstallWindow(InstallOptions options)
    {
        _options = options;
        Text = options.Rollback ? "恢复我控制上个版本" : options.Uninstall ? "卸载我控制" : "我控制 · 电脑端安装";
        ClientSize = new Size(650, 520);
        MinimumSize = new Size(666, 559);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(247, 249, 252);
        var title = new Label { Text = options.Rollback ? "恢复上个版本" : options.Uninstall ? "卸载我控制" : "安装我控制", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true, Location = new Point(28, 24) };
        var intro = new Label { Text = options.Rollback ? "验证并恢复上次升级前的程序备份。设备绑定和个人设置保留。" : options.Uninstall ? "移除本产品的程序、服务与启动项。设备绑定和设置会保留。" : "将远程串流、副屏和组网组件安装到同一个产品目录。", AutoSize = false, Size = new Size(595, 42), Location = new Point(30, 76) };
        _directory = new TextBox { Text = options.InstallRoot, Location = new Point(30, 132), Size = new Size(492, 30), ReadOnly = options.Uninstall };
        var browse = new Button { Text = "选择位置", Location = new Point(532, 130), Size = new Size(90, 32), Enabled = !options.Uninstall };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = "选择我控制的专用安装文件夹", SelectedPath = _directory.Text }; if (picker.ShowDialog(this) == DialogResult.OK) _directory.Text = picker.SelectedPath; };
        _startup = new CheckBox { Text = "登录 Windows 后显示后台托盘", Checked = options.AutoStart, AutoSize = true, Location = new Point(30, 180), Visible = !options.Uninstall };
        _driver = new CheckBox { Text = "安装副屏驱动（已安装时保留现有驱动）", Checked = options.InstallDriver, AutoSize = true, Location = new Point(30, 213), Visible = !options.Uninstall };
        _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Location = new Point(30, 257), Size = new Size(592, 184), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        _execute = new Button { Text = options.Rollback ? "恢复上个版本" : options.Uninstall ? "开始卸载" : "安装 / 修复", Size = new Size(145, 39), Location = new Point(477, 460), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _execute.Click += async (_, _) => await ExecuteAsync();
        Controls.AddRange([title, intro, _directory, browse, _startup, _driver, _log, _execute]);
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private async Task ExecuteAsync()
    {
        if (_execute.Text == "完成") { Close(); return; }
        _options.InstallRoot = _directory.Text;
        _options.AutoStart = _startup.Checked;
        _options.InstallDriver = _driver.Checked;
        _busy = true;
        _execute.Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                var deployment = new Deployment(_options, message => BeginInvoke(() => _log.AppendText(message + Environment.NewLine)));
                if (_options.Rollback) deployment.Rollback(); else if (_options.Uninstall) deployment.Uninstall(); else deployment.Install();
            });
            _log.AppendText(_options.Rollback ? "上个版本已恢复。请重新打开我控制。" : _options.Uninstall ? "卸载完成。配置与共享驱动已保留。" : "安装完成。请从开始菜单打开“我控制”。");
            _execute.Text = "完成";
        }
        catch (Exception ex)
        {
            Deployment.WriteFailure(_options, ex);
            _log.AppendText("操作失败：" + ex.Message + Environment.NewLine + "详细记录位于 C:\\ProgramData\\StreamHub\\logs\\安装.log");
        }
        finally { _busy = false; _execute.Enabled = true; }
    }
}

internal sealed class InstallOptions
{
    public string InstallRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StreamHub");
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
                case "--install-dir" when i + 1 < args.Length: result.InstallRoot = args[++i]; break;
                case "--cleanup-parent" when i + 1 < args.Length: result.CleanupParent = int.Parse(args[++i]); break;
            }
        }
        return result;
    }
}
