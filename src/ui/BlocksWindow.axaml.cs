using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// The project's block library (reference/GPS_PORT.md, phase 5): every block ASM kept in the .pdp.
/// New adds one from the template with a name and icon (<see cref="NewBlockWindow"/>), Import adds .asm files to <see cref="ProjectFile.BlockLibrary"/>, Edit opens the selected one in
/// <see cref="BlockEditWindow"/>, Open hands it to the user's text editor (File ▸ Preferences),
/// Remove deletes it once no tile runs it. Which tile runs which block
/// is set from the Map16 editor's Custom button. On a base gps.exe already put blocks on, all of
/// that waits for their GPS folder to be imported (<see cref="EditorSession.BlocksLockedReason"/>).
/// </summary>
public partial class BlocksWindow : Window
{
    /// <summary><paramref name="Sa1"/>: its SA-1 badge (reference/SA1_PORT.md, phase 4), <paramref name="Sa1Tip"/> why.</summary>
    public sealed record LibraryRow(string Name, string Note, Avalonia.Media.Imaging.Bitmap? Icon = null, string? Description = null,
                                    string? Sa1 = null, string? Sa1Tip = null);

    private readonly EditorSession session = null!;
    private ListBox library = null!;
    private Button removeButton = null!, editButton = null!, openButton = null!, newButton = null!, importButton = null!;
    private TextBlock hint = null!;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public BlocksWindow() => AvaloniaXamlLoader.Load(this);

    internal BlocksWindow(EditorSession session) : this()
    {
        this.session = session;
        library = this.GetControl<ListBox>("Library");
        removeButton = this.GetControl<Button>("RemoveButton");
        editButton = this.GetControl<Button>("EditButton");
        openButton = this.GetControl<Button>("OpenButton");
        openButton.Content = $"Open in {session.TextEditorName ?? "editor"}";
        newButton = this.GetControl<Button>("NewButton");
        importButton = this.GetControl<Button>("ImportButton");
        hint = this.GetControl<TextBlock>("Hint");
        Refresh();
    }

    private bool Locked => session.BlocksLockedReason is not null;

    private string? Selected => (library.SelectedItem as LibraryRow)?.Name;

    /// <summary>Refill the library, landing on <paramref name="want"/> if it is there.</summary>
    private void Refresh(string? want = null)
    {
        var used = session.BlockEntries.GroupBy(e => e.File).ToDictionary(g => g.Key, g => g.Count());
        var rows = session.BlockNames.Select(n =>
        {
            var (sa1, tip) = session.BlockSa1(n);
            return new LibraryRow(n, used.TryGetValue(n, out int c) ? (c == 1 ? "on 1 tile" : $"on {c} tiles") : "unused",
                NewBlockWindow.IconFor(session, session.BlockIcon(n)), session.BlockDescription(n), sa1, tip);
        }).ToList();
        library.ItemsSource = rows;
        library.SelectedItem = rows.FirstOrDefault(r => r.Name == want);
        if (rows.Count == 0) hint.Text = "no blocks yet — New… starts one, Import… adds .asm files";
        var locked = session.BlocksLockedReason;
        this.GetControl<Border>("LockNotice").IsVisible = locked is not null;
        this.GetControl<TextBlock>("LockText").Text = locked;
        newButton.IsEnabled = importButton.IsEnabled = locked is null;
        UpdateButtons();
    }

    private void OnSelect(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
        => removeButton.IsEnabled = editButton.IsEnabled = openButton.IsEnabled = Selected is not null && !Locked;

    /// <summary>A GPS folder: driver, routines, blocks and list.txt, into the project. On a base GPS
    /// already put blocks on, this is what lets them be managed here.</summary>
    private async void OnImportGps(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the GPS folder (the one with list.txt)", AllowMultiple = false,
        });
        if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not { } folder) return;
        hint.Text = session.ImportGps(folder);
        Refresh(Selected);
    }

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        var w = new NewBlockWindow(session);
        await w.ShowDialog(this);
        if (w.Made is not { } made) return;
        hint.Text = session.Status;
        Refresh(made);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add block .asm files to the library", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Block ASM") { Patterns = ["*.asm"] }],
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
            {
                hint.Text = session.ImportBlockFile(path, out var made);
                last = made ?? last;
            }
        Refresh(last ?? Selected);
    }

    /// <summary>A removed block's ASM is gone from the project, so it asks first.</summary>
    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } name) return;
        var ask = new ConfirmWindow("Remove block", $"Remove {name} from the library? Its ASM goes with it.", "Remove");
        await ask.ShowDialog(this);
        if (!ask.Confirmed) return;
        hint.Text = session.DeleteBlock(name);
        Refresh(name);                               // still selected if it was refused
    }

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } name) return;
        await new BlockEditWindow(session, name).ShowDialog(this);
        Refresh(name);
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } name) hint.Text = session.OpenBlockExternally(name);
    }

    private void OnEdit(object? sender, Avalonia.Input.TappedEventArgs e) => OnEdit(sender, (RoutedEventArgs)e);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
