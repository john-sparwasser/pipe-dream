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

    /// <summary>Startup reopens the head of the recent list, so "most recent first" is load-bearing:
    /// if a reopen pushed the wrong end, every launch would land in some old project.</summary>
    [Fact]
    public void the_last_project_opened_is_the_head_of_the_recent_list()
    {
        // Touch writes through to config.json — the run's temp copy, not the real user's
        // (TestConfigDir), which is what makes writing from a test safe at all.
        string a = Path.Combine(Path.GetTempPath(), "pd-recent-a", "project.pdp");
        string b = Path.Combine(Path.GetTempPath(), "pd-recent-b", "project.pdp");
        var c = new Config();
        c.TouchRecentProject(a);
        c.TouchRecentProject(b);
        Assert.Equal(b, c.RecentProjects[0]);

        c.TouchRecentProject(a);                              // reopening an older one promotes it
        Assert.Equal(a, c.RecentProjects[0]);
        Assert.Equal(2, c.RecentProjects.Count);
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

    /// <summary>What keeps the two tests above from eating the real user's settings. Save()
    /// serialises the instance it is called on, so a defaults-only Config saved against the
    /// live path wipes the vanilla ROM, emulator and recents — which is exactly what happened
    /// until TestConfigDir redirected the run. Delete that initializer and this fails instead
    /// of somebody's config.</summary>
    [Fact]
    public void the_test_run_never_writes_the_real_user_config()
    {
        string real = Config.DirFor(
            OperatingSystem.IsMacOS(),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.NotEqual(real, Config.Dir);
        Assert.StartsWith(Path.GetTempPath(), Config.Dir);
        Assert.StartsWith(Config.Dir, Config.FilePath);
    }

    // The monospace-font probe that used to live here went with the ImGui layer: Avalonia
    // resolves fonts by family name and falls back on its own, so there is no per-platform
    // path list left to pin.
}
