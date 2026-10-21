using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The SPC700 interpreter, one behaviour at a time. Programs are assembled by Asar's spc700 mode
/// (so these need it), loaded at $0200 and run to a STOP.
/// </summary>
public class Spc700Tests
{
    private static Spc700 Run(string body, Action<Spc700>? setup = null)
    {
        var rom = TestRom.Create(size: 0x80000);
        var path = Path.Combine(Path.GetTempPath(), $"pipedream-spc-{Guid.NewGuid():N}.asm");
        File.WriteAllText(path, "norom\narch spc700\norg $0000\nbase $0200\n" + body + "\n    stop\nEnd:\n");
        var r = Asar.Patch(rom, path);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var s = new Spc700 { Pc = 0x200, Sp = 0xEF };
        Array.Copy(rom.Data, 0, s.Ram, 0x200, r.Labels["End"] - 0x200 + 1);
        setup?.Invoke(s);
        s.Run(5_000_000);
        Assert.True(s.Halted, "the program did not reach its stop");
        return s;
    }

    [AsarFact]
    public void adc_sets_carry_half_carry_and_overflow()
    {
        var s = Run("clrc\nmov a,#$7F\nadc a,#$01");          // $7F + 1 = $80: signed overflow, half carry
        Assert.Equal(0x80, s.A);
        Assert.Equal(0x80 | 0x40 | 0x08, s.Psw & 0xCF);        // N V H
        s = Run("clrc\nmov a,#$FF\nadc a,#$01");
        Assert.Equal(0, s.A);
        Assert.Equal(0x02 | 0x08 | 0x01, s.Psw & 0xCF);        // Z H C
    }

    [AsarFact]
    public void sbc_borrows_through_the_carry_flag()
    {
        var s = Run("setc\nmov a,#$10\nsbc a,#$01");
        Assert.Equal(0x0F, s.A);
        Assert.Equal(0x01, s.Psw & 0xC9);                       // C set (no borrow), H clear (borrow from bit 4), N V clear
        s = Run("clrc\nmov a,#$10\nsbc a,#$01");
        Assert.Equal(0x0E, s.A);
    }

    [AsarFact]
    public void cmp_leaves_its_operand_and_sets_carry_when_greater_or_equal()
    {
        var s = Run("mov a,#$05\ncmp a,#$05");
        Assert.Equal(5, s.A);
        Assert.Equal(0x03, s.Psw & 0x83 | s.Psw & 0x02);        // Z and C
        s = Run("mov a,#$05\ncmp a,#$06");
        Assert.Equal(0x80, s.Psw & 0x83);                        // N, not Z, not C
    }

    [AsarFact]
    public void mul_and_div()
    {
        var s = Run("mov y,#$12\nmov a,#$34\nmul ya");
        Assert.Equal(0x12 * 0x34, s.Y << 8 | s.A);
        s = Run("mov y,#$01\nmov a,#$00\nmov x,#$03\ndiv ya,x");  // 256 / 3
        Assert.Equal((85, 1), (s.A, s.Y));
        s = Run("mov y,#$FF\nmov a,#$FF\nmov x,#$10\ndiv ya,x");  // the quotient overflows 8 bits
        Assert.Equal(0x40, s.Psw & 0x40);                          // V
    }

    [AsarFact]
    public void daa_and_das_correct_binary_coded_decimal()
    {
        var s = Run("clrc\nmov a,#$15\nadc a,#$27\ndaa a");        // 15 + 27 = 42
        Assert.Equal(0x42, s.A);
        s = Run("clrc\nmov a,#$99\nadc a,#$01\ndaa a");            // 99 + 1 = 100: A=00, C
        Assert.Equal((0x00, 1), (s.A, s.Psw & 1));
        s = Run("setc\nmov a,#$42\nsbc a,#$27\ndas a");            // 42 - 27 = 15
        Assert.Equal(0x15, s.A);
    }

