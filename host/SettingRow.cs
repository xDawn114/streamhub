using System.Windows;
using System.Windows.Controls;

namespace StreamHub.Host;

/// <summary>A settings row grows with its explanation and keeps its control at a readable width.</summary>
public sealed class SettingRow : HeaderedContentControl
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
