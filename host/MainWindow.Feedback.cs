using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace StreamHub.Host;

public partial class MainWindow
{
    private sealed record FeedbackEntry(string Message, string Time);
    private readonly ObservableCollection<FeedbackEntry> _feedbackEntries = [];
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _statusFeedback;

    private void InitializeDesktopFeedback()
    {
        FeedbackHistoryList.ItemsSource = _feedbackEntries;
        _feedbackTimer.Tick += (_, _) => DismissFeedback();
    }

    private void ShowFeedback(string message, bool error = false, bool persistent = false, bool remember = true)
    {
        if (_closing.IsCancellationRequested || string.IsNullOrWhiteSpace(message)) return;
        _feedbackTimer.Stop();
        FeedbackText.Text = message;
        FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? "DangerText" : "FeedbackTextBrush");
        FeedbackHost.Visibility = Visibility.Visible;
        if (remember && (_feedbackEntries.Count == 0 || _feedbackEntries[0].Message != message))
        {
            _feedbackEntries.Insert(0, new(message, DateTimeOffset.Now.ToString("HH:mm:ss")));
            while (_feedbackEntries.Count > 12) _feedbackEntries.RemoveAt(_feedbackEntries.Count - 1);
            FeedbackHistoryEmptyText.Visibility = Visibility.Collapsed;
        }
        if (!error && !persistent) _feedbackTimer.Start();
    }

    private void UpdateStatusFeedback(bool success, string message)
    {
        if (!success)
        {
            if (_statusFeedback != message) ShowFeedback(message, error: true);
            _statusFeedback = message;
        }
        else
        {
            if (_statusFeedback is not null && FeedbackText.Text == _statusFeedback) DismissFeedback();
            _statusFeedback = null;
        }
    }

    private void DismissFeedback() { _feedbackTimer.Stop(); FeedbackHost.Visibility = Visibility.Collapsed; }
    private void DismissFeedback_Click(object sender, RoutedEventArgs args) => DismissFeedback();
    private void OpenFeedbackHistory_Click(object sender, RoutedEventArgs args)
    {
        CloseTransientPopovers();
        FeedbackHistoryPopover.IsOpen = true;
    }
}
