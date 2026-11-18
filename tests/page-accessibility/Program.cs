using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        string root = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        var snapshots = new List<object>();
        var now = DateTimeOffset.UtcNow;
        var runtime = new RuntimeClient((request, _) =>
        {
            string? payload = request.Command switch
            {
                "control-device-list" => JsonSerializer.Serialize(new ControlDeviceList(false, []), RuntimeProtocol.JsonOptions),
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(), [], []), RuntimeProtocol.JsonOptions),
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot(null, null, null), RuntimeProtocol.JsonOptions),
                _ => null
            };
            var status = new RuntimeStatus("fixture", "fixture", new("remote", false, true, null, 48989, false, "idle"),
                new("tablet", false, false, null, 49089, false, "idle"), new(false, false, null, "idle"), "fixture", root, now);
            return Task.FromResult(new RuntimeResponse(true, "fixture", status, payload));
        });
        var window = new MainWindow(runtime, Path.Combine(root, "preferences.json"))
            { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        void Check(bool passed, string label)
        {
            if (!passed) throw new Exception(label);
            checks.Add(label);
        }
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                var pages = (TabControl)window.FindName("Pages");
                var categories = (TabControl)window.FindName("SettingsCategories");
                var windowPeer = UIElementAutomationPeer.CreatePeerForElement(window)!;
                pages.SelectedItem = window.FindName("SettingsPage");
                window.UpdateLayout(); Pump();
                var peers = Walk(windowPeer).ToArray();
                Check(peers.Any(p => p.GetAutomationId() == "SettingsCategories"),
                    "Window automation tree reaches the settings categories through Pages");
                var pagesPeer = peers.Single(p => p.GetAutomationId() == "Pages");
                Check(pagesPeer.GetAutomationControlType() == AutomationControlType.Pane,
                    "Content-only page host has pane semantics instead of phantom tabs");
                var categoryPeer = peers.Single(p => p.GetAutomationId() == "SettingsCategories");
                var tabs = categoryPeer.GetChildren()!.Where(p => p.GetAutomationControlType() == AutomationControlType.TabItem).ToArray();
                Check(tabs.Length == 6 && tabs.All(p => p.IsControlElement() && !string.IsNullOrWhiteSpace(p.GetName())),
                    "All six settings tabs expose accessible names and control elements");
                string[] expectedIds = ["ThemeChoice", "PrivacyModeCheck", "", "PublicLanAccessCheck", "OrientationBox", "CheckUpdateButton"];
                for (int i = 0; i < tabs.Length; i++)
                {
                    var selection = (ISelectionItemProvider)tabs[i].GetPattern(PatternInterface.SelectionItem)!;
                    selection.Select(); window.UpdateLayout(); Pump();
                    Check(categories.SelectedIndex == i && selection.IsSelected,
                        $"Settings tab {i} can be selected through its automation pattern");
                    var selectedPeers = Walk(windowPeer).ToArray();
                    if (expectedIds[i].Length > 0)
                        Check(selectedPeers.Any(p => p.GetAutomationId() == expectedIds[i]),
                            $"Selected category {i} exposes its actual controls");
                    if (i != 0)
                        Check(!selectedPeers.Any(p => p.GetAutomationId() == "ThemeChoice"),
                            $"Category {i} does not expose stale general-settings content");
                    snapshots.Add(new { category = tabs[i].GetName(), controls = selectedPeers.Select(p =>
                        new { id = p.GetAutomationId(), name = p.GetName(), type = p.GetAutomationControlType().ToString() }).ToArray() });
                }
                foreach (string page in new[] { "HomePage", "FilesPage", "SettingsPage" })
                {
                    pages.SelectedItem = window.FindName(page); window.UpdateLayout(); Pump();
                    var current = Walk(windowPeer).ToArray();
                    Check(current.Any(p => p.GetAutomationId() == (page == "FilesPage" ? "SendFileButton" : page == "HomePage" ? "RefreshDeviceCardsButton" : "SettingsCategories")),
                        $"Navigation to {page} refreshes the reachable automation content");
                    if (page != "SettingsPage")
                        Check(!current.Any(p => p.GetAutomationId() == "SettingsCategories"),
                            $"Navigation to {page} removes the old settings subtree");
                }
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
                    { passed = true, checkedAt = DateTimeOffset.Now, count = checks.Count, checks, snapshots,
                      installedServiceCalls = 0, operatingSystemInputSent = false }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS {checks.Count} focused automation-peer checks.");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
                    { passed = false, checkedAt = DateTimeOffset.Now, checks, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                Console.Error.WriteLine(ex); Environment.ExitCode = 1;
            }
            finally { window.DisposeWindow(); app.Shutdown(); }
        }));
        window.Show(); app.Run();
    }

    static IEnumerable<AutomationPeer> Walk(AutomationPeer peer)
    {
        yield return peer;
        foreach (var child in peer.GetChildren() ?? [])
            foreach (var descendant in Walk(child)) yield return descendant;
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
