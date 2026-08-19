using Xunit;

namespace PipeDream.Tests;

/// <summary>BPS create/apply. Round-trips (apply(create(a,b)) == b) rather than golden
/// patch bytes — the spec doesn't mandate a canonical encoding, only that any valid
/// command stream reproduces the target. The applier covers the full spec (all four
/// actions) so foreign patches work; the SourceCopy/TargetCopy case is hand-built.</summary>
public class BpsTests
{
    private static byte[] Random(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void round_trips_identical_files()
    {
        var a = Random(0x8000, 1);
        Assert.Equal(a, BpsApplier.Apply(a, BpsWriter.Create(a, a)));
    }

    [Fact]
    public void round_trips_scattered_edits()
    {
        var a = Random(0x8000, 2);
        var b = (byte[])a.Clone();
        b[0] ^= 1; b[0x123] ^= 0xFF; b[0x7FFF] ^= 0x80;
        for (int i = 0x2000; i < 0x2100; i++) b[i] = 0xAB;
        Assert.Equal(b, BpsApplier.Apply(a, BpsWriter.Create(a, b)));
    }

    [Fact]
    public void round_trips_growth_like_rom_expansion()
    {
        var a = Random(0x8000, 3);
        var b = new byte[0x10000];                     // doubled, tail is new data
        a.CopyTo(b, 0);
        new Random(4).NextBytes(b.AsSpan(0x8000));
        b[0x100] ^= 0x55;                              // plus an in-place edit
        Assert.Equal(b, BpsApplier.Apply(a, BpsWriter.Create(a, b)));
    }

    [Fact]
    public void periodic_expansion_regions_compress_instead_of_inlining()
    {
        // Models a prepped ROM: 32KB source, target doubles it with zero fill, a
        // word fill (0x1004-style), and a byte fill — like real expansion regions.
        var a = Random(0x8000, 7);
        var b = new byte[0x10000];
        a.CopyTo(b, 0);
        for (int i = 0x9000; i < 0xA000; i += 2) { b[i] = 0x04; b[i + 1] = 0x10; }
        for (int i = 0xC000; i < 0xD000; i++) b[i] = 0x30;
        byte[] patch = BpsWriter.Create(a, b);
        Assert.Equal(b, BpsApplier.Apply(a, patch));
        Assert.True(patch.Length < 256,
            $"expected fills to RLE-compress, got {patch.Length} byte patch");
    }

    [Fact]
    public void wrong_source_is_rejected_by_crc()
    {
        var a = Random(0x1000, 5);
        var b = (byte[])a.Clone(); b[7] ^= 1;
        byte[] patch = BpsWriter.Create(a, b);
        var wrong = (byte[])a.Clone(); wrong[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => BpsApplier.Apply(wrong, patch));
    }

    [Fact]
    public void corrupt_patch_is_rejected_by_crc()
    {
        var a = Random(0x1000, 6);
        var b = (byte[])a.Clone(); b[7] ^= 1;
        byte[] patch = BpsWriter.Create(a, b);
        patch[8] ^= 1;
        Assert.Throws<InvalidDataException>(() => BpsApplier.Apply(a, patch));
    }

    [Fact]
    public void applier_handles_source_copy_and_target_copy_actions()
    {
        // Hand-built patch: source "ABCD" → target "CDCDAB".
        byte[] source = "ABCD"u8.ToArray();
        byte[] target = "CDCDAB"u8.ToArray();
        var p = new List<byte> { (byte)'B', (byte)'P', (byte)'S', (byte)'1' };
        BpsWriter.WriteVarint(p, 4);                   // source size
        BpsWriter.WriteVarint(p, 6);                   // target size
        BpsWriter.WriteVarint(p, 0);                   // metadata
        // SourceCopy len 2 from source offset +2 ("CD")
        BpsWriter.WriteVarint(p, ((2UL - 1) << 2) | 2);
        BpsWriter.WriteVarint(p, 2UL << 1);            // relative +2, sign bit clear
        // TargetCopy len 2 from target offset 0 ("CD" again, already written)
        BpsWriter.WriteVarint(p, ((2UL - 1) << 2) | 3);
        BpsWriter.WriteVarint(p, 0UL << 1);            // relative +0
        // SourceCopy len 2 from source offset 0 ("AB"): srcRel is now 4 → relative -4
        BpsWriter.WriteVarint(p, ((2UL - 1) << 2) | 2);
        BpsWriter.WriteVarint(p, (4UL << 1) | 1);      // magnitude 4, sign bit = negative
        void U32(uint v) { p.Add((byte)v); p.Add((byte)(v >> 8)); p.Add((byte)(v >> 16)); p.Add((byte)(v >> 24)); }
        U32(Crc32.Compute(source));
        U32(Crc32.Compute(target));
        U32(Crc32.Compute(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(p)));
        Assert.Equal(target, BpsApplier.Apply(source, p.ToArray()));
    }
}
