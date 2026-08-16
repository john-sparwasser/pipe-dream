using Xunit;

namespace PipeDream.Tests;

/// <summary>LevelEncoder ↔ LevelParser round-trips and NormalizeStream contracts.</summary>
public class LevelRoundTripTests
{
    private static void AssertSameObject(LevelObject e, LevelObject a)
    {
        Assert.Equal(e.NewScreen, a.NewScreen);
        Assert.Equal(e.Number, a.Number);
        Assert.Equal(e.Screen, a.Screen);
        Assert.Equal(e.XNibble, a.XNibble);
        Assert.Equal(e.Y, a.Y);
        Assert.Equal(e.Byte3, a.Byte3);
        Assert.Equal(e.ExtraByte, a.ExtraByte);
        Assert.Equal(e.Dm16Tile, a.Dm16Tile);
        Assert.Equal(e.Dm16Page, a.Dm16Page);
        Assert.Equal(e.Dm16ExtX, a.Dm16ExtX);
        Assert.Equal(e.Dm16ExtH, a.Dm16ExtH);
    }

    [Fact]
    public void standard_extended_and_screen_exit_objects_survive_encode_then_parse()
    {
        var (rom, level) = TestRom.CreateWithLevel();
        var objs = new List<LevelObject>
        {
            new(false, 0x11, 0, 4, 10, 0x21, -1),      // standard object, 2x3 settings byte
            new(false, 0x00, 0, 5, 3, 0x30, -1),       // extended object 0x30
            new(false, 0x00, 0, 0, 1, 0x00, 0x42),     // screen exit (4th byte = 0x42)
            new(false, 0x00, 0, 2, 6, 0x00, 0x00),     // screen exit with extra byte 0
            new(false, 0x3F, 2, 15, 0x1F, 0xFF, -1),   // field extremes on a later screen
        };
        var norm = LevelEncoder.NormalizeStream(objs);
        byte[] enc = LevelEncoder.Encode(level, rom, norm);
        var parsed = LevelParser.ParseEncoded(rom, enc);

        Assert.Equal(norm.Count, parsed.Count);
        for (int i = 0; i < norm.Count; i++) AssertSameObject(norm[i], parsed[i]);
    }

    [Fact]
    public void encoded_stream_written_to_the_rom_parses_back_identically()
    {
        var (rom, level) = TestRom.CreateWithLevel();
        var objs = new List<LevelObject>
        {
            new(false, 0x11, 0, 4, 10, 0x21, -1),
            new(false, 0x0D, 1, 0, 0x14, 0x07, -1),
            new(false, 0x00, 3, 8, 2, 0x00, 0x13),     // screen exit on screen 3
        };
        var norm = LevelEncoder.NormalizeStream(objs);
        byte[] enc = LevelEncoder.Encode(level, rom, norm);

        // The 5 header bytes are carried verbatim from the ROM.
        Assert.Equal(TestRom.LevelHeaderBytes, enc.Take(5).ToArray());

        enc.CopyTo(rom.Data, rom.FileOffset(level.DataPointer));
        var reparsed = LevelParser.Parse(rom, TestRom.TestLevel);

        Assert.False(reparsed.Empty);
        Assert.Equal(norm.Count, reparsed.Objects.Count);
        for (int i = 0; i < norm.Count; i++) AssertSameObject(norm[i], reparsed.Objects[i]);
    }

    [Fact]
    public void empty_object_list_round_trips_as_an_empty_level()
    {
        var (rom, level) = TestRom.CreateWithLevel();
        byte[] enc = LevelEncoder.Encode(level, rom, new List<LevelObject>());
        Assert.Equal(6, enc.Length);                    // header + terminator only
        Assert.Equal(0xFF, enc[5]);
        Assert.Empty(LevelParser.ParseEncoded(rom, enc));
        Assert.True(level.Empty);                       // the fixture level itself is empty
    }

    [Fact]
    public void dm16_all_forms_round_trip_geometry_and_are_a_byte_stream_fixed_point()
    {
        var (rom, level) = TestRom.CreateWithLevel(dm16: true);
        var objs = new List<LevelObject>
        {
            LevelObject.MakeDm16(0x045, screen: 0, xNib: 3, y: 7),               // page-0 form (0x22)
            LevelObject.MakeDm16(0x1A3, screen: 0, xNib: 8, y: 2, w: 4, h: 3),   // Form A (0x23)
            LevelObject.MakeDm16(0x2B0, screen: 1, xNib: 0, y: 5, w: 2, h: 2),   // Form B compact (0x27)
            LevelObject.MakeDm16(0x2C1, screen: 1, xNib: 4, y: 1, w: 20, h: 3),  // extended Form B (w > 16)
            LevelObject.MakeDm16(0x3FF, screen: 2, xNib: 0, y: 0, w: 3, h: 40),  // extended Form B (h > 16)
        };
        var norm = LevelEncoder.NormalizeStream(objs);
        byte[] enc = LevelEncoder.Encode(level, rom, norm);
        var parsed = LevelParser.ParseEncoded(rom, enc);

        Assert.Equal(norm.Count, parsed.Count);
        for (int i = 0; i < norm.Count; i++)
        {
            // Geometry/identity a user relies on. (The raw Dm16Page field is allowed to
            // differ between "constructed" and "parsed" compact Form B — the bytes below
            // pin the real contract.)
            Assert.Equal(norm[i].IsDm16, parsed[i].IsDm16);
            Assert.Equal(norm[i].Dm16Tile, parsed[i].Dm16Tile);
            Assert.Equal(norm[i].Screen, parsed[i].Screen);
            Assert.Equal(norm[i].XNibble, parsed[i].XNibble);
            Assert.Equal(norm[i].Y, parsed[i].Y);
            Assert.Equal(norm[i].Dm16Size(), parsed[i].Dm16Size());
        }
        // Re-encoding the parse must reproduce the stream byte-for-byte.
        Assert.Equal(enc, LevelEncoder.Encode(level, rom, parsed));
    }

