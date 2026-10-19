namespace PipeDream;

/// <summary>
/// Lunar Magic's sprite Map16: the 16x16 tiles a custom sprite's Displays are drawn from.
/// Tiles 0x000-0x3FF are Lunar Magic's own (SpriteMap16.bin, taken from Lunar Magic 3.40 by
/// <c>--lmspritemap16</c>); a sprite's own Map16 replaces them from 0x300. A tile is four tile
/// words, TL BL TR BR, laid out as FG Map16's: YXPCCCTT TTTTTTTT, where the 8x8 tile numbers
/// 0x000-0x1FF are SP1-SP4 and 0x200-0x2FF are GFX33, the animated tiles (Yoshi, Podoboo).
/// </summary>
public static class SpriteMap16
{
    public const int Count = 0x400, Own = 0x300;

    private static byte[]? lm;
    private static byte[] Lm => lm ??= LoadEmbedded();

    /// <summary>The OAM a display draws with its sprite's spawn cell at level pixel (x, y): each
    /// Map16 tile becomes its four 8x8 tiles, in the order Lunar Magic layers them (first on top).</summary>
    public static List<SpriteRender.Oam> Oam(PixiConfig.Display d, byte[] own, int x, int y)
    {
        var oam = new List<SpriteRender.Oam>();
        foreach (var t in d.Tiles)
            if (Words(t.Number, own) is { } w)
                for (int q = 0; q < 4; q++)
                {
                    int lo = w[q * 2], hi = w[q * 2 + 1];
                    oam.Add(new SpriteRender.Oam(x + t.X + (q >> 1) * 8, y + t.Y + (q & 1) * 8,
                        lo | (hi & 3) << 8, (hi & 0xC0) | ((hi >> 2) & 7) << 1, false));
                }
        return oam;
    }

    /// <summary>The display Lunar Magic shows for a placement: the one for its extra bit and its
    /// position nibbles (or its extension byte's value), else the first for its extra bit, else
    /// the first. Null when the config has none, or the one picked is text.</summary>
    public static PixiConfig.Display? Pick(PixiConfig c, bool bit, byte[]? bytes, int cx, int cy)
    {
        var mine = c.Displays.Where(d => d.ExtraBit == bit).ToList();
        var d = mine.FirstOrDefault(d => c.DisplayByExtraByte
                    ? bytes is { } eb && d.X < eb.Length && eb[d.X] == d.Y
                    : d.X == (cx & 15) && d.Y == (cy & 15))
                ?? mine.FirstOrDefault() ?? c.Displays.FirstOrDefault();
        return d is { UseText: false, Tiles.Count: > 0 } ? d : null;
    }

    /// <summary>A tile's 8 bytes: the sprite's own from 0x300 when it has that many, else Lunar Magic's.</summary>
    private static byte[]? Words(int number, byte[] own)
    {
        int i = (number - Own) * 8;
        if (number >= Own && i + 8 <= own.Length) return own[i..(i + 8)];
        return number is >= 0 and < Count ? Lm[(number * 8)..(number * 8 + 8)] : null;
    }

    /// <summary>Find Lunar Magic's table in its executable: it opens with tile 000 (SP1's
    /// 00/10/01/11, palette 0) and tile 001 (the same in palette 1), then a flipped tile 002.</summary>
    public static byte[]? FindInLunarMagic(byte[] exe)
    {
        ReadOnlySpan<byte> head = [0x00, 0x00, 0x10, 0x00, 0x01, 0x00, 0x11, 0x00,
                                   0x00, 0x04, 0x10, 0x04, 0x01, 0x04, 0x11, 0x04, 0x87, 0x4C];
        int at = exe.AsSpan().IndexOf(head);
        return at >= 0 && at + Count * 8 <= exe.Length ? exe[at..(at + Count * 8)] : null;
    }

    private static byte[] LoadEmbedded()
    {
        using var s = typeof(SpriteMap16).Assembly.GetManifestResourceStream("SpriteMap16.bin");
        if (s is null) return new byte[Count * 8];
        var b = new byte[Count * 8];
        s.ReadExactly(b);
        return b;
    }
}