    [AsarFact]
    public void sixteen_bit_operations_use_ya_and_wrap_in_the_direct_page()
    {
        var s = Run("mov $10,#$FF\nmov $11,#$00\nmovw ya,$10\nincw $10\nmov a,$10\nmov x,$11");
        Assert.Equal((0x00, 0x01), (s.A, s.X));                      // $00FF + 1 = $0100
        s = Run("mov a,#$FF\nmov y,#$00\nmovw $20,ya\nclrc\nmov a,#$01\nmov y,#$00\naddw ya,$20");
        Assert.Equal(0x100, s.Y << 8 | s.A);
        s = Run("mov a,#$00\nmov y,#$01\nmovw $20,ya\nmov a,#$01\nmov y,#$00\nsubw ya,$20");   // 1 - 256 = -255
        Assert.Equal(0xFF01, s.Y << 8 | s.A);
        Assert.Equal(0x80, s.Psw & 0x81);                            // N, borrow
        s = Run("mov a,#$34\nmov y,#$12\nmovw $20,ya\ncmpw ya,$20");
        Assert.Equal(0x03, s.Psw & 0x03 | (s.Psw & 1));              // equal: Z and C
        Assert.Equal(0x02, s.Psw & 0x02);
    }

    [AsarFact]
    public void memory_bits_and_the_carry_flag()
    {
        var s = Run("mov $30,#$00\nset1 $30.3\nset1 $30.5\nclr1 $30.3");
        Assert.Equal(0x20, s.Ram[0x30]);
        s = Run("mov $30,#$08\nmov1 c,$0030.3\nmov a,#$00\nmov $31,#$00\nmov1 $0031.6,c");
        Assert.Equal(0x40, s.Ram[0x31]);
        s = Run("mov $30,#$FF\nnot1 $0030.0\ntset $0030,a\nmov a,#$01\ntclr $0030,a");
        Assert.Equal(0xFE, s.Ram[0x30]);
    }

    [AsarFact]
    public void branches_loops_and_subroutines()
    {
        var s = Run("""
                mov a,#$00
                mov x,#$05
            Loop:
                mov $60,x
                clrc
                adc a,$60
                dec x
                bne Loop            ; 5+4+3+2+1
                call Sub
                bra Done
            Sub:
                mov y,#$07
                ret
            Done:
            """);
        Assert.Equal((15, 7, 0), (s.A, s.Y, s.X));
        s = Run("mov y,#$04\nmov a,#$00\nL:\ninc a\ndbnz y,L");
        Assert.Equal(4, s.A);
        s = Run("mov a,#$05\nmov $40,#$05\ncbne $40,Wrong\nmov x,#$11\nbra Out\nWrong:\nmov x,#$22\nOut:");
        Assert.Equal(0x11, s.X);
    }

    [AsarFact]
    public void the_direct_page_flag_moves_the_page()
    {
        var s = Run("setp\nmov $10,#$55\nclrp\nmov $10,#$AA");
        Assert.Equal(0x55, s.Ram[0x110]);
        Assert.Equal(0xAA, s.Ram[0x10]);
    }

    [AsarFact]
    public void dsp_writes_are_logged_with_their_cycle()
    {
        var s = Run("mov $f2,#$4C\nmov $f3,#$01\nmov $f2,#$6C\nmov $f3,#$20");
        Assert.Equal([(0x4C, 0x01), (0x6C, 0x20)], s.DspWrites!.Select(w => (w.Reg, (int)w.Value)).ToArray());
        Assert.True(s.DspWrites![1].Cycle > s.DspWrites![0].Cycle);
        Assert.Equal(0x01, s.Dsp[0x4C]);
    }

    [AsarFact]
    public void ports_and_timers()
    {
        var s = Run("mov $f4,#$AB\nmov a,$f5\nmov $50,a", t => t.FromCpu[1] = 0x77);
        Assert.Equal(0xAB, s.ToCpu[0]);
        Assert.Equal(0x77, s.Ram[0x50]);
        // Timer 0 ticks every 128 cycles; with a target of 2 its counter rises every 256, so a ~2000-cycle loop reads about 8.
        s = Run("""
                mov $fa,#$02
                mov $f1,#$01
                mov x,#$00
            Wait:
                inc x
                cmp x,#$FF
                bne Wait
                mov a,$fd
            """);
        Assert.InRange(s.A, 7, 8);
    }
}
