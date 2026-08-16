using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace PipeDream.Tests;

public class PngTests
{
    [Fact]
    public void write_produces_a_png_whose_decoded_pixels_match_exactly()
    {
        // RGBA8888 as the codebase uses it: byte order R,G,B,A → uint 0xAABBGGRR.
        uint[] px =
        {
            0xFF0000FF,   // red, opaque
            0xFF00FF00,   // green, opaque
            0x80FF0000,   // blue, half alpha
            0x00112233,   // fully transparent, non-zero channels
            0xFFFFFFFF,   // white
            0xFF000000,   // black
        };
        const int w = 3, h = 2;
        string path = Path.Combine(Path.GetTempPath(), $"pd-test-{Guid.NewGuid():N}.png");
        try
        {
            Png.Write(path, px, w, h);
            byte[] f = File.ReadAllBytes(path);

            // PNG signature.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, f.Take(8).ToArray());

            // Walk the chunks.
            var chunks = new List<(string Type, byte[] Data)>();
            for (int p = 8; p + 8 <= f.Length;)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(f.AsSpan(p));
                string type = System.Text.Encoding.ASCII.GetString(f, p + 4, 4);
                chunks.Add((type, f.AsSpan(p + 8, len).ToArray()));
                p += 12 + len;                              // len + type + data + crc
            }
            Assert.Equal("IHDR", chunks[0].Type);
            Assert.Equal("IEND", chunks[^1].Type);

            var ihdr = chunks[0].Data;
            Assert.Equal(13, ihdr.Length);
            Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(0)));
            Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4)));
            Assert.Equal(8, ihdr[8]);                       // bit depth
            Assert.Equal(6, ihdr[9]);                       // color type: RGBA

            // Inflate the IDAT zlib stream (skip 2-byte zlib header, drop 4-byte adler).
            byte[] idat = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
            using var ds = new DeflateStream(
                new MemoryStream(idat, 2, idat.Length - 6), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            ds.CopyTo(raw);
            byte[] scan = raw.ToArray();

            Assert.Equal(h * (1 + w * 4), scan.Length);     // per row: filter byte + w RGBA pixels
            for (int y = 0; y < h; y++)
            {
                int o = y * (1 + w * 4);
                Assert.Equal(0, scan[o]);                   // filter: none
                for (int x = 0; x < w; x++)
                {
                    uint c = px[y * w + x];
                    int q = o + 1 + x * 4;
                    Assert.Equal((byte)c, scan[q]);           // R
                    Assert.Equal((byte)(c >> 8), scan[q + 1]);  // G
                    Assert.Equal((byte)(c >> 16), scan[q + 2]); // B
                    Assert.Equal((byte)(c >> 24), scan[q + 3]); // A
                }
            }
        }
        finally { File.Delete(path); }
    }
}
