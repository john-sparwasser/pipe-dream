using System.Text.RegularExpressions;
using Avalonia.Controls;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Help ▸ Third-party licences carries Asar's LGPL inside the one-file binary. The two ways it
/// goes stale: a text the window names is not embedded, or the notice names a different Quasar
/// commit from the one tools/asar/build.sh pins (the source the LGPL has us point at).
/// </summary>
public class LicensesTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void every_licence_is_embedded_and_the_notice_names_the_pinned_quasar()
    {
        string all = new PipeDream.Ui.LicensesWindow().GetControl<TextBox>("Text").Text!;
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", all);
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", all);
        Assert.Contains("SIL OPEN FONT LICENSE", all);

        string root = new DirectoryInfo(Path.GetDirectoryName(Self())!).Parent!.Parent!.FullName;
        string pin = Regex.Match(File.ReadAllText(Path.Combine(root, "tools", "asar", "build.sh")), @"^pin=(\w+)", RegexOptions.Multiline).Groups[1].Value;
        Assert.Matches("^[0-9a-f]{40}$", pin);
        Assert.Contains("commit " + pin, all);
    }

    private static string Self([System.Runtime.CompilerServices.CallerFilePath] string self = "") => self;
}
