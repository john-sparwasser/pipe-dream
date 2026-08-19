using PipeDream;
using Xunit;

namespace PipeDream.Tests;

// LevelObject struct math + Level.NormalizeStream ordering. Pure, no ROM.
public class LevelObjectTests
{
    [Fact]
    public void AbsoluteX_And_Size()
    {
        var o = new LevelObject(false, 0x14, screen: 2, xNibble: 5, y: 10, b3: 0x33, extra: -1);
        Assert.Equal(2 * 16 + 5, o.AbsoluteX);
        Assert.Equal(4, o.Width);    // (0x33 & 0x0F) + 1
        Assert.Equal(4, o.Height);   // (0x33 >> 4) + 1
        Assert.False(o.Extended);
    }

    [Fact]
    public void Number0_IsExtended()
    {
        var o = new LevelObject(false, 0, 0, 0, 0, b3: 0x41, extra: -1);
        Assert.True(o.Extended);
        Assert.Equal(0x41, o.ExtendedNumber);
    }

    [Fact]
    public void ScreenExit_IsExtendedZeroByte3()
    {
        Assert.True(new LevelObject(false, 0, 1, 0, 0, b3: 0x00, extra: 5).IsScreenExit);
        Assert.False(new LevelObject(false, 0, 1, 0, 0, b3: 0x01, extra: -1).IsScreenExit);
    }

    [Fact]
    public void MakeDm16_PicksFormByPage()
    {
        Assert.Equal(0x22, LevelObject.MakeDm16(0x0FF, 0, 0, 0).Number);   // page 0
        Assert.Equal(0x23, LevelObject.MakeDm16(0x1AB, 0, 0, 0).Number);   // page 1
        Assert.Equal(0x27, LevelObject.MakeDm16(0x305, 0, 0, 0).Number);   // any page
        Assert.Equal(0x305, LevelObject.MakeDm16(0x305, 0, 0, 0).Dm16Tile);
    }

    [Fact]
    public void WithNewScreen_OnlyChangesFlag()
    {
        var o = new LevelObject(true, 0x14, 3, 5, 10, 0x33, -1);
        var c = o.WithNewScreen(false);
        Assert.False(c.NewScreen);
        Assert.Equal(o.Number, c.Number);
        Assert.Equal(o.Screen, c.Screen);
        Assert.Equal(o.AbsoluteX, c.AbsoluteX);
        Assert.Equal(o.Byte3, c.Byte3);
    }

    [Fact]
    public void ScreenJump_EncodesTargetInY()
    {
        var j = LevelObject.ScreenJump(7);
        Assert.True(j.Extended);
        Assert.Equal(0x01, j.Byte3);       // ext obj 0x01 = screen jump
        Assert.Equal(7, j.Y & 0x1F);       // target screen lives in the Y/b1 low bits
    }

    [Fact]
    public void NormalizeStream_SortsByScreen_And_ClearsNewScreen()
    {
        var objs = new[]
        {
            new LevelObject(false, 0x14, screen: 3, 0, 0, 0x00, -1),
            new LevelObject(false, 0x14, screen: 0, 0, 0, 0x00, -1),
            new LevelObject(false, 0x14, screen: 3, 1, 0, 0x00, -1),
        };
        var norm = LevelEncoder.NormalizeStream(objs);
        // Real objects come out screen-ascending with NewScreen cleared.
        var real = norm.FindAll(o => !(o.Extended && o.Byte3 == 0x01));
        Assert.Equal(new[] { 0, 3, 3 }, real.ConvertAll(o => o.Screen).ToArray());
        Assert.All(real, o => Assert.False(o.NewScreen));
        // Same-screen relative order preserved (stable sort): xNibble 0 before 1 on screen 3.
        Assert.Equal(new[] { 0, 1 }, real.FindAll(o => o.Screen == 3).ConvertAll(o => o.XNibble).ToArray());
    }

    [Fact]
    public void NormalizeStream_InsertsJumpsForGaps()
    {
        var objs = new[]
        {
            new LevelObject(false, 0x14, screen: 0, 0, 0, 0x00, -1),
            new LevelObject(false, 0x14, screen: 5, 0, 0, 0x00, -1),
        };
        var norm = LevelEncoder.NormalizeStream(objs);
        // A screen-jump command to screen 5 must precede the screen-5 object (counter only +1s).
        int jump = norm.FindIndex(o => o.Extended && o.Byte3 == 0x01 && (o.Y & 0x1F) == 5);
        int obj5 = norm.FindIndex(o => !o.Extended && o.Screen == 5);
        Assert.True(jump >= 0 && jump < obj5);
        // No jump needed to reach screen 0 (running counter starts there).
        Assert.DoesNotContain(norm.GetRange(0, norm.FindIndex(o => !o.Extended)),
                              o => o.Extended && o.Byte3 == 0x01 && (o.Y & 0x1F) == 0);
    }
}
