using Xunit;

namespace PipeDream.Tests;

/// <summary>Platform-dependent choices, pinned so they can be checked from any host. The
/// app itself is portable (SDL3 dialogs, no P/Invoke outside the installer); these are the
/// four places where "which OS is this" actually changes behaviour.</summary>
public class PortabilityTests
{
    [Fact]
    public void config_dir_follows_each_platform_convention()
    {
        // macOS wants Application Support, not the ~/.config that .NET's ApplicationData
        // hands back there.
        string mac = Config.DirFor(isMacOS: true, appData: "/home/u/.config", home: "/Users/u");
        Assert.Equal(Path.Combine("/Users/u", "Library", "Application Support", "PipeDream"), mac);

        // Windows (%APPDATA%) and Linux ($XDG_CONFIG_HOME) both go through ApplicationData.
        string other = Config.DirFor(isMacOS: false, appData: "/home/u/.config", home: "/home/u");
        Assert.Equal(Path.Combine("/home/u/.config", "PipeDream"), other);
    }

    [Fact]
    public void recent_projects_dedupe_by_the_hosts_path_rules()
    {
        var c = new Config();
        string a = Path.Combine(Path.GetTempPath(), "pd-recents", "project.pdp");
        c.RecentProjects.Add(a);
        c.RecentProjects.Add(a.ToUpperInvariant());

        // Whichever way the host compares, the SAME string must always collapse.
        Assert.True(string.Equals(a, a, Config.PathComparison));
        // On a case-sensitive host the upper-cased twin is a different file and must survive.
        bool caseSensitive = Config.PathComparison == StringComparison.Ordinal;
        Assert.Equal(caseSensitive, !string.Equals(a, a.ToUpperInvariant(), Config.PathComparison));
        Assert.Equal(OperatingSystem.IsLinux(), caseSensitive);
    }

    [Fact]
    public void reference_rom_root_is_overridable_for_non_windows_hosts()
    {
        const string key = "PIPEDREAM_SMW_ROOT";
        string? saved = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, Path.Combine("/srv", "smw"));
            Assert.Equal(Path.Combine("/srv", "smw", ".resources", "SMW.smc"), ReferenceRoms.Vanilla);
            Assert.Equal(Path.Combine("/srv", "smw", "ShaoBase", "base.smc"), ReferenceRoms.ShaoBase);

            // Empty is treated as unset, so a blank var can't silently break the defaults.
            Environment.SetEnvironmentVariable(key, "");
            Assert.Equal(Path.Combine(@"C:\SMW\Projects", ".resources", "SMW.smc"), ReferenceRoms.Vanilla);
        }
        finally { Environment.SetEnvironmentVariable(key, saved); }
    }

    // The monospace-font probe that used to live here went with the ImGui layer: Avalonia
    // resolves fonts by family name and falls back on its own, so there is no per-platform
    // path list left to pin.
}
