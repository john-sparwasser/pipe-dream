using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// config.json is one file with as many writers as the user has editor instances open — there is
/// no single-instance guard — and it is read back on every launch. These pin the two ways a
/// shipped build could lose it: a stale writer, and a read that fails. Each test works a file of
/// its own, so the run's shared (redirected) config and parallel classes cannot perturb it.
/// </summary>
public class ConfigTests
{
    private static string Fresh()
        => Path.Combine(Path.GetTempPath(), "pdcfg-" + Guid.NewGuid().ToString("N")[..8], "config.json");

    private static void Drop(string p) { try { Directory.Delete(Path.GetDirectoryName(p)!, true); } catch { } }

    [Fact]
    public void two_instances_saving_the_same_file_keep_each_others_changes()
    {
        string p = Fresh();
        try
        {
            var a = Config.Load(p);
            var b = Config.Load(p);                                  // loaded before a writes anything
            a.VanillaRomPath = "/roms/smw.smc"; a.Save(p);
            b.EmulatorPath = "/bin/mesen";      b.Save(p);           // b never saw a's ROM

            var disk = Config.Load(p);
            Assert.Equal("/roms/smw.smc", disk.VanillaRomPath);     // used to come back null: b's stale copy won
            Assert.Equal("/bin/mesen", disk.EmulatorPath);

            // A field an instance DID change is its own to set — even past a value the other
            // instance wrote in between — while everything else still follows the disk.
            a.EmulatorPath = "/bin/snes9x"; a.Save(p);
            var again = Config.Load(p);
            Assert.Equal("/bin/snes9x", again.EmulatorPath);
            Assert.Equal("/roms/smw.smc", again.VanillaRomPath);
        }
        finally { Drop(p); }
    }

    [Fact]
    public void a_file_that_does_not_parse_is_set_aside_rather_than_overwritten()
    {
        string p = Fresh();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "{ \"VanillaRomPath\": \"/roms/smw.smc\", ");   // cut off mid-write

            var c = Config.Load(p);
            Assert.Null(c.VanillaRomPath);                                     // defaults, as before —
            Assert.False(File.Exists(p));
            Assert.Contains("/roms/smw.smc", File.ReadAllText(p + ".corrupt")); // but nothing is lost

            c.GfxBrowserView = "cards"; c.Save(p);                             // and the next save is clean
            Assert.Equal("cards", Config.Load(p).GfxBrowserView);
            Assert.Contains("/roms/smw.smc", File.ReadAllText(p + ".corrupt"));
        }
        finally { Drop(p); }
    }
}
