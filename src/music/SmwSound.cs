namespace PipeDream.Music;

/// <summary>
/// What SMW itself uploads to the SPC700, read from a ROM. Its 20 samples are the ones AddmusicK ships as
/// <c>samples/default/*.brr</c> (the same bytes, checked in <c>MusicLayoutTests</c>), so a project needs no AddmusicK folder for them.
/// </summary>
public static class SmwSound
{
    /// <summary>SMW's sample upload: a directory of 20 entries at ARAM $8000, the sample data from $8100.</summary>
    public const int SampleStream = 0x0F8000;
    private const int SampleDirectory = 0x8000, SampleCount = 20;

    /// <summary>The ARAM the upload stream at <paramref name="snes"/> fills: blocks of length, destination and data, ended by a
    /// length of 0. The stream runs on through bank boundaries as the file does (LoROM banks are contiguous there).</summary>
    public static byte[] Upload(Rom rom, int snes)
    {
        var ram = new byte[0x10000];
        int o = rom.FileOffset(snes);
        while (o + 4 <= rom.Data.Length)
        {
            int n = rom.Data[o] | rom.Data[o + 1] << 8, dest = rom.Data[o + 2] | rom.Data[o + 3] << 8;
            if (n == 0) break;
            if (o + 4 + n > rom.Data.Length || dest + n > 0x10000) throw new InvalidDataException($"SMW's SPC upload at {snes:X6} runs off the end");
            Array.Copy(rom.Data, o + 4, ram, dest, n);
            o += 4 + n;
        }
        return ram;
    }

    /// <summary>SMW's samples in AddmusicK's .brr form (a two-byte loop offset, then the BRR blocks up to the one marked last),
    /// in directory order: index i is what <c>#default</c> calls sample i.</summary>
    public static List<byte[]> StockSamples(Rom rom)
    {
        var ram = Upload(rom, SampleStream);
        var samples = new List<byte[]>(SampleCount);
        for (int i = 0; i < SampleCount; i++)
        {
            int start = ram[SampleDirectory + 4 * i] | ram[SampleDirectory + 4 * i + 1] << 8;
            int loop = ram[SampleDirectory + 4 * i + 2] | ram[SampleDirectory + 4 * i + 3] << 8;
            int end = start;
            while ((ram[end] & 1) == 0)
            {
                end += 9;
                if (end + 9 > 0x10000) throw new InvalidDataException($"SMW's sample {i} has no end block");
            }
            end += 9;
            var brr = new byte[2 + end - start];
            brr[0] = (byte)(loop - start); brr[1] = (byte)((loop - start) >> 8);
            Array.Copy(ram, start, brr, 2, end - start);
            samples.Add(brr);
        }
        return samples;
    }
}
