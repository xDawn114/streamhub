using System.Windows;
using System.Windows.Controls;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private bool _renderingPower, _savingPower;
    private void RefreshPowerSettings()
    {
        if (PreventSleepChoice is null || _savingPower) return;
        _renderingPower = true;
        try
        {
            PreventSleepChoice.SelectedIndex = _status?.PreventSleepMode switch
            { PowerAwakePolicy.System => 0, PowerAwakePolicy.PluggedIn => 2, _ => 1 };
            PreventSleepChoice.IsEnabled = _status is not null && !_busy;
            PowerStateText.Text = _status?.SleepPreventionMessage is { Length: > 0 } message ? message : "等待后台服务读取防休眠状态。";
            PowerStateText.SetResourceReference(TextBlock.ForegroundProperty,
                _status?.SleepPreventionActive == true ? "SuccessText" : "Muted");
        }
        finally { _renderingPower = false; }
    }
    private async void PreventSleep_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_renderingPower || _savingPower || !IsLoaded || _status is null) return;
        var mode = PreventSleepChoice.SelectedIndex switch
        { 0 => PowerAwakePolicy.System, 2 => PowerAwakePolicy.PluggedIn, _ => PowerAwakePolicy.Streaming };
        if (mode == _status.PreventSleepMode) return;
        _savingPower = true;
        PreventSleepChoice.IsEnabled = false;
        try
        {
            var result = await _runtime.SendAsync(new("power-settings", PreventSleepMode: mode), _closing.Token);
            if (result.Success && result.Status is not null) _status = result.Status;
            ShowFeedback(result.Message, error: !result.Success);
        }
        catch (OperationCanceledException) { }
        finally { _savingPower = false; RefreshPowerSettings(); }
    }
}
