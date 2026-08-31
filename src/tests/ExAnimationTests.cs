using System.Linq;
using PipeDream;
using Xunit;

namespace PipeDream.Tests;

// Pure parse of an LM ExAnimation record (CONTRACT §12e), synthetic bytes, no ROM.
public class ExAnimationTests
{
    // exanim_1's real record: 1 slot, dest 0xA0, 3 frames -> source tiles 0x601/0x655/0x6AA.
    private static readonly byte[] Exanim1 =
    [
        0x01, 0x00,             // +0 slot count
        0xFF, 0xFF,             // +2 flag AND mask
        0x00, 0x00,             // +4 flag OR mask
        0x00, 0x00,             // +6 $7FC070 selector
        // slot 0 @ record+8:
        0x02, 0x00,             // +8 unknown
        0x01, 0x00,             // +A unknown
        0x02, 0x00,             // +C frameCount-1 = 2 -> 3 frames
        0x0A,                   // +E dest
        0x20, 0x7D,             // frame 0 -> $7D20 -> tile 0x601
        0xA0, 0x87,             // frame 1 -> $87A0 -> tile 0x655
        0x40, 0x92,             // frame 2 -> $9240 -> tile 0x6AA
    ];

    [Fact]
    public void ParsesDestFramesAndSources()
    {
        var slots = ExAnimation.ParseSlots(Exanim1);
        Assert.Single(slots);
        var s = slots[0];
        Assert.Equal(0xA0, s.DestTile);       // dest word 0x0A00 / 16
        Assert.Equal(3, s.FrameCount);
        Assert.Equal(0x601, s.SrcTile(0));
        Assert.Equal(0x655, s.SrcTile(1));
        Assert.Equal(0x6AA, s.SrcTile(2));
    }

    [Fact]
    public void DestIsWordDividedBy16_NonAligned()
    {
        // exanim_4's real record: dest dialog 0x2A -> word 0x02A0 (bytes A0 02) -> tile 0x2A.
        byte[] rec = [.. Exanim1[..13], 0xA0, 0x02, .. Exanim1[15..]];
        var s = ExAnimation.ParseSlots(rec)[0];
        Assert.Equal(0x2A, s.DestTile);
        Assert.Equal(3, s.FrameCount);        // frameCount byte unaffected by the dest change
    }

    [Fact]
    public void FrameCountControlsSlotWidth()
    {
        // A 4th frame (0x633 -> $8360) must extend the slot by exactly one 16-bit word.
        byte[] rec = [.. Exanim1[..12], 0x03, 0x00, 0x0A,
                      0x20, 0x7D, 0xA0, 0x87, 0x40, 0x92, 0x60, 0x83];
        var s = ExAnimation.ParseSlots(rec)[0];
        Assert.Equal(4, s.FrameCount);
        Assert.Equal(0x633, s.SrcTile(3));
    }

    /// <summary>The fields pinned by the exanim_a..t controlled saves (CONTRACT §12e), on their real
    /// record bytes: slot placement through the offset table (k), a stateful trigger doubling the
    /// list (r, Custom 5), a palette rotation with no frame words and the colour count in the dest
    /// high byte (q), and the alternate-file flag turning frames into file offsets (h).</summary>
    [Fact]
    public void PinnedFields_SlotTable_Doubling_Palette_AltFile()
    {
        // k: six entries, only slot 5 used, its block at section+0x0C.
        byte[] k = [0x06, 0x00, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x0C, 0x00,
                    0x01, 0x00, 0x02, 0x00, 0x0A, 0x20, 0x7D, 0xA0, 0x87, 0x40, 0x92];
        var ks = Assert.Single(ExAnimation.ParseSlots(k));
        Assert.Equal(5, ks.Index);
        Assert.Equal(0xA0, ks.DestTile);

        // r: Custom 5 → 2×3 words.
        byte[] r = [0x01, 0x00, 0xFF, 0xFF, 0, 0, 0, 0, 0x02, 0x00,
                    0x01, 0x25, 0x02, 0x00, 0x0A, 0x20, 0x7D, 0xA0, 0x87, 0x40, 0x92, 0, 0, 0, 0, 0, 0];
        var rs = Assert.Single(ExAnimation.ParseSlots(r));
        Assert.Equal(ExAnimation.TriggerCustom0 + 5, rs.Trigger);
        Assert.True(rs.Doubled);
        Assert.Equal(6, rs.Frames.Length);
        Assert.Equal(3, rs.FrameCount);

        // q: Palette Rotate Right, 4 colours from 0x85, no frame data.
        byte[] q = [0x01, 0x00, 0xFF, 0xFF, 0, 0, 0, 0, 0x02, 0x00, 0x18, 0x00, 0x02, 0x85, 0x03];
        var qs = Assert.Single(ExAnimation.ParseSlots(q));
        Assert.True(qs.IsPalette);
        Assert.Equal(0x85, qs.DestColor);
        Assert.Equal(4, qs.Colors);
        Assert.Empty(qs.Frames);

        // h: alt file (record index 0 → 60), frames are byte offsets: C01/C05/C0A.
        byte[] h = [0x01, 0x00, 0xFF, 0xFF, 0, 0, 0, 0, 0x02, 0x00,
                    0x01, 0x00, 0x02, 0x00, 0x8A, 0x20, 0x00, 0xA0, 0x00, 0x40, 0x01];
        var hs = Assert.Single(ExAnimation.ParseSlots(h));
        Assert.True(hs.AltFile);
        Assert.Equal(0xA0, hs.DestTile);
        Assert.Equal(new[] { 0xC01, 0xC05, 0xC0A }, Enumerable.Range(0, 3).Select(hs.SrcTile).ToArray());

        // Line codes are list indices: 0E moves 32 tiles.
        Assert.Equal(32, ExAnimation.LineTiles[0x0E]);
    }

