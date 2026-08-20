using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The layering, enforced rather than described.
///
///   src/            Program and App — what starts, plus the command-line tools
///   src/ui/         draws and takes input
///   src/services/   what the editor DOES — composition, editing, the save cycle
///   src/rom/        the SNES/SMW formats
///   src/data/       the project file, config, the ROM builder — storage
///
/// The UI talks to the services and nothing else. Storage is the editor's database: it is not
/// called from the presentation layer, and it knows nothing about it.
///
/// **This test is now the whole enforcement.** While these were separate assemblies, half the
/// boundary was structural — the UI could not see internal storage types because they lived in
/// another assembly. One assembly means the compiler no longer helps at all: nothing but what
/// follows stops a control calling Rom.Load. Treat a failure here as a build break, not a style
/// note.
///
/// Every one of these calls has a home in the services layer, and the reason to care is
/// concrete: the whole open → edit → save → build cycle has to stay runnable without a window,
/// both for these tests and for the command line.
/// </summary>
public class ArchitectureTests(ITestOutputHelper log)
{
    /// <summary>Repo root, found by walking up from the test binary until the app project shows.</summary>
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "PipeDream.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Layer(string name) => Path.Combine(Root(), "src", name);

    private static IEnumerable<string> Sources(string layer)
        => Directory.EnumerateFiles(Layer(layer), "*.cs", SearchOption.AllDirectories)
                    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Code with the comments stripped. Comments name these types constantly, on
    /// purpose — they are where the layering is explained.</summary>
    private static string CodeOf(string line) => Regex.Replace(line, @"//.*|///.*", "");

    /// <summary>
    /// Storage entry points the presentation layer must not name. Deliberately the GATEWAYS —
    /// the things that read or write — and not the domain records that travel through them: a
    /// LevelHeader or a Sprite crossing the boundary is data, and a dialog that edits header
    /// fields has to know their names.
    /// </summary>
    private static readonly (string Pattern, string Instead)[] Banned =
    [
        (@"\bRom\s*\.", "ask EditorSession"),
        (@"\bRom\??\s+\w+\s*[;=)]", "hold no Rom: the session owns it"),
        (@"\bRomPrep\b", "EditorSession's open/prep path"),
        (@"\bRomBuilder\b", "EditorSession.Build / ExportBps"),
        (@"\bProjectFile\b", "EditorSession"),
        (@"\bProject\s*\.", "EditorSession"),
        (@"\bConfig\s*\.", "EditorSession's config accessors"),
        (@"\bLevelParser\b", "LevelScene"),
        (@"\bLevelEncoder\b", "LevelEdit"),
        (@"\bObjectEngine\b", "LevelEdit"),
        (@"\bMap16\s*\.", "LevelScene / Map16Edit / Map16Layout"),
        (@"\bPalette\s*\.\s*Load", "LevelScene"),
        (@"\bGfx\s*\.", "GfxSheets"),
        (@"\bSpriteRender\b", "Catalog / SpriteOverlay via LevelScene"),
        (@"\bSpriteDisplay\b", "Catalog"),
        (@"\bSpriteOverlay\s*\.", "EditorSession"),
        (@"\bDm16Saver\b", "LevelEdit"),
        (@"\bDebugCommands\b", "EditorSession.RunCommandLine"),
        (@"\bFile\s*\.\s*(Exists|ReadAll|WriteAll|Copy|Delete|Open)", "EditorSession"),
        (@"\bDirectory\s*\.", "EditorSession"),
    ];

    [Fact]
    public void the_ui_layer_does_not_call_the_storage_layer()
    {
        var offences = new List<string>();
        foreach (string file in Sources("ui"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = CodeOf(lines[i]);
                if (code.TrimStart().StartsWith("using ")) continue;
                foreach (var (pattern, instead) in Banned)
                    if (Regex.IsMatch(code, pattern))
                        offences.Add($"ui/{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}\n"
                                   + $"        → {instead}");
            }
        }
        foreach (string o in offences) log.WriteLine(o);
        Assert.Empty(offences);
    }

    /// <summary>The services must run without a window — that is what makes the save cycle
    /// testable and the command line possible at all.</summary>
    [Fact]
    public void the_services_layer_does_not_reference_a_ui_framework()
    {
        var offences = Sources("services")
            .Where(f => Regex.Replace(File.ReadAllText(f), @"//.*|/\*[\s\S]*?\*/", "")
                             .Contains("Avalonia"))
            .Select(Path.GetFileName)
            .ToList();
        foreach (var o in offences) log.WriteLine(o!);
        Assert.Empty(offences);
    }

    /// <summary>
    /// Storage does not know the layers above it exist. This one used to be free — rom and data
    /// were in a project that simply did not reference the others — and became checkable-only
    /// when everything merged into one assembly, so it is spelled out rather than assumed.
    /// </summary>
    [Fact]
    public void the_storage_layers_do_not_call_upward()
    {
        var offences = new List<string>();
        foreach (string layer in new[] { "rom", "data" })
            foreach (string file in Sources(layer))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = CodeOf(lines[i]);
                    if (code.Contains("Avalonia") || code.Contains("PipeDream.Services")
                        || Regex.IsMatch(code, @"\b(EditorSession|LevelScene|LevelEdit|SpriteEdit|GfxEdit)\b"))
                        offences.Add($"{layer}/{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        foreach (string o in offences) log.WriteLine(o);
        Assert.Empty(offences);
    }

    /// <summary>
    /// The command line runs in the same executable as the editor, so the rule that the
    /// presentation layer cannot call storage has to survive that: the entry point asks the
    /// services layer to run a command rather than reaching for the ROM tools itself.
    /// </summary>
    [Fact]
    public void the_command_line_goes_through_the_services_layer()
    {
        string program = File.ReadAllText(Path.Combine(Root(), "src", "Program.cs"));
        Assert.Contains("EditorSession.RunCommandLine", program);
        Assert.DoesNotContain("DebugCommands", program);

        // And both spellings work: the switch alone, or any command flag.
        Assert.True(EditorSession.IsCommandLine([EditorSession.HeadlessSwitch]));
        Assert.True(EditorSession.IsCommandLine(["--selfcheck"]));
        Assert.False(EditorSession.IsCommandLine([]));
        Assert.False(EditorSession.IsCommandLine([@"C:\some\rom.smc", "105"]));
    }

    /// <summary>The layer folders are the layering, so their existence is part of the contract —
    /// a file that lands outside them belongs to no layer and no rule.</summary>
    [Fact]
    public void every_source_file_belongs_to_a_layer()
    {
        // The only things allowed at src/ top level are what starts the process.
        var top = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs")
                           .Select(Path.GetFileName).OrderBy(n => n).ToList();
        log.WriteLine(string.Join(", ", top!));
        Assert.Equal(["App.axaml.cs", "DebugCommands.cs", "Program.cs"], top);

        foreach (string layer in new[] { "ui", "services", "rom", "data", "tests" })
            Assert.True(Directory.Exists(Layer(layer)), $"src/{layer} is missing");
    }
}
