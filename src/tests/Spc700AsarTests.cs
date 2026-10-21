using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The music engine (AMK_PORT.md phase 5) is SPC700 code assembled by the bundled Asar. This pins
/// that the pinned build has `arch spc700` and a flat output: the program lands at file offset 0 as
/// bytes, its labels carry the `base` address it will run at in ARAM, and a bad instruction is an error.
/// </summary>
public class Spc700AsarTests
{
    private static Asar.Result Assemble(string body, out byte[] data)
    {
        var rom = TestRom.Create(size: 0x80000);
        var path = Path.Combine(Path.GetTempPath(), $"pipedream-spc-{Guid.NewGuid():N}.asm");
        File.WriteAllText(path, body);
        var r = Asar.Patch(rom, path);
        data = rom.Data;
        return r;
    }

    [AsarFact]
    public void a_spc700_program_assembles_to_flat_bytes_with_its_ram_addresses()
    {
        var r = Assemble("""
            norom
            arch spc700
            org $0000
            base $0200
            Main:
                mov a,#$12
                mov $f4,a
                mov $f2,#$6c
                mov a,$1234
                mov a,$10+x
                movw ya,$10
                call Sub
                bra Main
                jmp Main
            Sub:
                ret
            """, out var d);

        Assert.True(r.Ok, string.Join("\n", r.Errors));
        byte[] expect =
        [
            0xE8, 0x12,                 // mov a,#$12
            0xC4, 0xF4,                 // mov $f4,a
            0x8F, 0x6C, 0xF2,           // mov $f2,#$6c (immediate first, then the direct page byte)
            0xE5, 0x34, 0x12,           // mov a,$1234
            0xF4, 0x10,                 // mov a,$10+x
            0xBA, 0x10,                 // movw ya,$10
            0x3F, 0x00, 0x00,           // call Sub (patched below)
            0x2F, 0x00,                 // bra Main (patched below)
            0x5F, 0x00, 0x02,           // jmp Main
            0x6F,                       // ret
        ];
        int sub = 0x0200 + expect.Length - 1;
        expect[15] = (byte)sub; expect[16] = (byte)(sub >> 8);
        expect[18] = 0xED;                                              // -19: back to Main
        Assert.Equal(expect, d[..expect.Length]);
        Assert.Equal(0x0200, r.Labels["Main"]);
        Assert.Equal(sub, r.Labels["Sub"]);
    }

    [AsarFact]
    public void a_bad_spc700_instruction_is_an_error_with_its_line()
    {
        var r = Assemble("norom\narch spc700\norg $0000\n    frobnicate a\n", out _);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains(":4:"));
    }
}
