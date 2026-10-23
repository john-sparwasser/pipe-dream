namespace PipeDream;

/// <summary>
/// The prep on an SA-1 build (reference/SA1_PORT.md, phase 2). The base stays LoROM and byte-frozen;
/// a build with SA-1 on applies the SA-1 Pack to its copy and then <see cref="ApplySa1"/> writes the
/// prep's SA-1 form over the result. Two things make that necessary:
///
///  • the pack moves RAM by rewriting operands at fixed vanilla offsets, and where the prep already
///    put a jump, it rewrites the jump's target instead (`JSL $06F5D0` at $00C17A becomes
///    `JSL $6FBEF5`) — so every byte of ours the pack wrote is written back;
///  • our routines, and Lunar Magic's that the prep carries, address vanilla RAM, which on SA-1 lives
///    elsewhere — so every one of them is re-emitted through <see cref="Sa1Remap"/>.
///
/// Lunar Magic does the same on an SA-1 ROM: its hooks sit over the pack's rewritten instructions and
/// its routines are the remapped forms (sgdq2024, Sa1RemapTests). The stamp lists and addresses are
/// in RomPrep.cs and RomPrep.Stamps.cs; the routines in RomPrep.Code.cs.
/// </summary>
public static partial class RomPrep
{
    /// <summary>
    /// The stamp list of <paramref name="version"/> for an SA-1 build: the Asm-built routines
    /// re-emitted with their RAM operands moved, the raw code in <see cref="Sa1RawCode"/> remapped
    /// instruction by instruction, every data stamp as it was. Throws when a routine cannot be said
    /// on SA-1 without changing its length — the layout is pinned, so that needs a new routine.
    /// </summary>
    internal static List<(int Pc, byte[] Bytes)> Sa1Stamps(int version, Sa1Remap remap)
    {
        List<(int Pc, byte[] Bytes)> stamps;
        Asm.Sa1 = remap;
        try { stamps = BuildStamps(version); }
        finally { Asm.Sa1 = null; }

        var raw = Sa1RawCode.ToDictionary(r => Pc(r.Snes));
        for (int i = 0; i < stamps.Count; i++)
        {
            var (pc, bytes) = stamps[i];
            if (!raw.TryGetValue(pc, out var r)) continue;
            var problems = new List<string>();
            stamps[i] = (pc, remap.Code(bytes, r.M8, r.X8, problems));
            if (problems.Count > 0)
                throw new InvalidOperationException($"${r.Snes:X6} cannot move to SA-1 in place: {string.Join("; ", problems)}");
        }
        return stamps;
    }

    /// <summary>
    /// The prep's code that is stamped as bytes rather than built with <see cref="Asm"/> — Lunar
    /// Magic's routines, carried byte for byte, and the in-place rewrites of vanilla loops — with the
    /// M/X state it is entered in. Data stamps (tables, RATS blocks) are never listed: their bytes are
    /// not instructions.
    /// </summary>
    internal static readonly (int Snes, bool M8, bool X8)[] Sa1RawCode =
    [
        (LmMainEntranceRoutine, true, true),
        (LmSecondaryRoutine, true, true),
    ];

    /// <summary>
    /// Write the prep's SA-1 form over a prepped base the SA-1 Pack was just applied to. A byte is
    /// written where the SA-1 form differs from the LoROM one, or where the pack wrote (its operand
    /// remap landing on a hook of ours); everywhere else the image is left as the pack made it.
    /// <paramref name="packWrote"/> is the pack's Asar blocks, as SNES addresses.
    /// </summary>
    public static void ApplySa1(Rom rom, int version, Sa1Remap remap, IEnumerable<(int Snes, int Length)> packWrote)
    {
        var lorom = Overlay(BuildStamps(version));
        var sa1 = Overlay(Sa1Stamps(version, remap));
        var wrote = new HashSet<int>();
        foreach (var (snes, n) in packWrote)
            for (int i = 0; i < n; i++) wrote.Add(Pc(snes + i));
        foreach (var (pc, b) in sa1)
            if (b != lorom[pc] || wrote.Contains(pc)) rom.Data[pc + rom.HeaderOffset] = b;
        RatsWriter.FixChecksum(rom);
        ResetScanCaches(rom);
    }

    /// <summary>A stamp list as the image it leaves, byte by byte: later stamps win, as in Apply.</summary>
    private static Dictionary<int, byte> Overlay(List<(int Pc, byte[] Bytes)> stamps)
    {
        var o = new Dictionary<int, byte>();
        foreach (var (pc, bytes) in stamps)
            for (int i = 0; i < bytes.Length; i++) o[pc + i] = bytes[i];
        return o;
    }
}
