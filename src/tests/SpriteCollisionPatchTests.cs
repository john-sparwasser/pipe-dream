using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// tools/patches/sprite_collision_speed.asm against vanilla SMW, side by side in the CPU interpreter
/// from random states. Everything a caller can see must match; the hidden high byte of A is the one
/// things the patch gives up (see the patch's header), so A is compared as its low byte and the overflow flag is not compared.
/// </summary>
public class SpriteCollisionPatchTests
{
    private const int Lookup = 0x019441, LineGuide = 0x01944D;

    private static string PatchPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tools", "patches", "sprite_collision_speed.asm");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tools/patches/sprite_collision_speed.asm");
    }

    private static Rom Patched()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var r = Asar.Patch(rom, PatchPath());
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        return rom;
    }

    private static Cpu65816 Run(Rom rom, int entry, int seed)
    {
        var r = new Random(seed);
        var cpu = new Cpu65816(rom);
        r.NextBytes(cpu.Ram7E);
        r.NextBytes(cpu.Ram7F);
        // Map16: half the high-plane bytes zero, so both the recolour and the skip paths run.
        for (int i = 0xC800; i < 0x10000; i++) if (r.Next(2) == 0) cpu.Ram7F[i] = 0;
        // Tiles the recolour cares about turn up often enough to be hit.
        int[] special = [0x29, 0x2B, 0x2F, 0x32, 0xEC, 0xF0, 0xFB, 0xFC, 0x00, 0x11, 0x6E];
        for (int i = 0xC800; i < 0x10000; i += 3) cpu.Ram7E[i] = (byte)special[r.Next(special.Length)];
        int slot = r.Next(12);
        var e = cpu.Ram7E;
        e[0x5B] = (byte)new[] { 0, 0, 0, 1, 2, 3, 0x80 }[r.Next(7)];
        e[0x185E] = (byte)(r.Next(4) == 0 ? 1 : 0);
        e[0x5D] = (byte)r.Next(1, 0x20);
        e[0x15E9] = (byte)slot;
        e[0x1656 + slot] = (byte)r.Next(256);
        e[0x14D4 + slot] = (byte)r.Next(0, 3);
        e[0x14E0 + slot] = (byte)(r.Next(8) == 0 ? r.Next(256) : r.Next(0, 0x10));
        // Close to the $01B0 limit as often as not, and to the screen edges.
        e[0xD8 + slot] = (byte)(r.Next(3) == 0 ? r.Next(0xA0, 0xC0) : r.Next(256));
        int e5B = e[0x5B], e185E = e[0x185E];
        int y = entry == Lookup ? r.Next(4) : r.Next(0x3D);
        cpu.PresetWidths(true, true);
        cpu.PresetDbr(r.Next(1, 0x10));
        cpu.PresetP(0x30);
        cpu.PresetRegs(r.Next(0x10000), slot, y);
        try { cpu.CallNear(entry, 100_000); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        { throw new InvalidOperationException($"seed {seed} entry {entry:X6} Y={y} slot={slot} $5B={e5B:X2} $185E={e185E}: {ex.Message} after {string.Join(' ', cpu.RecentPcs.TakeLast(30).Select(x => x.ToString("X6")))}", ex); }
        return cpu;
    }

    private static void Same(Cpu65816 a, Cpu65816 b, string what)
    {
        var (ra, rb) = (a.Registers, b.Registers);
        Assert.True((ra.A & 0xFF, ra.X, ra.Y, ra.P & 0xBF, ra.SP, ra.DBR, ra.PBR) == (rb.A & 0xFF, rb.X, rb.Y, rb.P & 0xBF, rb.SP, rb.DBR, rb.PBR),
            $"{what}: registers {ra} vs {rb}");
        for (int i = 0; i < 0x10000; i++)
        {
            if (i >= 0x100 && i <= Math.Min(ra.SP, rb.SP)) continue;   // below SP: the routine's own pushes, dead
            Assert.True(a.Ram7E[i] == b.Ram7E[i], $"{what}: $7E{i:X4} {a.Ram7E[i]:X2} vs {b.Ram7E[i]:X2}");
            Assert.True(a.Ram7F[i] == b.Ram7F[i], $"{what}: $7F{i:X4}");
        }
    }

    [AsarRomFact]
    public void the_map16_lookup_leaves_everything_as_it_did()
    {
        var vanilla = Rom.Load(TestRom.RealRomPath);
        var patched = Patched();
        int slow = 0;
        foreach (int entry in new[] { Lookup, LineGuide })
            for (int seed = 0; seed < 3_000; seed++)
            {
                Cpu65816 v, p;
                try { v = Run(vanilla, entry, seed); }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { slow++; continue; }
                p = Run(patched, entry, seed);
                Same(v, p, $"${entry:X6} seed {seed}");
            }
        Assert.True(slow < 100, $"{slow} runs did not finish in vanilla");
    }
}
