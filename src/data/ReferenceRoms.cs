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
}
