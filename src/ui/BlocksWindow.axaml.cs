using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// The project's custom blocks (reference/GPS_PORT.md, phase 5): which Map16 tile runs which
/// block file. Assign writes <see cref="ProjectFile.Blocks"/>; New block drops a template into
/// gps/blocks/ and opens it; Build runs the ordinary build so an assembler error lands in the
/// hint as file:line: message. Acts-like is read, not edited — the Map16 editor owns it.
/// </summary>
public partial class BlocksWindow : Window
{
    public sealed class Row(EditorSession.BlockEntry e)
    {
        public int Tile => e.Tile;
        public string File => e.File;
        public string Slot => $"${e.Tile:X3}";
        public string Acts => e.ActsLike is { } a ? $"acts 0x{a:X3}" : "acts —";
        public string Note => !e.FileExists ? "file missing"
            : !e.Built ? "not in the last build"
            : e.Handled.Length > 0 ? string.Join(", ", e.Handled)
            : "no code on any contact";
    }

    private readonly EditorSession session = null!;
    private ListBox blocks = null!;
    private TextBox tileBox = null!, newName = null!;
    private ComboBox fileBox = null!;
    private Button removeButton = null!;
    private TextBlock hint = null!;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public BlocksWindow() => AvaloniaXamlLoader.Load(this);

    internal BlocksWindow(EditorSession session) : this()
    {
        this.session = session;
        blocks = this.GetControl<ListBox>("Blocks");
        tileBox = this.GetControl<TextBox>("TileBox");
        fileBox = this.GetControl<ComboBox>("FileBox");
        newName = this.GetControl<TextBox>("NewName");
        removeButton = this.GetControl<Button>("RemoveButton");
        hint = this.GetControl<TextBlock>("Hint");
        PopulateFiles();
        Refresh();
        if (session.HasProject && !session.HasGpsDriver) hint.Text = "no GPS driver yet — Debug ▸ Import GPS folder… brings main.asm, routines and blocks in";
    }

    /// <summary>The file picker lists gps/blocks/; <paramref name="want"/> is the one to land on.</summary>
    private void PopulateFiles(string? want = null)
    {
        want ??= fileBox.SelectedItem as string;
        var files = session.BlockFiles;
        fileBox.ItemsSource = files;
        fileBox.SelectedIndex = Math.Max(0, files.ToList().IndexOf(want ?? ""));
    }

    /// <summary>Refill the list, keeping the selection on <paramref name="want"/> or where it was.</summary>
    private void Refresh(int? want = null)
    {
        int? keep = want ?? (blocks.SelectedItem as Row)?.Tile;
        var rows = session.BlockEntries.Select(e => new Row(e)).ToList();
        blocks.ItemsSource = rows;
        blocks.SelectedItem = rows.FirstOrDefault(r => r.Tile == keep) ?? rows.FirstOrDefault();
        if (rows.Count == 0) hint.Text = "no blocks yet — assign a file to a tile, or import a GPS folder";
    }

    /// <summary>Selecting a row loads it into the assign controls, so Assign re-assigns it.</summary>
    private void OnSelect(object? sender, SelectionChangedEventArgs e)
    {
        if (blocks.SelectedItem is not Row r) return;
        tileBox.Text = $"{r.Tile:X3}";
        PopulateFiles(r.File);
        removeButton.Content = "Remove";
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
        if (TileFromBox() is not { } tile) return;
        hint.Text = session.AssignBlock(tile, fileBox.SelectedItem as string);
        Refresh(tile);
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        hint.Text = session.NewBlock(newName.Text ?? "", out var file);
        if (file is null) return;
        newName.Text = "";
        PopulateFiles(file);
        if (session.OpenBlockFile(file) is { } problem) hint.Text += " — " + problem;
    }

    /// <summary>Removing is a one-step undo away (reassign), but it still arms first.</summary>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (blocks.SelectedItem is not Row r) return;
        if (removeButton.Content as string != "Sure?")
        {
            removeButton.Content = "Sure?";
            hint.Text = $"takes {r.File} off ${r.Tile:X3}; the file stays in gps/blocks/";
            return;
        }
        removeButton.Content = "Remove";
        hint.Text = session.AssignBlock(r.Tile, null);
        Refresh();
    }

    private void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        string? file = (blocks.SelectedItem as Row)?.File ?? fileBox.SelectedItem as string;
        if (file is null) return;
        hint.Text = session.OpenBlockFile(file) ?? $"opened {file}";
    }

    /// <summary>Double-click a row to open its file.</summary>
    private void OnOpenFile(object? sender, TappedEventArgs e) => OnOpenFile(sender, (RoutedEventArgs)e);

    private void OnBuild(object? sender, RoutedEventArgs e)
    {
        hint.Text = session.Build();
        Refresh();                                   // the contacts column reads the new build
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
