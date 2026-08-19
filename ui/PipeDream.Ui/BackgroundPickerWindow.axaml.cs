using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// Choose which background image a level's layer 2 uses.
///
/// A background is recognised by the levels that share it — vanilla ships no names for them, and
/// seventeen bare addresses mean nothing on their own — so the "used by" column is the point of
/// the list rather than decoration.
/// </summary>
public partial class BackgroundPickerWindow : Window
{
    public sealed class Row
    {
        public required int Lo16 { get; init; }
        public string Address => $"${Lo16:X4}";
        public required int Page { get; init; }
        public required string UsedBy { get; init; }

        /// <summary>The one currently in use is marked, so "Use" on it is visibly a no-op.</summary>
        public required bool Current { get; init; }
        public IBrush Highlight => Current ? UiColors.Selection : Brushes.White;
    }

    /// <summary>The chosen address, or null when cancelled.</summary>
    public int? Picked { get; private set; }

    private ListBox list = null!;

    public BackgroundPickerWindow() => AvaloniaXamlLoader.Load(this);

    internal BackgroundPickerWindow(
        IReadOnlyList<(int Lo16, int Page, IReadOnlyList<int> Levels)> backgrounds, int? current) : this()
    {
        list = this.GetControl<ListBox>("List");
        var rows = backgrounds.Select(b => new Row
        {
            Lo16 = b.Lo16,
            Page = b.Page,
            // The first few sharers are enough to recognise which background this is.
            UsedBy = string.Join(" ", b.Levels.Take(8).Select(l => $"{l:X3}"))
                   + (b.Levels.Count > 8 ? $" +{b.Levels.Count - 8}" : ""),
            Current = b.Lo16 == current,
        }).ToList();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(r => r.Current) ?? rows.FirstOrDefault();
    }

    private void OnUse(object? sender, RoutedEventArgs e)
    {
        if (list.SelectedItem is not Row r) return;
        Picked = r.Lo16;
        Close();
    }

    private void OnUse(object? sender, TappedEventArgs e) => OnUse(sender, (RoutedEventArgs)e);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
