using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PipeDream.Music;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// The project's AddmusicK-style music (reference/AMK_PORT.md): its songs, with the number each plays
/// under and what it compiles to, its sound effects, and its samples. New and Import add songs, Edit opens
/// <see cref="SongEditWindow"/>, Number sets the slot, and the folder buttons read and write an AddmusicK
/// folder whole. Each button says what happened on the status line.
/// </summary>
public partial class MusicWindow : Window
{
    public sealed record SongRow(string Path, string Number, string Size);
    public sealed record SampleRow(string Path, string Size);
    public sealed record EffectRow(string Port, int Slot, string File, string Size)
    {
        public string Where => $"{Port} {Slot:X2}";
    }

    /// <summary>Where a song's data would start in the ARAM — only for the size, which does not depend on it.</summary>
    internal const int SizeBase = 0x238D;

    private readonly EditorSession session = null!;
    private ListBox songs = null!, samples = null!, effects = null!;
    private Button editButton = null!, numberButton = null!, deleteButton = null!, deleteSampleButton = null!;
    private Button editEffectButton = null!, deleteEffectButton = null!;
    private TextBlock hint = null!;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public MusicWindow() => AvaloniaXamlLoader.Load(this);

    internal MusicWindow(EditorSession session) : this()
    {
        this.session = session;
        songs = this.GetControl<ListBox>("SongList");
        samples = this.GetControl<ListBox>("SampleList");
        editButton = this.GetControl<Button>("EditButton");
        numberButton = this.GetControl<Button>("NumberButton");
        deleteButton = this.GetControl<Button>("DeleteButton");
        deleteSampleButton = this.GetControl<Button>("DeleteSampleButton");
        effects = this.GetControl<ListBox>("EffectList");
        editEffectButton = this.GetControl<Button>("EditEffectButton");
        deleteEffectButton = this.GetControl<Button>("DeleteEffectButton");
        hint = this.GetControl<TextBlock>("Hint");
        Refresh();
    }

    private string? SelectedSong => (songs.SelectedItem as SongRow)?.Path;
    private string? SelectedSample => (samples.SelectedItem as SampleRow)?.Path;
    private EffectRow? SelectedEffect => effects.SelectedItem as EffectRow;

    /// <summary>Refill both lists, landing on <paramref name="song"/> and <paramref name="sample"/> if they are there.</summary>
    private void Refresh(string? song = null, string? sample = null)
    {
        var songRows = session.MusicSongs.Select(s => new SongRow(s.Path, s.Number, SizeOf(s.Path))).ToList();
        songs.ItemsSource = songRows;
        songs.SelectedItem = songRows.FirstOrDefault(r => r.Path == song);
        var sampleRows = session.MusicSamples.Select(s => new SampleRow(s.Path, $"{s.Bytes:N0} bytes")).ToList();
        samples.ItemsSource = sampleRows;
        samples.SelectedItem = sampleRows.FirstOrDefault(r => r.Path == sample);
        var keep = SelectedEffect;
        var effectRows = session.MusicEffects.Select(x => new EffectRow(x.Port, x.Slot, x.File, x.Size)).ToList();
        effects.ItemsSource = effectRows;
        effects.SelectedItem = effectRows.FirstOrDefault(r => r.Port == keep?.Port && r.Slot == keep.Slot);
        UpdateButtons();
    }

    /// <summary>What the song compiles to, or "error" when it does not compile.</summary>
    internal static string SizeOf(EditorSession session, string path)
    {
        var compiled = MmlCompiler.Compile(MmlParser.Parse(session.SongText(path) ?? ""), SizeBase);
        return compiled.Ok ? $"{compiled.Bytes.Length:N0} bytes" : "error";
    }

    private string SizeOf(string path) => SizeOf(session, path);

