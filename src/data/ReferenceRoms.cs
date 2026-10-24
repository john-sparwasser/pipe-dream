namespace PipeDream;

/// <summary>
/// Where the reverse-engineering reference ROMs live — the vanilla image plus the
/// Lunar-Magic-saved oracles the self-check and the gated tests compare against. These are
/// never redistributed (see .gitignore) and never touched by the shipped editor; they only
/// back <c>--selfcheck</c>, the debug CLI defaults, and RealRomFact/LmRefRomFact tests.
///
/// The root comes from PIPEDREAM_SMW_ROOT so the same commands work on macOS and Linux;
/// the historical Windows layout stays the default so nothing needs reconfiguring here.
/// A missing root is not an error — the checks and tests that need a given ROM skip when
/// the file is absent.
/// </summary>
internal static class ReferenceRoms
{
    /// <summary>Override with PIPEDREAM_SMW_ROOT (e.g. ~/smw on a Mac).</summary>
    internal static string Root =>
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") is { Length: > 0 } r
            ? r : @"C:\SMW\Projects";

    /// <summary>A file in the shared .resources folder (vanilla + the LM diff oracles).</summary>
    internal static string Resource(string fileName) => Path.Combine(Root, ".resources", fileName);

    /// <summary>A file inside one of the reference hack folders.</summary>
    internal static string InProject(string projectFolder, string fileName) =>
        Path.Combine(Root, projectFolder, fileName);

    internal static string Vanilla => Resource("SMW.smc");
    internal static string LmAfter => Resource("after.smc");
    internal static string ShaoBase => InProject("ShaoBase", "base.smc");
    /// <summary>Clean + SA-1 Pack v1.40, one level saved by Lunar Magic 3.40: the oracle LM's SA-1
    /// forms are generated from (<see cref="LmSa1Forms"/>), with the extracted pack beside it.</summary>
    internal static string Sa1LevelSave => Resource(Path.Combine("sa1", "lm-sa1.smc"));
    internal static string Sa1Pack => Resource(Path.Combine("sa1", "SA1-Pack-140"));
    /// <summary>The SA-1 base `--sa1prep` leaves, one ExAnimation slot saved into level 105 by LM 3.40
    /// (tools/lm/Invoke-LmGui.ps1 -Action SaveExAnim): the oracle for LM's SA-1 ExAnimation engine,
    /// against the LoROM install in <see cref="ExAnim1"/>.</summary>
    internal static string Sa1ExAnimSave => Resource(Path.Combine("sa1", "lm-sa1-exanim.smc"));
    /// <summary>Vanilla with the same slot saved by LM on LoROM: where LmExAnimEngine's bytes come from.</summary>
    internal static string ExAnim1 => Resource("exanim_1.smc");
    /// <summary>A 4MB SA-1 hack saved by Lunar Magic: its SA-1 forms of LM's routines are the oracle
    /// for <see cref="Sa1Remap"/> (reference/SA1_PORT.md).</summary>
    internal static string Sgdq2024 => InProject("Secret", Path.Combine("workspace", "sgdq2024.smc"));
}
