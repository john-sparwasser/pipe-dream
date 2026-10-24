using Xunit;

namespace PipeDream.Tests;

/// <summary>The oracle ROMs come from their patches when .resources lacks them (src/data/Fixtures.cs).</summary>
public class FixturesTests
{
    [Fact]
    public void a_fixture_is_its_patch_applied_over_vanilla_built_once_and_refused_over_another_rom()
    {
        string root = Path.Combine(Path.GetTempPath(), "pd-fixtures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rnd = new Random(1);
            var vanilla = new byte[0x2000]; rnd.NextBytes(vanilla);
            var oracle = (byte[])vanilla.Clone();
            for (int i = 0x100; i < 0x180; i++) oracle[i] ^= 0x5A;
            string fixtures = Path.Combine(root, "fixtures"), cache = Path.Combine(root, "cache"), van = Path.Combine(root, "SMW.smc");
            Directory.CreateDirectory(Path.Combine(fixtures, "sa1"));
            File.WriteAllBytes(van, vanilla);
            File.WriteAllBytes(Path.Combine(fixtures, "sa1", "x.bps"), BpsWriter.Create(vanilla, oracle));
            string name = Path.Combine("sa1", "x.smc");

            string? path = Fixtures.Materialise(fixtures, cache, van, name);
            Assert.NotNull(path);
            Assert.EndsWith("x.smc", path);                                      // the ROM's own name, in a folder of its own
            Assert.Equal(oracle, File.ReadAllBytes(path));
            Assert.Null(Fixtures.Materialise(fixtures, cache, van, "absent.smc"));

            var written = File.GetLastWriteTimeUtc(path);
            Assert.Equal(path, Fixtures.Materialise(fixtures, cache, van, name));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));              // built once

            File.WriteAllBytes(van, oracle);                                     // not the ROM the patch was made from
            var e = Assert.Throws<InvalidDataException>(() => Fixtures.Materialise(fixtures, Path.Combine(root, "cache2"), van, name));
            Assert.Contains("x.bps", e.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void create_writes_one_patch_per_oracle_and_skips_vanilla_and_restore_copies()
    {
        string root = Path.Combine(Path.GetTempPath(), "pd-fixtures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vanilla = new byte[0x1000]; new Random(2).NextBytes(vanilla);
            var oracle = (byte[])vanilla.Clone(); oracle[0x800] ^= 0xFF;
            string res = Path.Combine(root, ".resources"), fixtures = Path.Combine(root, "fixtures");
            Directory.CreateDirectory(Path.Combine(res, "sa1", "sysLMRestore"));
            File.WriteAllBytes(Path.Combine(res, "SMW.smc"), vanilla);
            File.WriteAllBytes(Path.Combine(res, "after.smc"), oracle);
            File.WriteAllBytes(Path.Combine(res, "sa1", "lm.smc"), oracle);
            File.WriteAllBytes(Path.Combine(res, "sa1", "sysLMRestore", "smwOrig.smc"), vanilla);

            var lines = Fixtures.Create(res, Path.Combine(res, "SMW.smc"), fixtures).ToList();
            Assert.Equal(2, lines.Count);
            Assert.True(File.Exists(Path.Combine(fixtures, "after.bps")));
            Assert.True(File.Exists(Path.Combine(fixtures, "sa1", "lm.bps")));
            Assert.Equal(oracle, BpsApplier.Apply(vanilla, File.ReadAllBytes(Path.Combine(fixtures, "sa1", "lm.bps"))));
        }
        finally { Directory.Delete(root, true); }
    }
}
