using System.Buffers.Binary;
using System.IO.Compression;

namespace PipeDream;

/// <summary>Minimal RGBA8888 → PNG writer (for exporting renders to inspect).</summary>
public static class Png
{
    public static void Write(string path, ReadOnlySpan<uint> rgba, int w, int h)
    {
        // Raw = per row: filter byte (0) + w*4 bytes (already R,G,B,A in memory).
        var raw = new byte[h * (1 + w * 4)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                uint c = rgba[y * w + x];
                raw[o++] = (byte)c; raw[o++] = (byte)(c >> 8);
                raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 24);
            }
        }

        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // color type RGBA
        Chunk(fs, "IHDR", ihdr);

        // zlib stream: 0x78 0x01 header + raw deflate + adler32
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x01);
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            ds.Write(raw);
        uint adler = Adler32(raw);
        Span<byte> a = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(a, adler);
        ms.Write(a);
        Chunk(fs, "IDAT", ms.ToArray());

        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream fs, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        fs.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        fs.Write(t);
        fs.Write(data);
        uint crc = Crc32(t, data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        fs.Write(c);
    }

    private static uint Adler32(ReadOnlySpan<byte> d)
    {
        uint a = 1, b = 0;
        foreach (byte x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        void Feed(ReadOnlySpan<byte> s)
        {
            foreach (byte b in s)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320 & (uint)(-(c & 1)));
            }
        }
        Feed(type); Feed(data);
        return c ^ 0xFFFFFFFF;
    }
}
