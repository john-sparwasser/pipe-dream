using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// The project's Asar patches (reference/PATCHES.md), in the order the build applies them. Each
/// row's checkbox turns a patch on or off; New adds one from the template, Import adds .asm files
/// with what they include, Edit opens <see cref="BlockEditWindow"/>, Open hands it to the user's
/// text editor, Remove deletes it, and Move up / Move down change when it applies.
/// </summary>
public partial class PatchesWindow : Window
{
    /// <summary><paramref name="Sa1"/>: its SA-1 badge (reference/SA1_PORT.md, phase 4), <paramref name="Sa1Tip"/> why.</summary>
    public sealed record Row(string Name, bool Enabled, string? Description, string Note, string? Sa1 = null, string? Sa1Tip = null);

    private readonly EditorSession session = null!;
    private ListBox list = null!;
    private Button removeButton = null!, editButton = null!, openButton = null!, upButton = null!, downButton = null!;
    private TextBlock hint = null!;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public PatchesWindow() => AvaloniaXamlLoader.Load(this);

    internal PatchesWindow(EditorSession session) : this()
    {
        this.session = session;
        list = this.GetControl<ListBox>("List");
        removeButton = this.GetControl<Button>("RemoveButton");
        editButton = this.GetControl<Button>("EditButton");
        openButton = this.GetControl<Button>("OpenButton");
        openButton.Content = $"Open in {session.TextEditorName ?? "editor"}";
        upButton = this.GetControl<Button>("UpButton");
        downButton = this.GetControl<Button>("DownButton");
        hint = this.GetControl<TextBlock>("Hint");
        // The rows' checkboxes are in a template, so their clicks are caught here as they bubble.
        list.AddHandler(Button.ClickEvent, OnToggle);
        Refresh();
    }

    private string? Selected => (list.SelectedItem as Row)?.Name;

    /// <summary>Refill the list, landing on <paramref name="want"/> if it is there.</summary>
    private void Refresh(string? want = null)
    {
        var rows = session.Patches.Select(p => new Row(p.Name, p.Enabled, p.Description,
            p.Files switch { 0 => "", 1 => "1 file", int n => $"{n} files" }, p.Sa1, p.Sa1Tip)).ToList();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(r => r.Name == want);
        if (rows.Count == 0) hint.Text = "no patches yet — New… starts one, Import… adds .asm files";
        UpdateButtons();
    }

    private void OnSelect(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        int at = list.SelectedIndex, count = list.ItemCount;
        removeButton.IsEnabled = editButton.IsEnabled = openButton.IsEnabled = at >= 0;
        upButton.IsEnabled = at > 0;
        downButton.IsEnabled = at >= 0 && at < count - 1;
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not CheckBox { DataContext: Row row } box) return;
        hint.Text = session.SetPatchEnabled(row.Name, box.IsChecked == true);
        Refresh(Selected);
    }

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        var ask = new TextPromptWindow("Name for the new patch", "");
        await ask.ShowDialog(this);
        if (ask.Result is not { } name) return;
        hint.Text = session.NewPatch(name, out var made);
        Refresh(made ?? Selected);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add .asm patches to the project", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Asar patch") { Patterns = ["*.asm"] }],
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
            {
                hint.Text = session.ImportPatchFile(path, out var made);
                last = made ?? last;
            }
        Refresh(last ?? Selected);
    }

    /// <summary>A removed patch's ASM and files are gone from the project, so it asks first.</summary>
    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } name) return;
        var ask = new ConfirmWindow("Remove patch", $"Remove {name} from the project? Its ASM and the files it includes go with it.", "Remove");
        await ask.ShowDialog(this);
        if (!ask.Confirmed) return;
        hint.Text = session.DeletePatch(name);
        Refresh();
    }

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } name) return;
        await new BlockEditWindow(name, session.PatchSource(name) ?? "", asm => session.SetPatchSource(name, asm)).ShowDialog(this);
        Refresh(name);
    }

    private void OnEdit(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        // A double-click on a row's checkbox is two toggles, not an edit.
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
        OnEdit(sender, (RoutedEventArgs)e);
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } name) hint.Text = session.OpenPatchExternally(name);
    }

    private void OnUp(object? sender, RoutedEventArgs e) => Move(-1);

    private void OnDown(object? sender, RoutedEventArgs e) => Move(1);

    private void Move(int by)
    {
        if (Selected is not { } name) return;
        hint.Text = session.MovePatch(name, by);
        Refresh(name);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
