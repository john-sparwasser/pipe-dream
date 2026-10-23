using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>Where an UberASM file goes: one of UberASM Tool's folders, picked by what it is, and its name. <see cref="Result"/> is
/// the path ("level/waves.asm"), null when cancelled.</summary>
public partial class UberFileWindow : Window
{
    /// <summary>The folders, what each is shown as, and what goes in it.</summary>
    private static readonly (string Folder, string Label, string About)[] Kinds =
    [
        ("level", "Level code", "Runs in the levels you give it (Run in…): load, init, main, end, nmi."),
        ("gamemode", "Game mode code", "Runs in game modes by number: init, main, end, nmi."),
        ("overworld", "Overworld code", "Runs on overworld maps 0-6: init, main, end, nmi."),
        ("library", "Library", "Shared code or data every file can call: math.asm's sqrt is math_sqrt."),
        ("routines", "Shared routine", "One routine per file, called with %UberRoutine(name), put in only when used."),
        ("other", "Other", "The global code, status bar and macro library files."),
    ];

    public string? Result { get; private set; }

    private ComboBox folder = null!;
    private TextBox name = null!;

    public UberFileWindow() => AvaloniaXamlLoader.Load(this);

    /// <param name="fileName">An imported file's name, which stays as it is; null for a new file, named here.</param>
    public UberFileWindow(string title, string? fileName = null) : this()
    {
        Title = title;
        folder = this.GetControl<ComboBox>("FolderBox");
        name = this.GetControl<TextBox>("NameBox");
        folder.ItemsSource = Kinds.Select(k => k.Label).ToList();
        folder.SelectedIndex = 0;
        if (fileName is not null) { name.Text = fileName; name.IsReadOnly = true; }
        Opened += (_, _) => (fileName is null ? (Control)name : folder).Focus();
    }

    private void OnFolder(object? sender, SelectionChangedEventArgs e)
    {
        if (folder is not null) this.GetControl<TextBlock>("About").Text = Kinds[Math.Max(0, folder.SelectedIndex)].About;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        string n = (name.Text ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (n.Length == 0) { name.Focus(); return; }
        Result = $"{Kinds[Math.Max(0, folder.SelectedIndex)].Folder}/{n}";
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
