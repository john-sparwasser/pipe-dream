using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PipeDream;

/// <summary>
/// The oracle ROMs shared through git. The tests and the generators compare against Lunar Magic's
/// saves — after.smc, the exanim series, the layer-3 and SA-1 saves — and a ROM is never committed,
/// so each ships as a BPS patch against the vanilla ROM in the repo's `fixtures/` folder, laid out
/// as `.resources` is (`fixtures/sa1/lm-sa1.bps` is `.resources/sa1/lm-sa1.smc`). A patch carries
/// what LM wrote and nothing of the game's.
///
/// <see cref="ReferenceRoms.Resource"/> looks in `.resources` first and asks here otherwise: the
/// patch is applied over <see cref="ReferenceRoms.Vanilla"/> into a per-user cache, once per patch
/// content, and that file's path is returned — a checkout with the vanilla ROM alone runs every
/// oracle test. `--fixtures` writes the patches from this machine's `.resources`.
/// </summary>
internal static class Fixtures
{
    /// <summary>`fixtures/` beside `src/`, found from the running binary; PIPEDREAM_FIXTURES overrides.</summary>
    internal static string? Dir
    {
        get
        {
            if (Environment.GetEnvironmentVariable("PIPEDREAM_FIXTURES") is { Length: > 0 } d) return d;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "fixtures")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                    return Path.Combine(dir.FullName, "fixtures");
            return null;
        }
    }

    private static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipeDream", "fixtures");

    private static readonly ConcurrentDictionary<string, string?> made = new();

    /// <summary>The ROM `.resources/<paramref name="fileName"/>` would be, built from its patch; null
    /// without a patch, a fixtures folder or the vanilla ROM.</summary>
    internal static string? Materialise(string fileName)
        => Dir is { } d ? made.GetOrAdd(fileName, f => Materialise(d, CacheDir, ReferenceRoms.Vanilla, f)) : null;

    /// <summary>Apply `<paramref name="fixturesDir"/>/<name>.bps` over <paramref name="vanillaPath"/> into
    /// <paramref name="cacheDir"/>, keyed by the patch's content so a changed patch is rebuilt and an
    /// unchanged one is not. The file keeps the ROM's name in a folder of its own (Lunar Magic wants
    /// one ROM per folder). A vanilla ROM the patch was not made from is refused by name.</summary>
    internal static string? Materialise(string fixturesDir, string cacheDir, string vanillaPath, string fileName)
    {
        string bps = Path.Combine(fixturesDir, Path.ChangeExtension(fileName, ".bps"));
        if (!File.Exists(bps) || !File.Exists(vanillaPath)) return null;
        var patch = File.ReadAllBytes(bps);
        string path = Path.Combine(cacheDir, Convert.ToHexString(SHA256.HashData(patch))[..16], fileName);
        if (File.Exists(path)) return path;
        byte[] rom;
        try { rom = BpsApplier.Apply(File.ReadAllBytes(vanillaPath), patch); }
        catch (InvalidDataException e) { throw new InvalidDataException($"{bps} does not apply over {vanillaPath}: {e.Message}", e); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";       // tests build the same fixture in parallel
        File.WriteAllBytes(tmp, rom);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Write `fixtures/<name>.bps` for every ROM under `.resources` that is an oracle: not the
    /// vanilla one, not Lunar Magic's restore copies, not the `test/` scratch projects, and not a
    /// `prepped-*` image the prep rebuilds itself. Returns one line per patch.</summary>
    internal static IEnumerable<string> Create(string resourcesDir, string vanillaPath, string fixturesDir)
    {
        var vanilla = File.ReadAllBytes(vanillaPath);
        foreach (var rom in Directory.EnumerateFiles(resourcesDir, "*.smc", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(resourcesDir, rom).Replace('\\', '/');
            if (name == "SMW.smc" || name.Contains("sysLMRestore") || name.StartsWith("test/") || Path.GetFileName(name).StartsWith("prepped-")) continue;
            var patch = BpsWriter.Create(vanilla, File.ReadAllBytes(rom));
            string bps = Path.Combine(fixturesDir, Path.ChangeExtension(name, ".bps"));
            Directory.CreateDirectory(Path.GetDirectoryName(bps)!);
            File.WriteAllBytes(bps, patch);
            yield return $"{name,-40} {patch.Length,8:N0} bytes";
        }
    }
}
