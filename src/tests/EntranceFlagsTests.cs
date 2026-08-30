using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The level-wide flags that ride on an entrance record, as Lunar Magic's entrance dialog
/// exposes them. Vanilla folds slippery/water into the action list ($00A6D5: action 5 = slippery,
/// 7 = water); LM carries them as $192A bits 7/6 from the action's high bits, consumed by its
/// $05DD00 routine (hooked from $00A6CC), and face-left as bit 6 of the FG/BG byte.
/// </summary>
public class EntranceFlagsTests
{
    [RealRomFact]
    public void lms_action_high_bits_set_slippery_and_water_then_clear_themselves()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        RomPrep.Apply(rom, 10);
        foreach (var (high, slippery, water) in new[] { (0x80, 0x80, 0), (0x40, 0, 1), (0xC0, 0x80, 1), (0x00, 0, 0) })
        {
            var cpu = new Cpu65816(rom);
            cpu.Ram7E[0x192A] = (byte)(high | 0x02);            // action 2 under the flags
            cpu.CallLong(0x05DD00);
            Assert.Equal(slippery, cpu.Ram7E[0x86]);
            Assert.Equal(water, cpu.Ram7E[0x85]);
            Assert.Equal(0x02, cpu.Ram7E[0x192A]);              // the action survives, the flags are consumed
        }
    }

    /// <summary>The record fields the dialog edits pack where LM reads them: action high bits into
    /// $05DE00 bits 6-7, face left into $06FE00 bit 6, relative into bit 7.</summary>
    [Fact]
    public void dialog_fields_pack_into_lms_bytes()
    {
        var e = new MainEntrance(new byte[12]) with { EntranceAction = 6, ActionHigh = 2, FaceLeft = 1, FgBgRelative = 1, BgHeight = 0x1A };
        byte[] b = e.ToBytes();
        Assert.Equal(6 << 3, b[1] & 0x38);
        Assert.Equal(0x80, b[4] & 0xC0);
        Assert.Equal(0xDA, b[10]);
        var back = new MainEntrance(b);
        Assert.Equal((1, 1, 2, 0x1A), (back.FaceLeft, back.FgBgRelative, back.ActionHigh, back.BgHeight));
        Assert.Equal(8, Ui.EntranceWindow.Actions.Length);
    }
}
