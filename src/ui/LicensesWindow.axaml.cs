using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// Help ▸ Third-party licences: the notice for what ships inside the app, then each licence text
/// it names (assets/licenses, embedded). The binary is one file on every platform, so this is
/// where Asar's LGPL travels with it; the Windows installer also puts the files beside the exe.
/// </summary>
public partial class LicensesWindow : Window
{
    /// <summary>The notice first, then the texts in the order it names them.</summary>
    private static readonly string[] Files =
        ["THIRD-PARTY-NOTICES.txt", "Asar-LICENSE.txt", "LGPL-3.0.txt", "GPL-3.0.txt", "JetBrainsMono-OFL.txt"];

    public LicensesWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.GetControl<TextBox>("Text").Text = AllText();
    }

    private static string AllText() => string.Join("\n\n" + new string('=', 78) + "\n\n", Files.Select(f =>
    {
        using var s = typeof(LicensesWindow).Assembly.GetManifestResourceStream("licenses/" + f)!;
        return (f == Files[0] ? "" : f + "\n\n") + new StreamReader(s).ReadToEnd().TrimEnd();
    }));

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
