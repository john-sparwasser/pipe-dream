namespace PipeDream;

/// <summary>
/// The prep on an SA-1 build (reference/SA1_PORT.md, phase 2). The base stays LoROM and byte-frozen;
/// a build with SA-1 on (<see cref="Sa1Target.Finish"/>) applies the SA-1 Pack to its copy and then
/// <see cref="ApplySa1"/> writes the prep's SA-1 form over the result. Two things make that necessary:
///
///  • the pack moves RAM by rewriting operands at fixed vanilla offsets, and where the prep already
///    put a jump, it rewrites the jump's target instead (`JSL $06F5D0` at $00C17A becomes
///    `JSL $6FBEF5`) — so every byte of ours the pack wrote is written back;
///  • our routines, and Lunar Magic's that the prep carries, address vanilla RAM, which on SA-1 lives
///    elsewhere — so every one of them is said again in its SA-1 form.
///
/// Whose code it is decides where the SA-1 form comes from: ours is re-emitted through
/// the <see cref="Sa1Target"/> (Asm's operands, or the sweep for <see cref="Sa1RawCode"/>); Lunar Magic's
/// is LM's own SA-1 bytes, carried as a diff in <see cref="LmSa1Forms"/>. The diff can also DROP a
/// byte — where LM's SA-1 save leaves the pack's remap or hijack standing, so must we: the one
/// restamp that does not is a crash ($02ABF3, the first boot's finding). The stamp lists and
/// addresses are in RomPrep.cs and RomPrep.Stamps.cs; the routines in RomPrep.Code.cs.
/// </summary>
public static partial class RomPrep
{
    /// <summary>
    /// Write the prep's SA-1 form over a prepped base the SA-1 Pack was just applied to. A byte is
    /// written where the SA-1 form differs from the LoROM one, or where the pack wrote (its operand
    /// remap landing on a hook of ours); everywhere else the image is left as the pack made it.
    /// <paramref name="packWrote"/> is the pack's Asar blocks, as SNES addresses. The base must be
    /// at <see cref="LmSa1Forms.PrepVersion"/>, the version LM's SA-1 forms were generated against.
    /// </summary>
    public static void ApplySa1(Rom rom, int version, Sa1Target target, IEnumerable<(int Snes, int Length)> packWrote)
    {
        if (version != LmSa1Forms.PrepVersion)
            throw new InvalidOperationException($"SA-1 needs the base at prep v{LmSa1Forms.PrepVersion}, not v{version}: Lunar Magic's SA-1 forms are generated against that version's stamps");
        var lorom = Overlay(RomTarget.LoRom.PrepStamps(version));
        var sa1 = Overlay(target.PrepStamps(version));
        LmSa1Forms.Apply(sa1);
        var wrote = new HashSet<int>();
        foreach (var (snes, n) in packWrote)
            for (int i = 0; i < n; i++) wrote.Add(Pc(snes + i));
        foreach (var (pc, b) in sa1)
            if (!lorom.TryGetValue(pc, out var l) || b != l || wrote.Contains(pc)) rom.Data[pc + rom.HeaderOffset] = b;
        RatsWriter.FixChecksum(rom);
        ResetScanCaches(rom);
    }

    /// <summary>
    /// The stamp list of <paramref name="version"/> for an SA-1 build, before LM's forms: the
    /// Asm-built routines re-emitted with their RAM operands moved, the raw code in
    /// <see cref="Sa1RawCode"/> remapped instruction by instruction, every other stamp as it was.
    /// Throws when a routine cannot be said on SA-1 without changing its length — the layout is
    /// pinned, so that needs a new routine.
    /// </summary>
    internal static List<(int Pc, byte[] Bytes)> Sa1Stamps(int version, Sa1Target target)
    {
        var stamps = BuildStamps(version, target);
        var remap = target.Remap;
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
    /// Code of OURS stamped as bytes rather than built with <see cref="Asm"/> — in-place rewrites of
    /// vanilla loops — with the M/X state it is entered in, for the sweep. Lunar Magic's bytes are
    /// never listed: their SA-1 forms are LM's own (<see cref="LmSa1Forms"/>). Data stamps (tables,
    /// RATS blocks) are never listed either: their bytes are not instructions. None yet: the raw
    /// rewrites at $00A897/$00AB0B/$00B895 are LM's 4bpp install, waiting on that oracle.
    /// </summary>
    internal static readonly (int Snes, bool M8, bool X8)[] Sa1RawCode = [];

    /// <summary>A stamp list as the image it leaves, byte by byte: later stamps win, as in Apply.</summary>
    private static Dictionary<int, byte> Overlay(List<(int Pc, byte[] Bytes)> stamps)
    {
        var o = new Dictionary<int, byte>();
        foreach (var (pc, bytes) in stamps)
            for (int i = 0; i < bytes.Length; i++) o[pc + i] = bytes[i];
        return o;
    }
}
