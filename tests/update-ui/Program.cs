using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHub.Host;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        var root = Path.GetFullPath(args.Single());
        if (!root.StartsWith(@"D:\Deepseek\build\stage72-tests\desktop\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Isolated output required.");
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var calls = new List<string>();
        var runtime = new RuntimeClient((request, token) =>
        {
            calls.Add(request.Command);
            var status = new RuntimeStatus("fixture", "示例电脑", new("remote", true, true, 1, 48989, true, "待机"),
                new("tablet", false, false, null, 49089, false, "停止"), new(false, false, null, "局域网"), "0.8.5", root, DateTimeOffset.UtcNow);
            string? payload = request.Command switch
            {
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(), [], []), RuntimeProtocol.JsonOptions),
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot("012345", DateTimeOffset.UtcNow.AddMinutes(3), null), RuntimeProtocol.JsonOptions),
                _ => null
            };
            return Task.FromResult(new RuntimeResponse(true, "本机隔离夹具", status, payload));
        });
        var window = new MainWindow(runtime, Path.Combine(root, "ui-preferences.json"))
        { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Width = 940, Height = 720 };
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var pages = (TabControl)window.FindName("Pages"); pages.SelectedItem = window.FindName("SettingsPage");
                var check = (Button)window.FindName("CheckUpdateButton");
                if (check.IsEnabled) throw new Exception("Unconfigured source offered a network operation.");
                var modes = new List<object>();
                foreach (int theme in new[] { 1, 2 })
                {
                    ((ComboBox)window.FindName("ThemeChoice")).SelectedIndex = theme;
                    foreach (int width in new[] { 940, 1280 })
                    {
                        window.Width = width; window.UpdateLayout();
                        var state = (TextBlock)window.FindName("UpdateStateText");
                        var card = (FrameworkElement)((FrameworkElement)state.Parent).Parent;
                        // Show the full action row as a local layout fixture, without invoking handlers.
                        foreach (string name in new[] { "DownloadUpdateButton", "InstallUpdateButton", "CancelUpdateButton" })
                            ((FrameworkElement)window.FindName(name)).Visibility = Visibility.Visible;
                        card.BringIntoView(); window.UpdateLayout();
                        var buttons = new[] { "CheckUpdateButton", "DownloadUpdateButton", "InstallUpdateButton", "CancelUpdateButton" }.Select(n => (Button)window.FindName(n)).ToArray();
                        foreach (var button in buttons)
                        {
                            var position = button.TransformToAncestor(card).Transform(new Point());
                            if (button.ActualHeight < 40 || position.X < 0 || position.X + button.ActualWidth > card.ActualWidth + 1 || button.Margin.Right < 8)
                                throw new Exception("Update action clipped or spacing too small: " + button.Name);
                        }
                        if (width == 940)
                        {
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            var visual = new DrawingVisual();
                            using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                            bitmap.Render(visual);
                            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                            if (!pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha != 0)) throw new Exception("Empty render is not visual evidence.");
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var output = File.Create(Path.Combine(root, theme == 1 ? "updates-light.png" : "updates-dark.png")); encoder.Save(output);
                        }
                        modes.Add(new { theme, windowWidth = width, cardWidth = card.ActualWidth, cardHeight = card.ActualHeight });
                    }
                }
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, modes, installedServiceCalls = 0, fixtureCalls = calls.Count }));
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString() })); }
            finally { window.DisposeWindow(); app.Shutdown(); }
        }));
        window.Show(); app.Run();
    }
}
