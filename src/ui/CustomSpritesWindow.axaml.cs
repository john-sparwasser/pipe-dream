using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// The project's custom sprites (reference/PIXI_PORT.md): the library and the slots each sprite
/// runs in. New adds one from the template, Import adds .json/.cfg sprites or a whole PIXI folder,
/// Slots… says which numbers run it, Edit ASM opens <see cref="BlockEditWindow"/> on the ASM and
/// Edit config <see cref="SpriteConfigWindow"/> on the PIXI config, Open hands the ASM to the user's text editor, Remove deletes it.
/// </summary>
public partial class CustomSpritesWindow : Window
{
    public sealed record Row(string Name, string Kind, string Slots, string Description);

    private readonly EditorSession session = null!;
    private ListBox list = null!;
    private ComboBox kindBox = null!;
    private Button removeButton = null!, slotsButton = null!, editButton = null!, configButton = null!, openButton = null!;
    private TextBlock hint = null!;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public CustomSpritesWindow() => AvaloniaXamlLoader.Load(this);

    internal CustomSpritesWindow(EditorSession session) : this()
    {
        this.session = session;
        list = this.GetControl<ListBox>("List");
        kindBox = this.GetControl<ComboBox>("KindBox");
        removeButton = this.GetControl<Button>("RemoveButton");
        slotsButton = this.GetControl<Button>("SlotsButton");
        editButton = this.GetControl<Button>("EditButton");
        configButton = this.GetControl<Button>("ConfigButton");
        openButton = this.GetControl<Button>("OpenButton");
        openButton.Content = $"Open in {session.TextEditorName ?? "editor"}";
        hint = this.GetControl<TextBlock>("Hint");
        Refresh();
    }

    private string? Selected => (list.SelectedItem as Row)?.Name;

    /// <summary>Refill the list, landing on <paramref name="want"/> if it is there.</summary>
    private void Refresh(string? want = null)
    {
        var rows = session.CustomSprites.Select(s => new Row(s.Name, s.Kind,
            s.Slots.Count == 0 ? "no slot" : string.Join(", ", s.Slots), s.Description)).ToList();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(r => r.Name == want);
        if (rows.Count == 0) hint.Text = "no custom sprites yet — New… starts one, Import PIXI folder… brings a whole PIXI setup";
        UpdateButtons();
    }

    private void OnSelect(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        removeButton.IsEnabled = slotsButton.IsEnabled = editButton.IsEnabled = openButton.IsEnabled = list.SelectedIndex >= 0;
        configButton.IsEnabled = list.SelectedItem is Row r && PixiImport.HasConfig(r.Kind);
    }

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        string kind = (kindBox.SelectedItem as ComboBoxItem)?.Content as string ?? "sprite";
        var ask = new TextPromptWindow($"Name for the new {kind}", "");
        await ask.ShowDialog(this);
        if (ask.Result is not { } name) return;
        hint.Text = session.NewCustomSprite(name, kind, out var made);
        Refresh(made ?? Selected);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add custom sprites to the project", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PIXI sprite") { Patterns = ["*.json", "*.cfg", "*.asm"] }],
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
            {
                hint.Text = session.ImportCustomSpriteFile(path, out var made);
                last = made ?? last;
            }
        Refresh(last ?? Selected);
    }

    private async void OnImportFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pick PIXI's folder (the one with list.txt)" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dir) return;
        hint.Text = session.ImportPixiFolder(dir);
        Refresh(Selected);
    }

    /// <summary>A removed sprite's ASM, config and slots are gone from the project, so it asks first.</summary>
    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } name) return;
        var ask = new ConfirmWindow("Remove sprite", $"Remove {name} from the project? Its ASM, config, included files and slots go with it.", "Remove");
        await ask.ShowDialog(this);
        if (!ask.Confirmed) return;
        hint.Text = session.DeleteCustomSprite(name);
        Refresh();
    }

    private async void OnSlots(object? sender, RoutedEventArgs e)
    {
        if (list.SelectedItem is not Row row) return;
        var ask = new TextPromptWindow(!PixiImport.HasConfig(row.Kind) ? $"Slots for {row.Name} — e.g. {row.Kind} 01 (empty: none)" : $"Slots for {row.Name} — e.g. 1A, 105:B0 (empty: none)", row.Slots == "no slot" ? "" : row.Slots);
        await ask.ShowDialog(this);
        if (ask.Result is not { } slots) return;
        hint.Text = session.SetCustomSpriteSlots(row.Name, slots);
        Refresh(row.Name);
    }

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } name) return;
        await new BlockEditWindow(name, session.CustomSpriteSource(name) ?? "", asm => session.SetCustomSpriteSource(name, asm)).ShowDialog(this);
        Refresh(name);
    }

    private void OnEdit(object? sender, Avalonia.Input.TappedEventArgs e) => OnEdit(sender, (RoutedEventArgs)e);

    /// <summary>The config in <see cref="SpriteConfigWindow"/>; one PIXI could not read opens as
    /// text, to fix by hand. A text edit PIXI could not read is refused, and the hint says why.</summary>
    private async void OnConfig(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } name) return;
        if (session.CustomSpriteModel(name) is { } model)
        {
            await new SpriteConfigWindow(session, name, model).ShowDialog(this);
            Refresh(name);
            return;
        }
        string? status = null;
        await new BlockEditWindow(name + " config", session.CustomSpriteConfig(name) ?? "",
                                  json => status = session.SetCustomSpriteConfig(name, json)).ShowDialog(this);
        Refresh(name);
        if (status is not null) hint.Text = status;
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } name) hint.Text = session.OpenCustomSpriteExternally(name);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
