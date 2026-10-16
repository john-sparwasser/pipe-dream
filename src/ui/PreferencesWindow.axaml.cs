using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>File ▸ Preferences: the per-user programs the editor hands things to — the text editor
/// custom blocks open in, and the emulator for Run in emulator (F4). Saved as they are picked.</summary>
public partial class PreferencesWindow : Window
{
    private readonly EditorSession session = null!;
    private TextBox editorPath = null!, emulatorPath = null!;

    public PreferencesWindow() => AvaloniaXamlLoader.Load(this);

    internal PreferencesWindow(EditorSession session) : this()
    {
        this.session = session;
        editorPath = this.GetControl<TextBox>("EditorPath");
        emulatorPath = this.GetControl<TextBox>("EmulatorPath");
        ShowChoices();
    }

    /// <summary>Put the current choices in the boxes; unset reads as what happens instead.</summary>
    private void ShowChoices()
    {
        editorPath.Text = session.TextEditorPath ?? "the OS's program for .asm files";
        emulatorPath.Text = session.EmulatorPath ?? "Mesen, found automatically";
    }

    private async Task<string?> PickProgram(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Program") { Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async void OnBrowseEditor(object? sender, RoutedEventArgs e)
    {
        if (await PickProgram("Choose the text editor custom blocks open in") is not { } p) return;
        session.SetTextEditor(p);
        ShowChoices();
    }

    private void OnDefaultEditor(object? sender, RoutedEventArgs e) { session.SetTextEditor(null); ShowChoices(); }

    private async void OnBrowseEmulator(object? sender, RoutedEventArgs e)
    {
        if (await PickProgram("Choose the emulator for Run in emulator (F4)") is not { } p) return;
        session.SetEmulator(p);
        ShowChoices();
    }

    private void OnDefaultEmulator(object? sender, RoutedEventArgs e) { session.SetEmulator(null); ShowChoices(); }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
