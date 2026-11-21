using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace StreamHub.Host;

public partial class MainWindow
{
    private sealed record UiPreferences(string Theme = "system", bool ReduceMotion = false, int TabletOrientation = 0, int TabletLink = 0,
        double? WindowLeft = null, double? WindowTop = null, double? WindowWidth = null, double? WindowHeight = null,
        bool WindowMaximized = false);
    private UiPreferences _uiPreferences = new();
    private bool _themeEventsAttached, _loadingAppearance = true;
    private bool _preferencesFileExistsOnStart, _restoringWindowGeometry;
    private readonly DispatcherTimer _windowGeometrySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly string _uiPreferencesPath;
    private string UiPreferencesPath => _uiPreferencesPath;
    private static string DefaultUiPreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamHub", "ui-preferences.json");

    private void ThemeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        bool firstLaunch = !_preferencesFileExistsOnStart;
        ThemeChoice.SelectedIndex = _uiPreferences.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        ReduceMotionCheck.IsChecked = _uiPreferences.ReduceMotion;
        OrientationBox.SelectedIndex = Math.Clamp(_uiPreferences.TabletOrientation,0,4);
        RequestLinkBox.SelectedIndex = Math.Clamp(_uiPreferences.TabletLink,0,2);
        _loadingAppearance = false;
        if (firstLaunch)
        {
            Pages.SelectedItem = HomePage;
            Appearance_Changed(sender, e);
        }
        ApplyDesktopTheme();
        if (!_themeEventsAttached)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _themeEventsAttached = true;
        }
    }

    private void ThemeWindow_Closed(object? sender, EventArgs e)
    {
        _windowGeometrySaveTimer.Stop();
        if (!_themeEventsAttached) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _themeEventsAttached = false;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyDesktopTheme));
            return;
        }
        ApplyDesktopTheme();
    }

    private void Appearance_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingAppearance || ThemeChoice is null || ReduceMotionCheck is null || OrientationBox is null || RequestLinkBox is null) return;
        var next = _uiPreferences with
        {
            Theme = ThemeChoice.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" },
            ReduceMotion = ReduceMotionCheck.IsChecked == true,
            TabletOrientation = OrientationBox.SelectedIndex,
            TabletLink = RequestLinkBox.SelectedIndex
        };
        next = CaptureWindowGeometry(next);
        try
        {
            PersistUiPreferences(next);
            ApplyDesktopTheme();
            if (_uiPreferences.ReduceMotion) ResetPageTransition();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _loadingAppearance = true;
            ThemeChoice.SelectedIndex = _uiPreferences.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
            ReduceMotionCheck.IsChecked = _uiPreferences.ReduceMotion;
            OrientationBox.SelectedIndex = _uiPreferences.TabletOrientation;
            RequestLinkBox.SelectedIndex = _uiPreferences.TabletLink;
            _loadingAppearance = false;
            ShowFeedback("设置未能保存：" + error.Message, error: true);
        }
    }

    private void TabletPreference_Changed(object sender, RoutedEventArgs e)
    {
        Appearance_Changed(sender,e);
    }

    private void LoadUiPreferences()
    {
        _preferencesFileExistsOnStart = File.Exists(UiPreferencesPath);
        try
        {
            if (_preferencesFileExistsOnStart)
                _uiPreferences = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(UiPreferencesPath)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void RestoreWindowGeometry()
    {
        if (_uiPreferences.WindowLeft is not double left || _uiPreferences.WindowTop is not double top ||
            _uiPreferences.WindowWidth is not double width || _uiPreferences.WindowHeight is not double height ||
            !double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height)) return;

        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        double screenHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        Width = Math.Clamp(width, MinWidth, screenWidth);
        Height = Math.Clamp(height, MinHeight, screenHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(left, screenLeft - Width + 120, screenLeft + screenWidth - 120);
        Top = Math.Clamp(top, screenTop - Height + 80, screenTop + screenHeight - 80);
        _restoringWindowGeometry = true;
        WindowState = _uiPreferences.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        _restoringWindowGeometry = false;
    }

    private void WindowGeometry_Changed(object? sender, EventArgs e)
    {
        if (_restoringWindowGeometry || WindowState == WindowState.Minimized || !IsLoaded) return;
        _windowGeometrySaveTimer.Stop();
        _windowGeometrySaveTimer.Start();
    }

    private void SaveWindowGeometryAfterDelay(object? sender, EventArgs e)
    {
        _windowGeometrySaveTimer.Stop();
        SaveWindowGeometry();
    }

    private void SaveWindowGeometry()
    {
        if (WindowState == WindowState.Minimized) return;
        try { PersistUiPreferences(CaptureWindowGeometry(_uiPreferences)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private UiPreferences CaptureWindowGeometry(UiPreferences preferences)
    {
        if (WindowState == WindowState.Minimized) return preferences;
        double left, top, width, height;
        if (WindowState == WindowState.Normal)
        {
            left = Left; top = Top; width = Width; height = Height;
        }
        else
        {
            var bounds = RestoreBounds;
            if (bounds.IsEmpty) return preferences;
            left = bounds.Left; top = bounds.Top; width = bounds.Width; height = bounds.Height;
        }
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0) return preferences;
        return preferences with
        {
            WindowLeft = left,
            WindowTop = top,
            WindowWidth = width,
            WindowHeight = height,
            WindowMaximized = WindowState == WindowState.Maximized
        };
    }

    private void PersistUiPreferences(UiPreferences next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UiPreferencesPath)!);
        var temporary = UiPreferencesPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, UiPreferencesPath, true);
        _uiPreferences = next;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Pages.SelectedItem = SettingsPage;

    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Pages) || !IsLoaded) return;
        CloseTransientPopovers();
        if (Pages.SelectedItem == FilesPage) _ = RunAction(RefreshFilesAsync);
        ResetPageTransition();
        if (_uiPreferences.ReduceMotion || !SystemParameters.ClientAreaAnimation || !SystemParameters.MenuAnimation) return;
        if (Pages.Template.FindName("PageViewport", Pages) is not FrameworkElement viewport) return;
        // A new selection cancels the previous animation; input is never delayed.
        var easing = AppMotion.EaseOut();
        viewport.BeginAnimation(OpacityProperty, new DoubleAnimation(0.78, 1, AppMotion.Page)
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        if (viewport.RenderTransform is TranslateTransform offset)
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, AppMotion.Page)
                { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }

    private void ResetPageTransition()
    {
        if (Pages?.Template?.FindName("PageViewport", Pages) is not FrameworkElement viewport) return;
        viewport.BeginAnimation(OpacityProperty, null);
        // Transforms from a ControlTemplate may be frozen; use an instance owned by this viewport.
        viewport.RenderTransform = new TranslateTransform();
    }

    private void SettingsCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SettingsCategories) || !IsLoaded) return;
        if (SettingsCategories.Template.FindName("PART_SelectedContentHost", SettingsCategories) is not FrameworkElement content) return;
        content.BeginAnimation(OpacityProperty, null);
        content.RenderTransform = new TranslateTransform();
        if (!AppMotion.IsEnabled(_uiPreferences.ReduceMotion)) return;
        content.Opacity = 0.92;
        if (content.RenderTransform is TranslateTransform offset)
        {
            offset.X = 0;
            offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28, 0, AppMotion.Subpage)
            {
                EasingFunction = AppMotion.EaseOut(),
                FillBehavior = FillBehavior.Stop
            });
        }
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0.92, 1, AppMotion.Subpage)
        {
            EasingFunction = AppMotion.EaseOut(),
            FillBehavior = FillBehavior.Stop
        });
    }

    private void Expander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && expander.Template.FindName("Body", expander) is FrameworkElement body)
            AppMotion.AnimateExpander(body, true, _uiPreferences.ReduceMotion);
    }

    private void Expander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && expander.Template.FindName("Body", expander) is FrameworkElement body)
            AppMotion.AnimateExpander(body, false, _uiPreferences.ReduceMotion);
    }

    private void ApplyDesktopTheme()
    {
        bool light = _uiPreferences.Theme != "dark";
        ScrollActivity.SetReduceMotion(this, _uiPreferences.ReduceMotion || !SystemParameters.ClientAreaAnimation);
        if (_uiPreferences.Theme == "system")
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                light = key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }

        string resourceName = SystemParameters.HighContrast ? "Theme.HighContrast.xaml" :
            light ? "Theme.Light.xaml" : "Theme.Dark.xaml";
        var dictionaries = Resources.MergedDictionaries;
        int current = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            string? source = dictionaries[i].Source?.OriginalString;
            if (source is null) continue;
            if (!source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase) &&
                !source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase) &&
                !source.EndsWith("Theme.HighContrast.xaml", StringComparison.OrdinalIgnoreCase)) continue;
            current = i;
            if (source.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase)) return;
            break;
        }
        var replacement = new ResourceDictionary { Source = new Uri(
            $"/{typeof(MainWindow).Assembly.GetName().Name};component/{resourceName}", UriKind.Relative) };
        if (current >= 0) dictionaries[current] = replacement;
        else dictionaries.Insert(0, replacement);
        if (IsLoaded && Pages is not null) AppMotion.AnimateThemeSurface(Pages, _uiPreferences.ReduceMotion);
    }
}
