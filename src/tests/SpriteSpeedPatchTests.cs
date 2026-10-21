using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// tools/patches/sprite_speed.asm against vanilla SMW, run side by side in the CPU interpreter from
/// random states: everything a caller can see must come out the same. Y is the one by-product —
/// SMW enters the bank 3 dispatch from ExecutePtr, which leaves Y as scratch.
/// </summary>
public class SpriteSpeedPatchTests
{
    private const int Dispatch = 0x03A118, InitSpriteTables = 0x07F7D2;

    private static string PatchPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tools", "patches", "sprite_speed.asm");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tools/patches/sprite_speed.asm");
    }

    private static Rom Patched()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var r = Asar.Patch(rom, PatchPath());
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        return rom;
    }

    private static (Cpu65816 Cpu, string? Error) Run(Rom rom, int entry, int seed, int number, int slot)
    {
        var r = new Random(seed);
        var cpu = new Cpu65816(rom);
        r.NextBytes(cpu.Ram7E);
        r.NextBytes(cpu.Ram7F);
        cpu.Ram7E[0x9E + slot] = (byte)number;
        cpu.PresetDbr(r.Next(1, 0x10));
        cpu.PresetCallerFrames(3);
        cpu.PresetP(0x30);
        cpu.PresetRegs(r.Next(0x10000), slot, r.Next(0x100));
        try { cpu.CallLong(entry, 300_000, anyBank: true); return (cpu, null); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { return (cpu, e.GetType().Name + ": " + e.Message); }
    }

    private static void Same(Cpu65816 a, Cpu65816 b, string what)
    {
        var (ra, rb) = (a.Registers, b.Registers);
        Assert.True((ra.A, ra.X, ra.P, ra.SP, ra.DBR, ra.PBR) == (rb.A, rb.X, rb.P, rb.SP, rb.DBR, rb.PBR), $"{what}: registers {ra} vs {rb}");
        for (int i = 0; i < 0x10000; i++)
        {
            // Below the stack pointer is what the routine pushed and pulled for itself: dead.
            if (i >= 0x100 && i <= Math.Min(ra.SP, rb.SP)) continue;
            Assert.True(a.Ram7E[i] == b.Ram7E[i], $"{what}: $7E{i:X4} {a.Ram7E[i]:X2} vs {b.Ram7E[i]:X2}");
            Assert.True(a.Ram7F[i] == b.Ram7F[i], $"{what}: $7F{i:X4}");
        }
    }

    [AsarRomFact]
    public void every_sprite_number_reaches_the_routine_it_did()
    {
        var vanilla = Rom.Load(TestRom.RealRomPath);
        var patched = Patched();
        int ran = 0;
        for (int number = 0; number < 0x100; number++)
            foreach (int slot in new[] { 0, 5, 11 })
            {
                var (v, ve) = Run(vanilla, Dispatch, number * 31 + slot, number, slot);
                var (p, pe) = Run(patched, Dispatch, number * 31 + slot, number, slot);
                Assert.True(ve == pe, $"sprite ${number:X2} slot {slot}: vanilla {ve ?? "returned"}, patched {pe ?? "returned"}");
                if (ve is not null) continue;
                Same(v, p, $"sprite ${number:X2} slot {slot}");
                ran++;
            }
        Assert.True(ran > 200, $"only {ran} runs came back, too few to mean anything");
    }

    [AsarRomFact]
    public void spawning_sets_a_slot_up_as_it_did()
    {
        var vanilla = Rom.Load(TestRom.RealRomPath);
        var patched = Patched();
        for (int seed = 0; seed < 300; seed++)
        {
            int number = seed % 0x100, slot = seed % 12;
            var (v, ve) = Run(vanilla, InitSpriteTables, seed, number, slot);
            var (p, pe) = Run(patched, InitSpriteTables, seed, number, slot);
            Assert.True(ve is null, $"vanilla seed {seed}: {ve} after {string.Join(' ', v.RecentPcs.TakeLast(12).Select(x => x.ToString("X6")))}");
            Assert.True(pe is null, $"patched seed {seed}: {pe}");
            Same(v, p, $"spawn sprite ${number:X2} slot {slot}");
            Assert.Equal(v.Registers.Y, p.Registers.Y);
        }
    }
}
