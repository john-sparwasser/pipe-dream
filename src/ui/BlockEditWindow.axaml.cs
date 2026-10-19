using System.Xml;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>One file of ASM — a library block from the Blocks window, or a patch from the Patches
/// window: AvaloniaEdit with line numbers, <see cref="Highlighting"/>'s colours and red squiggles
/// under syntax errors. Save writes it back into the project; Cancel, Esc and closing the window
/// drop the edit.</summary>
public partial class BlockEditWindow : Window
{
    /// <summary>Asar's syntax colours, from ui/Asar.xshd; read once.</summary>
    internal static IHighlightingDefinition Highlighting => highlighting ??= Load();
    private static IHighlightingDefinition? highlighting;

    private static IHighlightingDefinition Load()
    {
        using var s = typeof(BlockEditWindow).Assembly.GetManifestResourceStream("Asar.xshd")!;
        using var r = XmlReader.Create(s);
        return HighlightingLoader.Load(r, HighlightingManager.Instance);
    }

    private readonly Action<string> save = _ => { };
    private TextEditor editor = null!;
    /// <summary>The syntax squiggles (<see cref="AsmLint"/>); a hint, so Save is never blocked by them.</summary>
    internal LintSquiggles Lint { get; private set; } = null!;

    public BlockEditWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>A library block's ASM.</summary>
    internal BlockEditWindow(EditorSession session, string name)
        : this(name, session.BlockSource(name) ?? "", asm => session.SetBlockSource(name, asm)) { }

    /// <summary><paramref name="text"/> under <paramref name="title"/>; Save hands the edit to
    /// <paramref name="save"/>.</summary>
    internal BlockEditWindow(string title, string text, Action<string> save) : this()
    {
        this.save = save;
        Title = title;
        editor = this.GetControl<TextEditor>("Editor");
        editor.SyntaxHighlighting = Highlighting;
        editor.Options.ConvertTabsToSpaces = false;
        editor.Options.HighlightCurrentLine = true;
        foreach (var m in editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>())
            m.Margin = new Avalonia.Thickness(6, 0, 10, 0);                   // air between the numbers and the code
        editor.Text = text;
        var problems = this.GetControl<TextBlock>("Problems");
        Lint = new LintSquiggles(editor);
        void Count()
        {
            problems.Text = Lint.Issues.Count switch { 0 => "", 1 => "1 problem", int n => $"{n} problems" };
            // Hovering the count lists them all, by line.
            ToolTip.SetTip(problems, Lint.Issues.Count == 0 ? null : string.Join("\n", Lint.Issues.Select(i =>
                $"line {editor.Document.GetLineByOffset(Math.Min(i.Offset, editor.Document.TextLength)).LineNumber}: {i.Message}")));
        }
        Lint.Checked += Count;
        Count();
        Opened += (_, _) => editor.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        save(editor.Text ?? "");
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
