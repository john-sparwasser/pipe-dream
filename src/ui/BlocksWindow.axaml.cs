using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// The project's custom blocks (reference/GPS_PORT.md, phase 5): the library of block ASM kept in
/// the .pdp, edited here, and which Map16 tile runs which of them. Run writes
/// <see cref="ProjectFile.Blocks"/>; New and Import .asm add to <see cref="ProjectFile.BlockLibrary"/>;
/// Build runs the ordinary build so an assembler error lands in the hint as file:line: message.
/// Acts-like is read, not edited — the Map16 editor owns it.
/// </summary>
public partial class BlocksWindow : Window
{
    public sealed class Row(EditorSession.BlockEntry e)
    {
        public int Tile => e.Tile;
        public string File => e.File;
        public string Slot => $"${e.Tile:X3}";
        public string Acts => e.ActsLike is { } a ? $"acts 0x{a:X3}" : "acts —";
        public string Note => !e.InLibrary ? "not in the library"
            : !e.Built ? "not in the last build"
            : e.Handled.Length > 0 ? string.Join(", ", e.Handled)
            : "no code on any contact";
    }

    public sealed record LibraryRow(string Name, string Note);

    private readonly EditorSession session = null!;
    private ListBox blocks = null!, library = null!;
    private TextBox tileBox = null!, newName = null!, editor = null!;
    private TextBlock hint = null!, editorTitle = null!;
    private Button removeButton = null!, deleteButton = null!;
    /// <summary>The block the editor holds, so its text goes back to the right one.</summary>
    private string? editing;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public BlocksWindow() => AvaloniaXamlLoader.Load(this);

    internal BlocksWindow(EditorSession session) : this()
    {
        this.session = session;
        blocks = this.GetControl<ListBox>("Blocks");
        library = this.GetControl<ListBox>("Library");
        tileBox = this.GetControl<TextBox>("TileBox");
        newName = this.GetControl<TextBox>("NewName");
        editor = this.GetControl<TextBox>("Editor");
        editorTitle = this.GetControl<TextBlock>("EditorTitle");
        removeButton = this.GetControl<Button>("RemoveButton");
        deleteButton = this.GetControl<Button>("DeleteButton");
        hint = this.GetControl<TextBlock>("Hint");
        Closing += (_, _) => CommitEditor();
        RefreshLibrary();
        Refresh();
        if (session.HasProject && !session.HasGpsDriver) hint.Text = "no GPS driver yet — Debug ▸ Import GPS folder… brings main.asm, routines and blocks in";
    }

    // ---- the library ----

    /// <summary>Refill the library, landing on <paramref name="want"/> or where it was.</summary>
    private void RefreshLibrary(string? want = null)
    {
        want ??= editing;
        var used = session.BlockEntries.GroupBy(e => e.File).ToDictionary(g => g.Key, g => g.Count());
        var rows = session.BlockNames.Select(n => new LibraryRow(n,
            used.TryGetValue(n, out int c) ? (c == 1 ? "on 1 tile" : $"on {c} tiles") : "unused")).ToList();
        library.ItemsSource = rows;
        library.SelectedItem = rows.FirstOrDefault(r => r.Name == want) ?? rows.FirstOrDefault();
    }

    /// <summary>Picking a block saves the one being edited and loads the new one.</summary>
    private void OnLibrarySelect(object? sender, SelectionChangedEventArgs e)
    {
        CommitEditor();
        editing = (library.SelectedItem as LibraryRow)?.Name;
        editor.Text = editing is null ? "" : session.BlockSource(editing) ?? "";
        editor.IsEnabled = editing is not null;
        editorTitle.Text = editing ?? "no block selected";
        deleteButton.Content = "Delete";
    }

    private void CommitEditor()
    {
        if (editing is not null) session.SetBlockSource(editing, editor.Text ?? "");
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        hint.Text = session.NewBlock(newName.Text ?? "", out var made);
        if (made is null) return;
        newName.Text = "";
        RefreshLibrary(made);
    }

    private async void OnImportFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a block .asm to the library", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Block ASM") { Patterns = ["*.asm"] }],
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
            {
                hint.Text = session.ImportBlockFile(path, out var made);
                last = made ?? last;
            }
        RefreshLibrary(last);
    }

    /// <summary>Deleting a block's ASM cannot be undone here, so it arms first.</summary>
    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (editing is not { } name) return;
        if (deleteButton.Content as string != "Sure?")
        {
            deleteButton.Content = "Sure?";
            hint.Text = $"deletes {name} from the library";
            return;
        }
        deleteButton.Content = "Delete";
        hint.Text = session.DeleteBlock(name);
        if (session.BlockSource(name) is null) editing = null;
        RefreshLibrary();
    }

    // ---- which tile runs what ----

    /// <summary>Refill the tile list, keeping the selection on <paramref name="want"/> or where it was.</summary>
    private void Refresh(int? want = null)
    {
        int? keep = want ?? (blocks.SelectedItem as Row)?.Tile;
        var rows = session.BlockEntries.Select(e => new Row(e)).ToList();
        blocks.ItemsSource = rows;
        blocks.SelectedItem = rows.FirstOrDefault(r => r.Tile == keep) ?? rows.FirstOrDefault();
        if (rows.Count == 0) hint.Text = "no blocks yet — add one to the library and run it on a tile, or import a GPS folder";
    }

    /// <summary>Selecting a tile loads it into the tile box and its block into the editor.</summary>
    private void OnSelect(object? sender, SelectionChangedEventArgs e)
    {
        if (blocks.SelectedItem is not Row r) return;
        tileBox.Text = $"{r.Tile:X3}";
        RefreshLibrary(r.File);
        removeButton.Content = "Take block off tile";
    }

    private int? TileFromBox()
    {
        string s = (tileBox.Text ?? "").Trim().TrimStart('$');
        if (int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int t)) return t;
        hint.Text = "tile is a hex Map16 number, 200-7FFF";
        return null;
    }

    private void OnAssign(object? sender, RoutedEventArgs e)
    {
        if (TileFromBox() is not { } tile || editing is null) return;
        hint.Text = session.AssignBlock(tile, editing);
        Refresh(tile);
        RefreshLibrary();
    }

    /// <summary>Taking a block off is a reassign away, but it still arms first.</summary>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (blocks.SelectedItem is not Row r) return;
        if (removeButton.Content as string != "Sure?")
        {
            removeButton.Content = "Sure?";
            hint.Text = $"takes {r.File} off ${r.Tile:X3}; the block stays in the library";
            return;
        }
        removeButton.Content = "Take block off tile";
        hint.Text = session.AssignBlock(r.Tile, null);
        Refresh();
        RefreshLibrary();
    }

    private void OnBuild(object? sender, RoutedEventArgs e)
    {
        CommitEditor();
        hint.Text = session.Build();
        Refresh();                                   // the contacts column reads the new build
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
