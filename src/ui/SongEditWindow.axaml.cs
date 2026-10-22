using System.Xml;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using PipeDream.Music;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>One song's MML: AvaloniaEdit with <see cref="Highlighting"/>'s colours, and under it what the
/// compiler says, refreshed a moment after typing stops, with the compiled size once it compiles. Save
/// writes into the project; Cancel, Esc and closing the window drop the edit, as in
/// <see cref="BlockEditWindow"/>.</summary>
public partial class SongEditWindow : Window
{
    public sealed record Problem(int Line, int Column, string Message)
    {
        public string Where => $"{Line}:{Column}";
    }

    /// <summary>MML's syntax colours, from ui/Mml.xshd; read once.</summary>
    internal static IHighlightingDefinition Highlighting => highlighting ??= Load();
    private static IHighlightingDefinition? highlighting;

    private static IHighlightingDefinition Load()
    {
        using var s = typeof(SongEditWindow).Assembly.GetManifestResourceStream("Mml.xshd")!;
        using var r = XmlReader.Create(s);
        return HighlightingLoader.Load(r, HighlightingManager.Instance);
    }

    private readonly EditorSession session = null!;
    private readonly string path = "";
    private TextEditor editor = null!;
    private ListBox problems = null!;
    private TextBlock size = null!;
    private DispatcherTimer? debounce;

    public SongEditWindow() => AvaloniaXamlLoader.Load(this);

    internal SongEditWindow(EditorSession session, string path) : this()
    {
        this.session = session;
        this.path = path;
        Title = path;
        editor = this.GetControl<TextEditor>("Editor");
        problems = this.GetControl<ListBox>("Problems");
        size = this.GetControl<TextBlock>("Size");
        var open = this.GetControl<Button>("OpenButton");
        open.Content = $"Open in {session.TextEditorName ?? "editor"}";
        editor.SyntaxHighlighting = Highlighting;
        editor.Options.ConvertTabsToSpaces = false;
        editor.Options.HighlightCurrentLine = true;
        foreach (var m in editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>())
            m.Margin = new Avalonia.Thickness(6, 0, 10, 0);                   // air between the numbers and the code
        editor.Text = session.SongText(path) ?? "";
        CheckNow();
        editor.TextChanged += (_, _) => Debounce();
        Opened += (_, _) => editor.Focus();
        Closed += (_, _) => debounce?.Stop();
    }

    /// <summary>Wait for a pause in the typing before compiling; a 100 KB song compiles in well under a
    /// second, but not on every keystroke.</summary>
    private void Debounce()
    {
        debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Stop();
        debounce.Tick -= OnDebounced;
        debounce.Tick += OnDebounced;
        debounce.Start();
    }

    private void OnDebounced(object? sender, EventArgs e)
    {
        debounce!.Stop();
        CheckNow();
    }

    /// <summary>Parse and compile the text as it stands, and show the problems or the size.</summary>
    internal void CheckNow()
    {
        var compiled = MmlCompiler.Compile(MmlParser.Parse(editor.Text ?? ""), MusicWindow.SizeBase);
        problems.ItemsSource = compiled.Diagnostics.Select(d => new Problem(d.Line, d.Column, d.Message)).ToList();
        size.Text = compiled.Ok ? $"{compiled.Bytes.Length:N0} bytes" : compiled.Diagnostics.Count switch { 1 => "1 problem", int n => $"{n} problems" };
    }

    private void OnJump(object? sender, RoutedEventArgs e)
    {
        if (problems.SelectedItem is not Problem p) return;
        var line = editor.Document.GetLineByNumber(Math.Clamp(p.Line, 1, editor.Document.LineCount));
        editor.CaretOffset = line.Offset + Math.Clamp(p.Column - 1, 0, line.Length);
        editor.ScrollToLine(line.LineNumber);
        editor.Focus();
    }

    private void OnProblemKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnJump(sender, e);
        e.Handled = true;
    }

    private void OnOpen(object? sender, RoutedEventArgs e) => size.Text = session.OpenSongExternally(path);

    private async void OnExportSpc(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export .spc", SuggestedFileName = Path.ChangeExtension(Path.GetFileName(path), ".spc"),
            FileTypeChoices = [new FilePickerFileType("SPC") { Patterns = ["*.spc"] }],
        });
        if (file?.TryGetLocalPath() is { } to) size.Text = session.ExportSpc(path, editor.Text ?? "", to);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        session.SetSongText(path, editor.Text ?? "");
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
