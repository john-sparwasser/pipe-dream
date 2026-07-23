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
}