    private void OnSelect(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        editButton.IsEnabled = numberButton.IsEnabled = deleteButton.IsEnabled = SelectedSong is not null;
        deleteSampleButton.IsEnabled = SelectedSample is not null;
        editEffectButton.IsEnabled = deleteEffectButton.IsEnabled = SelectedEffect is not null;
    }

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        var ask = new TextPromptWindow("Name for the new song", "");
        await ask.ShowDialog(this);
        if (ask.Result is not { } name) return;
        hint.Text = session.NewSong(name, out var made);
        Refresh(made ?? SelectedSong, SelectedSample);
    }

    private async void OnImportSong(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add songs to the project", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("AddmusicK song") { Patterns = ["*.txt", "*.mml"] }],
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
            {
                hint.Text = session.ImportSongFile(path, out var made);
                last = made ?? last;
            }
        Refresh(last ?? SelectedSong, SelectedSample);
    }

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (SelectedSong is not { } path) return;
        await new SongEditWindow(session, path).ShowDialog(this);
        Refresh(path, SelectedSample);
    }

    private async void OnNumber(object? sender, RoutedEventArgs e)
    {
        if (SelectedSong is not { } path) return;
        var current = session.MusicSongs.First(s => s.Path == path).Number;
        var ask = new SongNumberWindow(ParseNumber(current));
        await ask.ShowDialog(this);
        if (ask.Result is { } r) hint.Text = session.SetSongNumber(path, r.Number, r.Global);
        else if (ask.Cleared) hint.Text = session.ClearSongNumber(path);
        Refresh(path, SelectedSample);
    }

    /// <summary>The first slot of a <see cref="EditorSession.SongInfo.Number"/> ("global 01, local 35"), if any.</summary>
    private static (int, bool)? ParseNumber(string slots)
    {
        var first = slots.Split(',')[0].Split(' ');
        return first.Length == 2 ? (Convert.ToInt32(first[1], 16), first[0] == "global") : null;
    }

    /// <summary>A deleted song's MML is gone from the project, so it asks first.</summary>
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectedSong is not { } path) return;
        var ask = new ConfirmWindow("Delete song", $"Delete {path} from the project? Its MML and number go with it.", "Delete");
        await ask.ShowDialog(this);
        if (!ask.Confirmed) return;
        hint.Text = session.DeleteSong(path);
        Refresh(null, SelectedSample);
    }

    private async void OnImportFolder(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "AddmusicK folder (the one with Addmusic_list.txt)" });
        if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not { } dir) return;
        hint.Text = session.ImportAmkFolder(dir);
        Refresh(SelectedSong, SelectedSample);
    }

    private async void OnExportFolder(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Write the AddmusicK folder to" });
        if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not { } dir) return;
        hint.Text = session.ExportAmkFolder(dir);
    }

    private async void OnImportSample(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add samples to the project", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("BRR sample or bank") { Patterns = ["*.brr", "*.bnk"] }],
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
            {
                hint.Text = session.ImportSampleFile(path, out var made);
                last = made ?? last;
            }
        Refresh(SelectedSong, last ?? SelectedSample);
    }

    private void OnDeleteSample(object? sender, RoutedEventArgs e)
    {
        if (SelectedSample is not { } path) return;
        hint.Text = session.DeleteSample(path);
        Refresh(SelectedSong);
    }

    private async void OnImportEffect(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a sound effect", FileTypeFilter = [new FilePickerFileType("AddmusicK sound effect") { Patterns = ["*.txt"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var ask = new TextPromptWindow("Port and slot, e.g. 1DF9 22", "1DF9 ");
        await ask.ShowDialog(this);
        if (ask.Result?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var port, var slot]
            || !int.TryParse(slot, System.Globalization.NumberStyles.HexNumber, null, out int n))
        { hint.Text = "a port (1DF9 or 1DFC) and a hex slot, e.g. 1DF9 22"; return; }
        hint.Text = session.ImportEffectFile(path, port.ToUpperInvariant(), n);
        Refresh(SelectedSong, SelectedSample);
    }

    private async void OnEditEffect(object? sender, RoutedEventArgs e)
    {
        if (SelectedEffect is not { } fx) return;
        await new SongEditWindow(session, fx.Port, fx.File).ShowDialog(this);
        Refresh(SelectedSong, SelectedSample);
    }

    private void OnDeleteEffect(object? sender, RoutedEventArgs e)
    {
        if (SelectedEffect is not { } fx) return;
        hint.Text = session.DeleteEffect(fx.Port, fx.Slot);
        Refresh(SelectedSong, SelectedSample);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
