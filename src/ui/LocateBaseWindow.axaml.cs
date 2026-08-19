using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace PipeDream.Ui;

/// <summary>Ask for the base ROM a project pinned but cannot find. Verification happens in the
/// service — a ROM that only looks right would corrupt every offset the project recorded.</summary>
public partial class LocateBaseWindow : Window
{
    /// <summary>The ROM the user chose, or null when cancelled.</summary>
    public string? Located { get; private set; }

    public LocateBaseWindow() => AvaloniaXamlLoader.Load(this);

    public LocateBaseWindow(string projectName, string problem, string pinned) : this()
    {
        this.GetControl<TextBlock>("Problem").Text = $"'{projectName}': {problem}";
        this.GetControl<TextBlock>("Pinned").Text = $"pinned base: {pinned}";
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate the project's base ROM",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SNES ROM") { Patterns = ["*.smc", "*.sfc"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        Located = path;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
