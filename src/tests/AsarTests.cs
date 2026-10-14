using Xunit;

namespace PipeDream.Tests;

public sealed class AsarFactAttribute : FactAttribute
{
    public AsarFactAttribute()
    {
        if (!Asar.Available) Skip = "Asar library not found (set PIPEDREAM_ASAR to libasar's path)";
    }
}

/// <summary>Asar as a library: a patch lands on an in-memory ROM, freecode claims RATS-tagged
/// space our RatsWriter sees, and a bad patch leaves the ROM alone with a file:line error.</summary>
public class AsarTests
{
    private static string Asm(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipedream-asar-{Guid.NewGuid():N}.asm");
        File.WriteAllText(path, body);
        return path;
    }

    [AsarFact]
    public void a_patch_writes_its_bytes_and_freecode_is_rats_tagged()
    {
        var rom = TestRom.Create(size: 0x100000);
        var r = Asar.Patch(rom, Asm("""
            lorom
            org $008000
                db $AA,$BB
            freecode
            Data:
                db $11,$22,$33
            print hex(Data)
            """));

        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal([0xAA, 0xBB], rom.Data[rom.FileOffset(0x008000)..][..2]);
        int data = Convert.ToInt32(Assert.Single(r.Prints), 16);
        Assert.Equal([0x11, 0x22, 0x33], rom.Data[rom.FileOffset(data)..][..3]);
        Assert.Contains(RatsWriter.EnumerateRats(rom), rat => rat.PcOffset + 8 == Rom.SnesToPc(data));
        Assert.Contains(r.Blocks, b => Rom.SnesToPc(b.Snes) == Rom.SnesToPc(data) - 8 && b.Length == 8 + 3);   // tag + data
    }

    [AsarFact]
    public void an_error_leaves_the_rom_alone_and_says_where()
    {
        var rom = TestRom.Create();
        var before = rom.Data.ToArray();
        var path = Asm("lorom\norg $008000\n    db $01\n    LDA.q #$00\n");

        var r = Asar.Patch(rom, path);

        Assert.False(r.Ok);
        Assert.Contains($"{Path.GetFileName(path)}:4", Assert.Single(r.Errors));
        Assert.Equal(before, rom.Data);
    }
}
