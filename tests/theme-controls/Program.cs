using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHub.Host;
using StreamHub.Host.Runtime;

internal static class Program
{
    private static int checks;
    [STAThread] private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(new RuntimeClient((_, _) => Task.FromResult(new RuntimeResponse(false, "fixture"))),
            Path.Combine(output, "preferences.json"));
        try
        {
            var pages = (PageContentHost)window.FindName("Pages");
            Check(!pages.Focusable && !pages.IsTabStop && pages.FocusVisualStyle is null, "page container cannot acquire focus rectangle");
            var settingRow = new SettingRow { Header = "Setting", Style = (Style)window.FindResource(typeof(SettingRow)) };
            Check(!settingRow.Focusable && !settingRow.IsTabStop && settingRow.FocusVisualStyle is null, "setting rows cannot receive a dotted focus rectangle");
            Check(((UIElement)window.FindName("AssistanceNavigation")).Visibility == Visibility.Collapsed, "frozen navigation hidden");
            Check(((UIElement)window.FindName("AssistancePage")).Visibility == Visibility.Collapsed, "frozen page hidden");
            typeof(MainWindow).GetMethod("OpenAssistance", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(pages.SelectedItem == window.FindName("HomePage"), "old assistance launch routes to devices");
            var permissions = (FrameworkElement)window.FindName("DevicePermissionsDialog");
            permissions.Visibility = Visibility.Visible;
            using var inputSource = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("Permission keyboard fixture")
                    { WindowStyle = 0, Width = 1, Height = 1 });
            var previewEscape = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            permissions.RaiseEvent(previewEscape);
            Check(permissions.Visibility == Visibility.Visible && !previewEscape.Handled,
                "permission dialog leaves preview Escape to an open dropdown");
            var consumedEscape = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent, Handled = true };
            permissions.RaiseEvent(consumedEscape);
            Check(permissions.Visibility == Visibility.Visible,
                "Escape handled by a nested control does not close the permission dialog");
            permissions.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(permissions.Visibility == Visibility.Collapsed,
                "unhandled Escape still closes the permission dialog");
            foreach (string theme in new[] { "Light", "Dark", "HighContrast", "Light" })
            {
                window.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(
                    $"/{typeof(MainWindow).Assembly.GetName().Name};component/Theme.{theme}.xaml", UriKind.Relative) };
                Brush Color(string key) => (Brush)window.FindResource(key);
                pages.ApplyTemplate();
                Check(Same(((Border)pages.Template.FindName("PageViewport",pages)).Background,Color("Canvas")), $"{theme}: full page viewport uses the canvas rather than a giant card");
                if (theme != "HighContrast")
                {
                    foreach (var pair in new[] { ("OnAccent", "Accent"), ("OnAccent", "AccentHover"), ("OnAccent", "AccentPressed"),
                        ("TextPrimary", "Surface"), ("TextPrimary", "ControlHover"), ("TextPrimary", "ControlPressed"),
                        ("Muted", "Canvas"), ("Muted", "InsetSurface"), ("AccentText", "Selection"), ("AccentText", "Surface"),
                        ("AccentText", "ControlHover"), ("ActionPressedText", "ControlPressed") })
                        Check(Contrast(Color(pair.Item1), Color(pair.Item2)) >= 4.5, $"{theme}: {pair} text contrast");
                    foreach (string surface in new[] { "Canvas", "Surface", "InsetSurface", "ControlHover", "ControlPressed" })
                    {
                        Check(Contrast(Color("ControlBorder"), Color(surface)) >= 3, $"{theme}: {surface} control outline contrast");
                        Check(Contrast(Color("AccentText"), Color(surface)) >= 3, $"{theme}: {surface} blue action outline contrast");
                    }
                    Check(Contrast(Color("DisabledText"), Color("DisabledSurface")) >= 3, $"{theme}: disabled text contrast");
                }
                var panel = new StackPanel { Margin = new Thickness(20) };
                var root = new Border { Width = 1100, Background = Color("Canvas"), Child = panel, Resources = window.Resources };
                panel.Children.Add(new TextBlock { Text = theme, Foreground = Color("TextPrimary"), FontSize = 22, Margin = new Thickness(0, 0, 0, 16) });
                foreach (bool primary in new[] { true, false })
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 24) };
                    panel.Children.Add(row);
                    foreach (string state in new[] { "Normal", "Hover", "Pressed", "Focus", "Disabled" })
                    {
                        var button = new Button { Content = state, Width = 180, Margin = new Thickness(0, 0, 24, 0),
                            Style = (Style)window.FindResource(primary ? "Primary" : typeof(Button)) };
                        row.Children.Add(button);
                        if (state == "Hover") SetState(button, "IsMouseOver", true);
                        if (state == "Pressed") SetState(button, "IsPressed", true);
                        if (state == "Focus") { FocusInput.SetKeyboardMode(button, true); SetState(button, "IsKeyboardFocused", true); }
                        if (state == "Disabled") button.IsEnabled = false;
                        button.ApplyTemplate();
                        var frame = (Border)button.Template.FindName("Frame", button);
                        string token = state switch { "Hover" => primary ? "AccentHover" : "ControlHover",
                            "Pressed" => primary ? "AccentPressed" : "ControlPressed", "Disabled" => "DisabledSurface",
                            _ => primary ? "Accent" : "Surface" };
                        Check(Same(frame.Background, Color(token)) && frame.Opacity == 1, $"{theme}: {(primary ? "primary" : "secondary")} {state} surface");
                        string textToken = state == "Disabled" ? "DisabledText" : primary ? "OnAccent" : state == "Pressed" ? "ActionPressedText" : "AccentText";
                        Check(Same(button.Foreground, Color(textToken)), $"{theme}: {(primary ? "primary" : "secondary")} {state} text");
                        if (!primary && state != "Disabled") Check(Same(frame.BorderBrush, Color("AccentText")), $"{theme}: enabled secondary action has a blue outline");
                        var ring = (Border)button.Template.FindName("FocusRing", button);
                        Check((ring.Visibility == Visibility.Visible) == (state == "Focus"), $"{theme}: {state} focus ring");
                    }
                }
                var choices = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(choices);
                var quick = new Button { Content = "Quick action", Style = (Style)window.FindResource("DeviceQuickAction") };
                choices.Children.Add(quick);
                quick.ApplyTemplate();
                var quickFrame = (Border)quick.Template.FindName("Frame", quick);
                SetState(quick, "IsMouseOver", true);
                Check(Same(quickFrame.Background, Color("ControlHover")), $"{theme}: quick action hover");
                SetState(quick, "IsPressed", true);
                Check(Same(quickFrame.Background, Color("ControlPressed")) && Same(quick.Foreground, Color("ActionPressedText")), $"{theme}: quick action pressed text");
                quick.IsEnabled = false;
                Check(Same(quickFrame.Background, Color("DisabledSurface")) && Same(quickFrame.BorderBrush, Color("ControlBorder"))
                    && Same(quick.Foreground, Color("DisabledText")), $"{theme}: disabled quick action overrides blue hover and pressed states");
                choices.Children.Remove(quick);
                var category = new TabItem { Header = "Input", Style = (Style)window.FindResource("SettingsCategoryItem") };
                choices.Children.Add(category);category.ApplyTemplate();SetState(category, "IsMouseOver", true);
                var categoryHover = (Border)category.Template.FindName("HoverFrame", category);
                Check(categoryHover.CornerRadius.TopLeft > 0 && Same(categoryHover.Background, Color("ControlHover")), $"{theme}: settings tab hover has rounded corners");
                choices.Children.Remove(category);
                foreach (string state in new[] { "Hover", "Selected", "Focus" })
                {
                    var item = new ComboBoxItem { Content = state, Width = 210, Margin = new Thickness(0, 0, 24, 0),
                        Style = (Style)window.FindResource("ComboBoxItemBase") };
                    choices.Children.Add(item);
                    if (state == "Hover") SetState(item, "IsMouseOver", true);
                    if (state == "Selected") item.IsSelected = true;
                    if (state == "Focus") { FocusInput.SetKeyboardMode(item, true); SetState(item, "IsKeyboardFocused", true); }
                    item.ApplyTemplate();
                    var mark = (Border)item.Template.FindName("SelectedMark", item);
                    var frame = (Border)item.Template.FindName("ItemFrame", item);
                    Check(frame.CornerRadius.TopLeft > 0 && item.FocusVisualStyle is null, $"{theme}: menu states use rounded corners without the system dotted focus visual");
                    Check((mark.Visibility == Visibility.Visible) == (state == "Selected"), $"{theme}: selected mark independent of hover and focus");
                    Check(state != "Focus" || Same(frame.BorderBrush, Color("Focus")), $"{theme}: list focus outline");
                }
                root.Measure(new Size(1100, 300)); root.Arrange(new Rect(0, 0, 1100, 300)); root.UpdateLayout();
                foreach (int count in new[] { 3, 30 })
                {
                    var combo = new ComboBox { Width = 224, SelectedIndex = 0,
                        Style = (Style)window.FindResource(typeof(ComboBox)),
                        ItemsSource = Enumerable.Range(0, count).Select(i => $"Option {i}").ToArray() };
                    choices.Children.Add(combo);
                    combo.ApplyTemplate();
                    combo.Measure(new Size(224, 40));
                    combo.Arrange(new Rect(0, 0, 224, 40));
                    combo.UpdateLayout();
                    var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                    var popupFrame = (Border)popup.Child;
                    popupFrame.Measure(new Size(224, double.PositiveInfinity));
                    popupFrame.Arrange(new Rect(new Point(), popupFrame.DesiredSize));
                    popupFrame.UpdateLayout();
                    var viewer = (ScrollViewer)popupFrame.Child;
                    var viewport = (ScrollContentPresenter)viewer.Template.FindName("PART_ScrollContentPresenter", viewer);
                    var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
                    item.ApplyTemplate();
                    var frame = (Border)item.Template.FindName("ItemFrame", item);
                    bool scrolling = count > 3;
                    Check(Math.Abs(popupFrame.ActualWidth - combo.ActualWidth) < .5,
                        $"{theme}: dropdown keeps the owning control width");
                    Check((viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible) == scrolling,
                        $"{theme}: {count}-item dropdown only shows a scrollbar for overflow");
                    Check(Math.Abs(frame.ActualWidth - viewport.ActualWidth) < .5,
                        $"{theme}: {count}-item selected background fills the content viewport");
                    Check(scrolling || Math.Abs(viewer.ActualWidth - viewport.ActualWidth) < .5,
                        $"{theme}: short dropdown does not reserve an empty scrollbar gutter");
                    if (scrolling)
                    {
                        viewer.ScrollToBottom(); popupFrame.UpdateLayout();
                        Check(viewer.VerticalOffset > 0, $"{theme}: long dropdown can reach later options");
                    }
                    if (theme != "HighContrast")
                    {
                        var popupBitmap = new RenderTargetBitmap((int)Math.Ceiling(popupFrame.ActualWidth),
                            (int)Math.Ceiling(popupFrame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        popupBitmap.Render(popupFrame);
                        var popupEncoder = new PngBitmapEncoder(); popupEncoder.Frames.Add(BitmapFrame.Create(popupBitmap));
                        using var popupStream = File.Create(Path.Combine(output, $"dropdown-{theme.ToLowerInvariant()}-{count}.png"));
                        popupEncoder.Save(popupStream);
                    }
                    choices.Children.Remove(combo);
                }
                if (theme != "HighContrast")
                {
                    var bitmap = new RenderTargetBitmap(1100, 300, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"controls-{theme.ToLowerInvariant()}.png")); encoder.Save(stream);
                }
            }
            Console.WriteLine($"{checks} theme/control checks passed. Offscreen rendering only; no installed settings changed.");
        }
        finally { window.DisposeWindow(); app.Shutdown(); }
    }
    private static void SetState(DependencyObject target, string name, bool value)
    {
        for (Type? type = target.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name + "PropertyKey", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) is { } field)
            { target.SetValue((DependencyPropertyKey)field.GetValue(null)!, value); return; }
        throw new Exception("Missing state key: " + name);
    }
    private static bool Same(Brush a, Brush b) => ((SolidColorBrush)a).Color == ((SolidColorBrush)b).Color;
    private static double Contrast(Brush a, Brush b)
    {
        double L(Brush brush)
        {
            var c = ((SolidColorBrush)brush).Color;
            double V(byte x) { double v = x / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * V(c.R) + .7152 * V(c.G) + .0722 * V(c.B);
        }
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); checks++; }
}
