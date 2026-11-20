using System.Reflection;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Supply fixture and evidence directories.");
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        var fixture = Path.GetFullPath(args[0]);
        var evidence = Path.GetFullPath(args[1]);
        var root = Path.Combine(fixture, "installed");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(root, ".streamhub-install.json"), "{\"productId\":\"StreamHub.PC.0b1e5f94\"}");
        var assembly = Assembly.Load("串流中心安装");
        var optionsType = assembly.GetType("StreamHub.Installer.InstallOptions", true)!;
        var windowType = assembly.GetType("StreamHub.Installer.InstallWindow", true)!;
        var locationType = assembly.GetType("StreamHub.Installer.InstallationLocation", true)!;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var parse = optionsType.GetMethod("Parse")!;
        var explicitOptions = parse.Invoke(null, [new[] { "--quiet", "--install-dir", @"D:\Explicit" }])!;
        Check((bool)optionsType.GetProperty("InstallRootExplicit")!.GetValue(explicitOptions)! &&
            (string)optionsType.GetProperty("InstallRoot")!.GetValue(explicitOptions)! == @"D:\Explicit", "explicit path preserved");
        var results = new List<object>();
        foreach (var scenario in new[] { "existing", "conflict", "fresh", "incomplete", "long-conflict", "relative", "scaled" })
        {
            var options = Activator.CreateInstance(optionsType, true)!;
            using var form = (Form)Activator.CreateInstance(windowType, [options])!;
            T Field<T>(string name) => (T)windowType.GetField(name, fields)!.GetValue(form)!;
            void Invoke(string name) => windowType.GetMethod(name, fields)!.Invoke(form, null);
            var directory = Field<TextBox>("_directory");
            var selected = scenario switch
            {
                "existing" or "incomplete" or "scaled" => root,
                "relative" => "D:Relative",
                _ => Path.Combine(fixture, "new-install")
            };
            string? existingRoot = scenario is "fresh" or "relative" ? null : root;
            string? error = null;
            if (scenario == "long-conflict") existingRoot = Path.Combine(fixture, string.Concat(Enumerable.Repeat("Original-", 18)));
            var location = Activator.CreateInstance(locationType, [existingRoot, error, scenario == "incomplete"]);
            if (scenario == "incomplete") File.Delete(Path.Combine(root, ".streamhub-install.json"));
            else File.WriteAllText(Path.Combine(root, ".streamhub-install.json"), "{\"productId\":\"StreamHub.PC.0b1e5f94\"}");
            windowType.GetField("_existing", fields)!.SetValue(form, location);
            directory.Text = selected;
            Invoke("RefreshDestination");
            if (scenario == "scaled") form.Scale(new SizeF(1.5f, 1.5f));
            form.PerformLayout();
            var execute = Field<Button>("_execute");
            var status = Field<Label>("_locationStatus");
            var log = Field<TextBox>("_log");
            var driver = Field<CheckBox>("_driver");
            var link = Field<LinkLabel>("_useExisting");
            var showLink = Field<bool>("_showUseExisting");
            var expected = scenario is "existing" or "fresh" or "incomplete" or "scaled";
            Check(execute.Enabled == expected, scenario + " install eligibility");
            Check(log.Top >= driver.Bottom && log.Bottom <= execute.Top - 10, scenario + " content layout");
            Check(!showLink || Field<CheckBox>("_startup").Top >= link.Bottom + 8, scenario + " repair link layout");
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(form.BackColor);
            foreach (Control control in form.Controls)
            {
                if (control == link && !showLink || control.Width <= 0 || control.Height <= 0) continue;
                control.DrawToBitmap(bitmap, control.Bounds);
            }
            bitmap.Save(Path.Combine(evidence, scenario + ".png"));
            results.Add(new { scenario, path = selected, action = execute.Text, enabled = execute.Enabled, status = status.Text });
            if (scenario == "conflict")
            {
                windowType.GetMethod("UseExistingLocation", fields)!.Invoke(form, null);
                Check(execute.Enabled && directory.Text == root && execute.Text == "更新 / 修复", "repair action restores original path");
            }
            windowType.GetField("_busy", fields)!.SetValue(form, true);
            Invoke("UpdateControls");
            Check(!execute.Enabled && !directory.Enabled && !Field<Button>("_browse").Enabled && !Field<CheckBox>("_startup").Enabled,
                scenario + " locks controls during install");
        }
        File.WriteAllText(Path.Combine(evidence, "ui-checks.json"), JsonSerializer.Serialize(new
        {
            checkedAt = DateTimeOffset.Now, shownOnDesktop = false, installationAttempted = false,
            rendering = "offscreen Windows Forms controls", scenarios = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Installer UI checks passed.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