    /// <summary>Encode is ParseSlots' inverse: real records come back byte-identical (baseline,
    /// slot 5 placement, doubled trigger, rotate), and a slot handed over with only its untriggered
    /// half is padded to the doubled length LM expects.</summary>
    [Fact]
    public void EncodeRoundTripsRealRecords()
    {
        byte[] k = [0x06, 0x00, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x0C, 0x00,
                    0x01, 0x00, 0x02, 0x00, 0x0A, 0x20, 0x7D, 0xA0, 0x87, 0x40, 0x92];
        byte[] r = [0x01, 0x00, 0xFF, 0xFF, 0, 0, 0, 0, 0x02, 0x00,
                    0x01, 0x25, 0x02, 0x00, 0x0A, 0x20, 0x7D, 0xA0, 0x87, 0x40, 0x92, 0, 0, 0, 0, 0, 0];
        byte[] q = [0x01, 0x00, 0xFF, 0xFF, 0, 0, 0, 0, 0x02, 0x00, 0x18, 0x00, 0x02, 0x85, 0x03];
        foreach (var rec in new[] { Exanim1, k, r, q })
            Assert.Equal(rec, ExAnimation.Encode(ExAnimation.ParseSlots(rec)));

        // Two slots, the second with only 3 of its 6 words given: the record grows to 6 and reparses.
        var a = new ExAnimation.Slot(0, 4, ExAnimation.TriggerNone, 2, 0x0A00, [0x7D20, 0x7D40], 0);
        var b = new ExAnimation.Slot(3, 1, ExAnimation.TriggerPow, 3, 0x8B00, [0x20, 0xA0, 0x140], 1);
        var back = ExAnimation.ParseSlots(ExAnimation.Encode([b, a], altFileIndex: 1));
        Assert.Equal(2, back.Count);
        Assert.Equal(0, back[0].Index);
        Assert.Equal(3, back[1].Index);
        Assert.Equal(6, back[1].Frames.Length);
        Assert.True(back[1].AltFile);
        Assert.Equal(1, back[1].AltFileIndex);
        Assert.Equal(0xC01, back[1].SrcTile(0) - 0x400);   // file index 1 → tiles from 0x1000
    }

    /// <summary>LM's dest numbering ↔ the record's raw VRAM word: layer 1/2 at word $0000
    /// (word/16), sprites 400-5FF at $6000 (OBSEL=3), layer-3 2bpp 1C00-1DFF at $4000
    /// (BG34NBA=4, 8 words per tile). Vanilla CHR bases, which LM keeps.</summary>
    [Theory]
    [InlineData(0x0A0, 0x0A00)]     // exanim_4's pinned FG dest
    [InlineData(0x2FF, 0x2FF0)]     // last layer-1/2 tile
    [InlineData(0x400, 0x6000)]     // first sprite tile
    [InlineData(0x5FF, 0x7FF0)]     // last sprite tile
    [InlineData(0x1C00, 0x4000)]    // first layer-3 tile
    [InlineData(0x1DFF, 0x4FF8)]    // last layer-3 tile
    public void dest_numbering_round_trips_through_the_vram_word(int tile, int word)
    {
        Assert.Equal(word, ExAnimation.LmTileToWord(tile));
        Assert.Equal(tile, ExAnimation.WordToLmTile(word));
        // The alt-file flag rides bit 15 and never collides with an encoded dest.
        Assert.Equal(tile, ExAnimation.WordToLmTile(word | 0x8000));
        Assert.True(word < 0x8000);
    }
}
