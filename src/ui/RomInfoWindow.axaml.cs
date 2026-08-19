using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>Read-only facts about the open ROM. A window rather than a drawn panel, so it can be
/// left open beside the editor.</summary>
public partial class RomInfoWindow : Window
{
    public sealed record Row(string Label, string Value);

    public RomInfoWindow() => AvaloniaXamlLoader.Load(this);

    public RomInfoWindow(IEnumerable<(string Label, string Value)> info) : this()
    {
        var rows = info.Select(i => new Row(i.Label, i.Value)).ToList();
        this.GetControl<ItemsControl>("Rows").ItemsSource = rows;
        this.GetControl<TextBlock>("Empty").IsVisible = rows.Count == 0;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
