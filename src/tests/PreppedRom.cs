namespace PipeDream.Tests;

/// <summary>
/// A prepped copy of the vanilla ROM, made once per test run and shared by everything that needs
/// what prep provides — Map16 page allocation, Direct Map16 tile placement, the acts-like table.
///
/// Shared deliberately. Prep is expensive, and six test classes used to keep their own copy under
/// the SAME temp filename: safe while they were separate assemblies running in separate
/// processes, a race the moment they became one, with a class reading the file while another was
/// still copying it. One Lazy in one process cannot do that, and the filename carries the process
/// id so two concurrent `dotnet test` runs stay out of each other's way too.
/// </summary>
internal static class PreppedRom
{
    /// <summary>Path to the prepped ROM, or null when there is no vanilla ROM to prep (or prep
    /// failed) — callers skip rather than fail, as they do for the vanilla ROM itself.</summary>
    public static string? Path => lazy.Value;

    private static readonly Lazy<string?> lazy =
        new(Make, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string? Make()
    {
        string vanilla = ReferenceRoms.Vanilla;
        if (!File.Exists(vanilla)) return null;
        string target = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                               $"pd-prepped-{Environment.ProcessId}.smc");
        try
        {
            File.Copy(vanilla, target, overwrite: true);
            return RomPrep.PrepInPlace(target) is null ? target : null;
        }
        catch { return null; }
    }

    /// <summary>A FRESH prepped copy, for a test that writes to the ROM and must not disturb the
    /// shared one. Null when there is nothing to copy.</summary>
    public static string? Fork()
    {
        if (Path is not { } src) return null;
        string mine = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                             $"pd-prepped-{Guid.NewGuid():N}.smc");
        File.Copy(src, mine, overwrite: true);
        return mine;
    }
}
