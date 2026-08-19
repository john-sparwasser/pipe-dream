using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace PipeDream.Ui;

/// <summary>
/// First run: until the config knows where an unedited SMW ROM lives, ask for one. The pick is
/// hash-checked and the verdict shown before anything is saved — a mismatch is allowed, since an
/// LM-prepared base works fully, but it is worth knowing which one you picked.
///
/// Skippable. The editor opens ROMs and projects perfectly well without a configured vanilla
/// image; what it cannot do is PREPARE a new project's base, and the places that need it say so.
/// </summary>
public partial class FirstRunWindow : Window
{
    /// <summary>The chosen path, or null when skipped.</summary>
    public string? Chosen { get; private set; }

    private TextBlock chosenLabel = null!, verdict = null!;
    private Button save = null!;
    private string? picked;

    public FirstRunWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Resolved explicitly, not through the XAML name generator: its fields are only assigned
        // by an InitializeComponent this window does not call, so they would all be null.
        chosenLabel = this.GetControl<TextBlock>("ChosenFile");
        verdict = this.GetControl<TextBlock>("Verdict");
        save = this.GetControl<Button>("SaveButton");
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate an unedited Super Mario World ROM",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SNES ROM") { Patterns = ["*.smc", "*.sfc"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        picked = path;
        chosenLabel.Text = Path.GetFileName(path);
        verdict.Text = EditorSession.DescribeRom(path);
        save.IsEnabled = true;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Chosen = picked;
        Close();
    }

    private void OnSkip(object? sender, RoutedEventArgs e) => Close();
}
