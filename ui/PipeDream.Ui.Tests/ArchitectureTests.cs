using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The layering, enforced rather than described.
///
///   ui/PipeDream.Ui              draws and takes input — and is the shipped exe, PipeDream
///   services/PipeDream.Services  what the editor DOES — composition, editing, the save cycle
///   PipeDream.Storage (src/)     ROM bytes, .pdp files, the patch builder
///
/// The UI talks to the services and nothing else. Storage is the editor's database: it is not
/// called from the presentation layer, and it knows nothing about it.
///
/// Part of that boundary is structural — PipeDream's internals are visible to the services and
/// not to the UI, so the project file and the config are simply out of reach. But <c>Rom</c>
/// itself has to stay public for the services' own API, and nothing stops a control from
/// calling <c>File.ReadAllBytes</c>. This test covers what the compiler cannot: it reads the UI
/// sources and fails on a storage call appearing in one.
///
/// A failure here is not a style complaint. Every one of these calls has a home in the services
/// layer, and the reason to care is concrete: the whole open → edit → save → build cycle has to
/// stay runnable without a window, both for the tests and for the command line.
/// </summary>
public class ArchitectureTests(ITestOutputHelper log)
{
    /// <summary>Repo root, found by walking up from the test binary until the app project shows.</summary>
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PipeDream.Storage.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> Sources(string relative)
        => Directory.EnumerateFiles(Path.Combine(Root(), relative), "*.cs", SearchOption.AllDirectories)
                    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

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
        (@"\bFile\s*\.\s*(Exists|ReadAll|WriteAll|Copy|Delete|Open)", "EditorSession"),
        (@"\bDirectory\s*\.", "EditorSession"),
    ];

    [Fact]
    public void the_ui_layer_does_not_call_the_storage_layer()
    {
        var offences = new List<string>();
        foreach (string file in Sources(Path.Combine("ui", "PipeDream.Ui")))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // Comments and doc summaries name these types constantly, on purpose: they are
                // where the layering is explained.
                string code = Regex.Replace(line, @"//.*|///.*", "");
                if (code.TrimStart().StartsWith("using ")) continue;
                foreach (var (pattern, instead) in Banned)
                    if (Regex.IsMatch(code, pattern))
                        offences.Add($"{Path.GetFileName(file)}:{i + 1}  {line.Trim()}\n"
                                   + $"        → {instead}");
            }
        }
        foreach (string o in offences) log.WriteLine(o);
        Assert.Empty(offences);
    }

    /// <summary>The services must run without a window — that is what makes the save cycle
    /// testable and a headless build possible.</summary>
    [Fact]
    public void the_services_layer_does_not_reference_a_ui_framework()
    {
        // Comments name the UI layer on purpose — that is where the boundary is explained —
        // so only real code counts.
        var offences = Sources(Path.Combine("services", "PipeDream.Services"))
            .Where(f => Regex.Replace(File.ReadAllText(f), @"//.*|/\*[\s\S]*?\*/", "") is var code
                        && (code.Contains("Avalonia") || code.Contains("ImGui")))
            .Select(Path.GetFileName)
            .ToList();
        foreach (string o in offences) log.WriteLine(o!);
        Assert.Empty(offences);

        // The references, not the whole file: the csproj comment explains the boundary and names
        // the layer above it.
        string proj = File.ReadAllText(Path.Combine(Root(), "services", "PipeDream.Services",
                                                    "PipeDream.Services.csproj"));
        var deps = Regex.Matches(proj, @"<(?:Package|Project)Reference\s+Include=""([^""]+)""")
                        .Select(m => m.Groups[1].Value).ToList();
        log.WriteLine(string.Join(", ", deps));
        Assert.DoesNotContain(deps, d => d.Contains("Avalonia") || d.Contains("Foster")
                                      || d.Contains("ImGui"));
    }

    /// <summary>
    /// The command line runs in the SAME executable as the editor, so the rule that the
    /// presentation layer cannot call storage has to survive that: the entry point asks the
    /// services layer to run a command rather than reaching for the ROM tools itself.
    /// </summary>
    [Fact]
    public void the_command_line_goes_through_the_services_layer()
    {
        string program = File.ReadAllText(Path.Combine(Root(), "ui", "PipeDream.Ui", "Program.cs"));
        Assert.Contains("EditorSession.RunCommandLine", program);
        Assert.DoesNotContain("DebugCommands", program);

        // And both spellings work: the switch alone, or any command flag.
        Assert.True(EditorSession.IsCommandLine([EditorSession.HeadlessSwitch]));
        Assert.True(EditorSession.IsCommandLine(["--selfcheck"]));
        Assert.False(EditorSession.IsCommandLine([]));
        Assert.False(EditorSession.IsCommandLine([@"C:\some\rom.smc", "105"]));
    }

    /// <summary>And the UI's only project reference is the services layer, so a new file cannot
    /// quietly acquire the storage layer as a direct dependency.</summary>
    [Fact]
    public void the_ui_project_references_only_the_services_layer()
    {
        string proj = File.ReadAllText(Path.Combine(Root(), "ui", "PipeDream.Ui", "PipeDream.Ui.csproj"));
        var refs = Regex.Matches(proj, @"<ProjectReference\s+Include=""([^""]+)""")
                        .Select(m => Path.GetFileName(m.Groups[1].Value))
                        .ToList();
        log.WriteLine(string.Join(", ", refs));
        Assert.Equal(["PipeDream.Services.csproj"], refs);
    }
}