    [Fact]
    public void lm_secondary_exit_round_trips_its_two_byte_exit_word()
    {
        var (rom, level) = TestRom.CreateWithLevel(dm16: true);
        var objs = new List<LevelObject> { new(false, 0x00, 0, 0, 4, 0x02, 0x1234) };
        var parsed = LevelParser.ParseEncoded(rom, LevelEncoder.Encode(level, rom, objs));
        var o = Assert.Single(parsed);
        AssertSameObject(objs[0], o);
        Assert.Equal(0x1234, o.ExtraByte);
    }

    [Fact]
    public void dm16_resized_object_round_trips_at_its_new_size()
    {
        var (rom, level) = TestRom.CreateWithLevel(dm16: true);
        var big = LevelObject.MakeDm16(0x2B0, screen: 0, xNib: 2, y: 3, w: 20, h: 5).Dm16Resized(30, 2);
        var small = LevelObject.MakeDm16(0x2B0, screen: 0, xNib: 2, y: 9, w: 20, h: 5).Dm16Resized(4, 4);
        Assert.Equal((30, 2), big.Dm16Size());
        Assert.Equal((4, 4), small.Dm16Size());
        var parsed = LevelParser.ParseEncoded(rom, LevelEncoder.Encode(level, rom, new[] { big, small }));
        Assert.Equal(2, parsed.Count);
        Assert.Equal((30, 2), parsed[0].Dm16Size());
        Assert.Equal((4, 4), parsed[1].Dm16Size());
        Assert.Equal(0x2B0, parsed[0].Dm16Tile);
        Assert.Equal(0x2B0, parsed[1].Dm16Tile);
    }

    // --- NormalizeStream ----------------------------------------------------

    [Fact]
    public void normalize_sorts_by_screen_keeps_within_screen_order_and_inserts_jumps()
    {
        var a = new LevelObject(false, 0x11, 1, 0, 0, 0x00, -1);
        var b = new LevelObject(true, 0x12, 0, 1, 1, 0x00, -1);
        var c = new LevelObject(false, 0x13, 1, 2, 2, 0x00, -1);

        var norm = LevelEncoder.NormalizeStream(new[] { a, b, c });

        Assert.Equal(4, norm.Count);
        Assert.Equal(0x12, norm[0].Number);            // screen 0 first
        Assert.False(norm[0].NewScreen);               // raw new-screen flags cleared
        Assert.True(norm[1].Extended && norm[1].Byte3 == 0x01);   // inserted screen jump
        Assert.Equal(1, norm[1].Screen);
        Assert.Equal(0x11, norm[2].Number);            // same-screen order preserved (stable)
        Assert.Equal(0x13, norm[3].Number);
    }

    [Fact]
    public void normalize_is_idempotent_and_order_stable_for_single_screen_input()
    {
        var objs = new[]
        {
            new LevelObject(false, 0x11, 0, 0, 0, 0x21, -1),
            new LevelObject(false, 0x12, 0, 3, 4, 0x00, -1),
            new LevelObject(false, 0x11, 0, 7, 2, 0x00, -1),
        };
        var once = LevelEncoder.NormalizeStream(objs);
        var twice = LevelEncoder.NormalizeStream(once);
        Assert.Equal(once.Count, twice.Count);
        for (int i = 0; i < once.Count; i++) AssertSameObject(once[i], twice[i]);
    }

    [Fact]
    public void normalize_is_idempotent_for_multi_screen_input()
    {
        var objs = new[]
        {
            new LevelObject(false, 0x11, 0, 0, 0, 0x21, -1),
            new LevelObject(false, 0x12, 2, 3, 4, 0x00, -1),
        };
        var once = LevelEncoder.NormalizeStream(objs);    // contains one inserted jump
        var twice = LevelEncoder.NormalizeStream(once);   // input jumps dropped + re-derived
        Assert.Equal(once.Count, twice.Count);
        for (int i = 0; i < once.Count; i++) AssertSameObject(once[i], twice[i]);
    }
}
