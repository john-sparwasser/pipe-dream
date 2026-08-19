using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Known-answer vectors for Gfx.Lz2Decompress, hand-derived from the LC_LZ2 format:
/// header byte = CCCLLLLL (command in bits 5-7, length-1 in bits 0-4); command 7 is the
/// long form: real command in bits 2-4, 10-bit length-1 = (bits 0-1 << 8) | next byte.
/// Commands: 0 = direct copy, 1 = byte fill, 2 = word fill (two bytes alternating),
/// 3 = increasing fill, 4 = copy from absolute output offset (big-endian). 0xFF ends.
/// </summary>
public class Lz2DecompressTests
{
    [Fact]
    public void direct_copy_chunk_emits_the_literal_bytes()
    {
        // 0x02 = cmd 0, len-1 = 2 → copy 3 literals.
        byte[] src = { 0x02, 0x41, 0x42, 0x43, 0xFF };
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, Gfx.Lz2Decompress(src, 0));
    }

    [Fact]
    public void byte_fill_repeats_one_byte()
    {
        // 0x23 = cmd 1, len-1 = 3 → four copies of 0xAA.
        byte[] src = { 0x23, 0xAA, 0xFF };
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, Gfx.Lz2Decompress(src, 0));
    }

    [Fact]
    public void word_fill_alternates_two_bytes_including_odd_lengths()
    {
        // 0x44 = cmd 2, len-1 = 4 → five output bytes alternating 0x12/0x34.
        byte[] src = { 0x44, 0x12, 0x34, 0xFF };
        Assert.Equal(new byte[] { 0x12, 0x34, 0x12, 0x34, 0x12 }, Gfx.Lz2Decompress(src, 0));
    }

    [Fact]
    public void increasing_fill_counts_up_from_the_seed_byte()
    {
        // 0x62 = cmd 3, len-1 = 2 → 0x10, 0x11, 0x12. Wraps at 0x100: seed 0xFF, len 2 → FF 00.
        byte[] src = { 0x62, 0x10, 0xFF };
        Assert.Equal(new byte[] { 0x10, 0x11, 0x12 }, Gfx.Lz2Decompress(src, 0));
        byte[] wrap = { 0x61, 0xFF, 0xFF };
        Assert.Equal(new byte[] { 0xFF, 0x00 }, Gfx.Lz2Decompress(wrap, 0));
    }

    [Fact]
    public void back_reference_copies_from_an_absolute_output_offset()
    {
        // Direct copy "AB C" then 0x81 = cmd 4, len-1 = 1, offset 0x0001 (big-endian)
        // → copies output[1..3] = "B C".
        byte[] src = { 0x02, 0x41, 0x42, 0x43, 0x81, 0x00, 0x01, 0xFF };
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43, 0x42, 0x43 }, Gfx.Lz2Decompress(src, 0));
    }

    [Fact]
    public void overlapping_back_reference_repeats_the_pattern_rle_style()
    {
        // "AB" then cmd 4 len 6 from offset 0: each copied byte may itself have just been
        // produced by this command → "AB" + "ABABAB".
        byte[] src = { 0x01, 0x41, 0x42, 0x85, 0x00, 0x00, 0xFF };
        Assert.Equal("ABABABAB"u8.ToArray(), Gfx.Lz2Decompress(src, 0));
    }

    [Fact]
    public void long_length_extension_form_carries_a_ten_bit_length()
    {
        // Byte fill of 300: len-1 = 299 = 0x12B → header 0xE0 | (cmd 1 << 2) | 0x1 = 0xE5,
        // second length byte 0x2B, fill byte 0x77.
        byte[] src = { 0xE5, 0x2B, 0x77, 0xFF };
        var outp = Gfx.Lz2Decompress(src, 0);
        Assert.Equal(300, outp.Length);
        Assert.All(outp, b => Assert.Equal(0x77, b));
    }

    [Fact]
    public void decompression_starts_at_the_given_offset_and_chains_chunks()
    {
        // Two junk prefix bytes, then: direct "XY", byte-fill 0x5A x3, word fill 1 2 x4.
        byte[] src = { 0xDE, 0xAD, 0x01, 0x58, 0x59, 0x22, 0x5A, 0x43, 0x01, 0x02, 0xFF };
        Assert.Equal(new byte[] { 0x58, 0x59, 0x5A, 0x5A, 0x5A, 0x01, 0x02, 0x01, 0x02 },
                     Gfx.Lz2Decompress(src, 2));
    }
}
