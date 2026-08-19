using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// Pick a GFX file by sight and name instead of by hex id. The same window serves both uses the
/// ImGui modal did: "assign this to a VRAM bin" and "open this in the tile editor" — the caller
/// says which in the purpose line and reads <see cref="Picked"/> afterwards.
/// </summary>
public partial class GfxBrowserWindow : Window
{
    /// <summary>A file as the list draws it: the service's facts plus a bitmap.</summary>
    public sealed class Row(GfxFileInfo info)
    {
        public int Id => info.Id;
        public bool Imported => info.Imported;
        public string Label => info.Label;
        public string? Name => info.Name;
        public bool HasName => info.Name is { Length: > 0 };
        public string Description => info.Description;

        // Scaled to fit 128 across, aspect kept: a short partial file stays short rather than
        // being stretched to look like a full sheet.
        public double ThumbW { get; } = info.Sheet.W > 0 ? info.Sheet.W * Math.Min(2.0, 128.0 / info.Sheet.W) : 16;
        public double ThumbH { get; } = info.Sheet.W > 0 ? info.Sheet.H * Math.Min(2.0, 128.0 / info.Sheet.W) : 16;

        public Bitmap? Thumb { get; } = info.Sheet.Px.Length > 0
            ? LevelBitmap.FromPixels(info.Sheet.Px, info.Sheet.W, info.Sheet.H) : null;
    }

    /// <summary>The chosen file id, or null when the window was cancelled.</summary>
    public int? Picked { get; private set; }

    private readonly EditorSession session = null!;
    private ListBox files = null!;
    private TextBox filter = null!;
    private CheckBox includeStock = null!;
    private TextBlock renameHint = null!;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public GfxBrowserWindow() => AvaloniaXamlLoader.Load(this);

    internal GfxBrowserWindow(EditorSession session, string purpose) : this()
    {
        this.session = session;
        files = this.GetControl<ListBox>("Files");
        filter = this.GetControl<TextBox>("Filter");
        includeStock = this.GetControl<CheckBox>("IncludeStock");
        renameHint = this.GetControl<TextBlock>("RenameHint");
        this.GetControl<TextBlock>("Purpose").Text = purpose;

        filter.TextChanged += (_, _) => Refresh();
        includeStock.IsCheckedChanged += (_, _) => Refresh();
        files.SelectionChanged += (_, _) => renameHint.Text =
            files.SelectedItem is Row { Imported: true, HasName: false } ? "unnamed" : "";
        Refresh();
    }

    private void Refresh()
    {
        int? keep = (files.SelectedItem as Row)?.Id;
        var rows = session.GfxFiles(includeStock.IsChecked == true, filter.Text ?? "")
                          .Select(i => new Row(i)).ToList();
        files.ItemsSource = rows;
        files.SelectedItem = rows.FirstOrDefault(r => r.Id == keep) ?? rows.FirstOrDefault();
        if (rows.Count == 0)
            renameHint.Text = filter.Text is { Length: > 0 }
                ? "nothing matches that filter"
                : "no custom GFX imported yet — use Import… in the GFX tab";
    }

    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (files.SelectedItem is not Row r) return;
        Picked = r.Id;
        Close();
    }

    /// <summary>Double-click a row to take it, the way the ImGui version made the thumbnail
    /// itself the pick target.</summary>
    private void OnSelect(object? sender, TappedEventArgs e) => OnSelect(sender, (RoutedEventArgs)e);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Rename an import in place. Only imports: vanilla ships no label table for its own
    /// files, so there is nothing to rename and nothing truthful to invent.</summary>
    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (files.SelectedItem is not Row r) return;
        var dlg = new TextPromptWindow($"Name for GFX{r.Id:X3}", r.Name ?? "");
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } name) return;
        if (!session.RenameGfx(r.Id, name)) { renameHint.Text = "stock files have no name"; return; }
        Refresh();
    }
}
