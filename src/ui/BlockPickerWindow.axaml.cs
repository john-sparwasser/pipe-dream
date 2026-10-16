using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// Pick the custom block for the selected Map16 tile(s): the project's block library, with "none"
/// at the top to take a block away. Opened from the Custom button beside the acts-as field; the
/// caller assigns what <see cref="Picked"/> says. New block adds the template and selects it,
/// so a tile can get a fresh block without a trip to the Blocks window (where Edit opens it).
/// </summary>
public partial class BlockPickerWindow : Window
{
    public sealed class Row(string? file, string note, bool current)
    {
        /// <summary>The library block, or null for "none".</summary>
        public string? File => file;
        public string Name => file ?? "(none)";
        public string Note => note;
        public IBrush Highlight => current ? UiColors.Selection : Brushes.White;
    }

    /// <summary>The chosen block, "" for none, or null when cancelled.</summary>
    public string? Picked { get; private set; }

    private readonly EditorSession session = null!;
    private readonly string? current;
    private ListBox list = null!;
    private TextBox newName = null!;
    private TextBlock hint = null!;

    public BlockPickerWindow() => AvaloniaXamlLoader.Load(this);

    /// <param name="current">The block the first selected tile has now, or null.</param>
    internal BlockPickerWindow(EditorSession session, IReadOnlyList<int> tiles, string? current) : this()
    {
        this.session = session;
        this.current = current;
        list = this.GetControl<ListBox>("List");
        newName = this.GetControl<TextBox>("NewName");
        hint = this.GetControl<TextBlock>("Hint");
        this.GetControl<TextBlock>("Subject").Text = tiles.Count == 1
            ? $"Tile 0x{tiles[0]:X4}" + (current is null ? " has no custom block." : $" runs {current}.")
            : $"{tiles.Count} tiles selected — the block you pick goes on all of them.";
        Refresh(current);
    }

    /// <summary>Refill the list: none first, then the library, landing on <paramref name="want"/>.</summary>
    private void Refresh(string? want)
    {
        var used = session.BlockEntries.GroupBy(e => e.File).ToDictionary(g => g.Key, g => g.Count());
        var rows = new List<Row> { new(null, "", current is null) };
        rows.AddRange(session.BlockNames.Select(f =>
            new Row(f, used.TryGetValue(f, out int n) ? (n == 1 ? "on 1 tile" : $"on {n} tiles") : "", f == current)));
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(r => r.File == want) ?? rows[0];
    }

    private void OnUse(object? sender, RoutedEventArgs e)
    {
        if (list.SelectedItem is not Row r) return;
        Picked = r.File ?? "";
        Close();
    }

    private void OnUse(object? sender, TappedEventArgs e) => OnUse(sender, (RoutedEventArgs)e);

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        hint.Text = session.NewBlock(newName.Text ?? "", out var file);
        if (file is null) return;
        newName.Text = "";
        Refresh(file);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
