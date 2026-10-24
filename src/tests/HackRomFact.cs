using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// A test that reads one of the reference HACKS (ShaoBase, Dogs of War, juz, BigEye, the sgdq2024
/// workspace) as well as vanilla and after.smc. The hacks are other people's work and are not
/// shipped as fixtures (src/data/Fixtures.cs), so on a checkout with the vanilla ROM and the
/// repo's patches alone these tests skip by name instead of failing on a missing file.
/// </summary>
public sealed class HackRomFactAttribute : FactAttribute
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["ShaoBase"] = Path.Combine("ShaoBase", "base.smc"),
        ["DogsOfWar"] = Path.Combine("DogsOfWar", "dogs_of_war.smc"),
        ["juz"] = Path.Combine("juz", "SMW.smc"),
        ["BigEye"] = Path.Combine("BigEye", "bigeye.smc"),
        ["Secret"] = Path.Combine("Secret", "workspace", "sgdq2024.smc"),
    };

    public HackRomFactAttribute(params string[] hacks)
    {
        if (!File.Exists(TestRom.RealRomPath)) { Skip = "real ROM not present: " + TestRom.RealRomPath; return; }
        if (!File.Exists(ReferenceRoms.LmAfter)) { Skip = "LM reference ROM not present: " + ReferenceRoms.LmAfter; return; }
        foreach (var hack in hacks)
        {
            string path = Path.Combine(ReferenceRoms.Root, Files[hack]);
            if (!File.Exists(path)) { Skip = "reference hack not present: " + path; return; }
        }
    }
}
