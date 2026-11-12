using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace StreamHub.Host;

public partial class MainWindow
{
    private sealed record UiPreferences(string Theme = "system", bool ReduceMotion = false);
    private UiPreferences _uiPreferences = new();
    private bool _themeEventsAttached, _loadingAppearance = true;
    private readonly string _uiPreferencesPath;
    private string UiPreferencesPath => _uiPreferencesPath;
    private static string DefaultUiPreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamHub", "ui-preferences.json");

    private void ThemeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(UiPreferencesPath))
                _uiPreferences = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(UiPreferencesPath)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        ThemeChoice.SelectedIndex = _uiPreferences.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        ReduceMotionCheck.IsChecked = _uiPreferences.ReduceMotion;
        _loadingAppearance = false;
        ApplyDesktopTheme();
        if (!_themeEventsAttached)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _themeEventsAttached = true;
        }
    }

    private void ThemeWindow_Closed(object? sender, EventArgs e)
    {
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
        if (_loadingAppearance || ThemeChoice is null || ReduceMotionCheck is null) return;
        _uiPreferences = new(ThemeChoice.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" },
            ReduceMotionCheck.IsChecked == true);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiPreferencesPath)!);
            var temporary = UiPreferencesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_uiPreferences));
            File.Move(temporary, UiPreferencesPath, true);
            ApplyDesktopTheme();
            if (_uiPreferences.ReduceMotion) ResetPageTransition();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { FeedbackText.Text = "外观设置未能保存：" + error.Message; }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Pages.SelectedItem = SettingsPage;

    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Pages) || !IsLoaded) return;
        ResetPageTransition();
        if (_uiPreferences.ReduceMotion || !SystemParameters.ClientAreaAnimation || !SystemParameters.MenuAnimation) return;
        if (Pages.Template.FindName("PageViewport", Pages) is not FrameworkElement viewport) return;
        // A new selection cancels the previous animation; input is never delayed.
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        viewport.BeginAnimation(OpacityProperty, new DoubleAnimation(0.78, 1, TimeSpan.FromMilliseconds(140))
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        if (viewport.RenderTransform is TranslateTransform offset)
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(140))
                { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }

    private void ResetPageTransition()
    {
        if (Pages?.Template?.FindName("PageViewport", Pages) is not FrameworkElement viewport) return;
        viewport.BeginAnimation(OpacityProperty, null);
        // Transforms from a ControlTemplate may be frozen; use an instance owned by this viewport.
        viewport.RenderTransform = new TranslateTransform();
    }

    private void ApplyDesktopTheme()
    {
        bool light = _uiPreferences.Theme != "dark";
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

        string resourceName = light ? "Theme.Light.xaml" : "Theme.Dark.xaml";
        var dictionaries = Resources.MergedDictionaries;
        int current = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            string? source = dictionaries[i].Source?.OriginalString;
            if (source is null) continue;
            if (!source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase) &&
                !source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase)) continue;
            current = i;
            if (source.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase)) return;
            break;
        }
        var replacement = new ResourceDictionary { Source = new Uri(
            $"/{typeof(MainWindow).Assembly.GetName().Name};component/{resourceName}", UriKind.Relative) };
        if (current >= 0) dictionaries[current] = replacement;
        else dictionaries.Insert(0, replacement);
    }
}
