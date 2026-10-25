using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// The project's UberASM (reference/UBERASM_PORT.md): its files, with where the list runs each, and the list itself per kind —
/// levels, game modes, overworld maps — as list.txt lines. New and Import add files, Edit opens <see cref="BlockEditWindow"/>
/// (Asar colouring), and a line is added or edited in list.txt's own syntax. Level Properties edits the current level's line too.
/// </summary>
public partial class UberAsmWindow : Window
{
    /// <summary><paramref name="Sa1"/>: its SA-1 badge (reference/SA1_PORT.md, phase 4), <paramref name="Sa1Tip"/> why.</summary>
    public sealed record FileRow(string Path, string RunsIn, string Size, string? Sa1 = null, string? Sa1Tip = null);
    public sealed record LineRow(int Number, string Where, string Line);

    private static readonly string[] Kinds = ["level", "gamemode", "overworld"];

    private readonly EditorSession session = null!;
    private ListBox files = null!, lines = null!;
    private ComboBox kind = null!;
    private TextBlock hint = null!;

    public UberAsmWindow() => AvaloniaXamlLoader.Load(this);

    internal UberAsmWindow(EditorSession session) : this()
    {
        this.session = session;
        files = this.GetControl<ListBox>("FileList");
        lines = this.GetControl<ListBox>("LineList");
        kind = this.GetControl<ComboBox>("KindBox");
        hint = this.GetControl<TextBlock>("Hint");
        Refresh();
    }

    private string Kind => Kinds[Math.Max(0, kind.SelectedIndex)];
    private string? SelectedFile => (files.SelectedItem as FileRow)?.Path;
    private LineRow? SelectedLine => lines.SelectedItem as LineRow;

    private void Refresh(string? file = null, int? line = null)
    {
        var fileRows = session.UberFiles.Select(f => new FileRow(f.Path, f.RunsIn, $"{f.Bytes:N0} bytes", f.Sa1, f.Sa1Tip)).ToList();
        files.ItemsSource = fileRows;
        files.SelectedItem = fileRows.FirstOrDefault(r => r.Path == (file ?? SelectedFile));
        var lineRows = session.UberNumbers(Kind).Select(n => new LineRow(n, Where(n), session.UberLine(Kind, n))).ToList();
        lines.ItemsSource = lineRows;
        lines.SelectedItem = lineRows.FirstOrDefault(r => r.Number == line);
        UpdateButtons();
    }

    private static string Where(int number) => number == UberEntry.All ? "*" : number.ToString("X");

    private void OnSelect(object? sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void OnKind(object? sender, SelectionChangedEventArgs e) { if (lines is not null) Refresh(); }

    private void UpdateButtons()
    {
        this.GetControl<Button>("EditButton").IsEnabled = SelectedFile is { } f && f.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);
        this.GetControl<Button>("DeleteButton").IsEnabled = SelectedFile is not null;
        this.GetControl<Button>("RunInButton").IsEnabled = SelectedFile?.Split('/')[0] is "level" or "gamemode" or "overworld";
        this.GetControl<Button>("EditLineButton").IsEnabled = this.GetControl<Button>("RemoveLineButton").IsEnabled = SelectedLine is not null;
    }

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        var ask = new UberFileWindow("New UberASM file");
        await ask.ShowDialog(this);
        if (ask.Result is not { } path) return;
        hint.Text = session.NewUberFile(path, out var made);
        Refresh(made);
    }

    private async void OnImportFile(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add a file to the project's UberASM" });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } file) return;
        var ask = new UberFileWindow("Import as", Path.GetFileName(file));
        await ask.ShowDialog(this);
        if (ask.Result is not { } path) return;
        hint.Text = session.ImportUberFile(file, path.Split('/')[0], out var made);
        Refresh(made);
    }

    private async void OnImportFolder(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "UberASM Tool folder (the one with list.txt)" });
        if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not { } dir) return;
        hint.Text = session.ImportUberFolder(dir);
        Refresh();
    }

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } path || !path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) return;
        await new BlockEditWindow(path, session.UberText(path) ?? "", text => session.SetUberText(path, text)).ShowDialog(this);
        Refresh(path);
    }

    /// <summary>A deleted file's source is gone from the project, and from every line that ran it, so it asks first.</summary>
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } path) return;
        var ask = new ConfirmWindow("Delete file", $"Delete {path} from the project? Lines that run it lose it.", "Delete");
        await ask.ShowDialog(this);
        if (!ask.Confirmed) return;
        hint.Text = session.DeleteUberFile(path);
        Refresh();
    }

    private async void OnRunIn(object? sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } path) return;
        string kind = path.Split('/')[0] switch { "level" => "levels", "gamemode" => "game modes", _ => "maps" };
        var ask = new TextPromptWindow($"Which {kind} run {path}? A number (hex), several (105 106), or * for all", "");
        await ask.ShowDialog(this);
        if (ask.Result is not { } numbers) return;
        hint.Text = session.RunUberFileIn(path, numbers);
        kind = path.Split('/')[0];
        this.GetControl<ComboBox>("KindBox").SelectedIndex = Array.IndexOf(Kinds, kind);
        Refresh(path);
    }

    private async void OnAddLine(object? sender, RoutedEventArgs e)
    {
        var ask = new TextPromptWindow($"A {Kind} number (hex, or *) and what it runs, as list.txt has it", "* ");
        await ask.ShowDialog(this);
        if (ask.Result?.Trim() is not { Length: > 0 } text) return;
        int space = text.IndexOfAny([' ', '\t']);
        string number = space < 0 ? text : text[..space];
        int? n = number == "*" ? UberEntry.All : int.TryParse(number, System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : null;
        if (n is null) { hint.Text = $"\"{number}\" is not a number or *"; return; }
        if (space < 0) { hint.Text = $"{number} runs what? Put the file after the number: {number} test.asm (or select the file above and use Run in…)"; return; }
        hint.Text = session.SetUberLine(Kind, n.Value, space < 0 ? "" : text[(space + 1)..]);
        Refresh(line: n);
    }

    private async void OnEditLine(object? sender, RoutedEventArgs e)
    {
        if (SelectedLine is not { } row) return;
        var ask = new TextPromptWindow($"What {Kind} {row.Where} runs, as list.txt has it", row.Line);
        await ask.ShowDialog(this);
        if (ask.Result is not { } text) return;
        hint.Text = session.SetUberLine(Kind, row.Number, text);
        Refresh(line: row.Number);
    }

    private void OnRemoveLine(object? sender, RoutedEventArgs e)
    {
        if (SelectedLine is not { } row) return;
        hint.Text = session.SetUberCalls(Kind, row.Number, []);
        Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
