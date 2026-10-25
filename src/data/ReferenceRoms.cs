namespace PipeDream;

/// <summary>
/// Where the reverse-engineering reference ROMs live — the vanilla image plus the
/// Lunar-Magic-saved oracles the self-check and the gated tests compare against. No ROM is ever
/// committed (see .gitignore): the oracles travel as BPS patches in the repo's fixtures/ folder
/// (<see cref="Fixtures"/>), and none of this is touched by the shipped editor; it only backs
/// <c>--selfcheck</c>, the debug CLI defaults, and RealRomFact/LmRefRomFact tests.
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

    /// <summary>A file in the shared .resources folder (vanilla + the LM diff oracles) — or, when it
    /// is not there, the same ROM built from the repo's patch of it (<see cref="Fixtures"/>).</summary>
    internal static string Resource(string fileName)
    {
        string path = Path.Combine(Root, ".resources", fileName);
        return File.Exists(path) ? path : Fixtures.Materialise(fileName) ?? path;
    }

    /// <summary>A file inside one of the reference hack folders.</summary>
    internal static string InProject(string projectFolder, string fileName) =>
        Path.Combine(Root, projectFolder, fileName);

    /// <summary>The clean US ROM every patch is against; the one file no fixture can stand in for.</summary>
    internal static string Vanilla => Path.Combine(Root, ".resources", "SMW.smc");
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
    /// <summary>The same slot saved onto the main map from LM 3.40's overworld editor (Invoke-LmGui.ps1
    /// -Action SaveOwExAnim): on the SA-1 base the oracle for LM's SA-1 overworld ExAnimation engine,
    /// on vanilla its LoROM install.</summary>
    internal static string Sa1OwExAnimSave => Resource(Path.Combine("sa1", "lm-sa1-ow-exanim.smc"));
    internal static string OwExAnim1 => Resource("ow_exanim_1.smc");
    /// <summary>LM 3.40's command line, `-ExportGFX`, `-ImportGFX`, then GFX00 back as ExGFX100 with
    /// `-ImportExGFX`: on clean + pack the oracle for LM's SA-1 4bpp mode, on vanilla its LoROM install.</summary>
    internal static string Sa1FourBppSave => Resource(Path.Combine("sa1", "lm-sa1-4bpp.smc"));
    internal static string FourBpp1 => Resource("gfx4bpp_1.smc");
    /// <summary>Level 105 saved with separate midway settings (Invoke-LmGui.ps1 -Action SaveMidway): on
    /// clean + pack the oracle for LM's SA-1 midway routine, on vanilla its LoROM install.</summary>
    internal static string Sa1MidwaySave => Resource(Path.Combine("sa1", "lm-sa1-midway.smc"));
    internal static string Midway1 => Resource("midway_1.smc");
    /// <summary>A 4MB SA-1 hack saved by Lunar Magic: its SA-1 forms of LM's routines are the oracle
    /// for <see cref="Sa1Remap"/> (reference/SA1_PORT.md).</summary>
    internal static string Sgdq2024 => InProject("Secret", Path.Combine("workspace", "sgdq2024.smc"));
}
