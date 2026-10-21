using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

/// <summary>
/// Where one frame of SMW's sprite loop ($01808C) spends its cycles, for common ground enemies on a
/// real level's Map16, in the CPU interpreter. Prints; asserts only that the scene stayed alive, so
/// the numbers mean what they say. Cycles are an estimate (65816 base timings, m/x width, taken
/// branches; no page-cross or slow-ROM penalties), good for ranking, not for absolute claims.
/// </summary>
public class SpriteFrameCostTests(ITestOutputHelper log)
{
    private const int Loop = 0x01808C, InitSpriteTables = 0x07F7D2, Warm = 90, Measured = 32;

    // Base cycles, 8-bit m/x, direct page aligned, no page cross.
    private static readonly byte[] Base = Table(
        "7674535632246465 " +
        "2557546624226475 " +
        "6684335642254465 " +
        "2557446624224475 " +
        "6624735632233465 " +
        "2557746624324475 " +
        "6664335642265465 " +
        "2557446624426475 " +
        "3644333622234445 " +
        "2667444625224555 " +
        "2624333622244445 " +
        "2557444624224445 " +
        "2634335622234465 " +
        "2557646624336475 " +
        "2634335622234465 " +
        "2557546624428475 ");

    private static byte[] Table(string s) => [.. s.Replace(" ", "").Select(c => (byte)(c - '0'))];

    private static int Cycles(byte op, int p)
    {
        bool m16 = (p & 0x20) == 0, x16 = (p & 0x10) == 0;
        int c = Base[op];
        int lo = op & 3, nib = op & 0xF;
        bool mem = lo == 1 || (lo == 3 && nib != 0xB);                     // ORA AND EOR ADC STA LDA CMP SBC, all modes
        bool rmw = lo == 2 && nib is 0x6 or 0xE && op is not (0x86 or 0x8E or 0x96 or 0x9E or 0xA6 or 0xAE or 0xB6 or 0xBE);
        bool xr = op is 0x86 or 0x8E or 0x96 or 0xA2 or 0xA6 or 0xAE or 0xB6 or 0xBE or 0xA0 or 0xA4 or 0xAC or 0xB4 or 0xBC
                     or 0x84 or 0x8C or 0x94 or 0xC0 or 0xC4 or 0xCC or 0xE0 or 0xE4 or 0xEC or 0x5A or 0x7A or 0xDA or 0xFA;
        bool mo = op is 0x24 or 0x2C or 0x34 or 0x3C or 0x64 or 0x74 or 0x9C or 0x9E or 0x48 or 0x68 or 0x89;
        bool tsb = op is 0x04 or 0x0C or 0x14 or 0x1C;
        if (m16) { if (mem || mo) c++; if (rmw || tsb) c += 2; }
        if (x16 && xr) c++;
        return c;
    }

    private sealed class Frame(int target, int sp, Frame? parent) { public int Target = target, Sp = sp; public Frame? Parent = parent; }

    private sealed class Profile
    {
        public long Cycles, Instr;
        public readonly Dictionary<int, long> Excl = [], Incl = [], Calls = [];
    }

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x0180D2] = "slot timers + OAM index",   [0x018127] = "HandleSprite",           [0x0185C3] = "CallSpriteMain",
        [0x018AFC] = "Goomba/Koopa main",         [0x01A40D] = "sprite<->sprite interact",[0x01A7E4] = "Mario<->sprite interact",
        [0x019032] = "SubUpdateSprPos",           [0x019140] = "sprite<->block interact", [0x019211] = "  buoyancy / water check",
        [0x01AC31] = "SubOffscreen",              [0x019D67] = "SubSprGfx1 (2-tile draw)",[0x019F0D] = "SubSprGfx2Entry1 (1-tile draw)",
        [0x01A365] = "GetDrawInfo",               [0x01AD30] = "SubHorizPos",             [0x0086DF] = "ExecutePtr",
        [0x01ABD8] = "SubSprYPosNoGrvty",         [0x01ABCC] = "SubSprXPosNoGrvty",       [0x01944D] = "  map16 lookup (CODE_01944D)",
        [0x038B6B] = "GetMarioClipping",          [0x03B69F] = "GetSpriteClippingA",      [0x03B72B] = "CheckForContact",
        [0x018E5F] = "SetAnimationFrame",         [0x018B31] = "",
    };

    private static string Name(int a) => Names.TryGetValue(a, out var n) && n.Length > 0 ? $"{n} (${a:X6})" : $"${a:X6}";

    private static Profile Run(Cpu65816 cpu, Func<int, bool> measure, int frames, Action<int> before)
    {
        var prof = new Profile(); var dbg = new List<string>();
        Frame? top = null;
        int prevAt = -1, prevSp = 0, prevP = 0; byte prevOp = 0; Frame? prevFrame = null;
        bool pushNext = false; int pushSp = 0; bool active = false;

        void Account(int nextAt)
        {
            if (prevAt < 0 || !active) return;
            int c = Cycles(prevOp, prevP);
            if (prevOp is 0x10 or 0x30 or 0x50 or 0x70 or 0x90 or 0xB0 or 0xD0 or 0xF0 && nextAt != prevAt + 2) c++;
            prof.Cycles += c; prof.Instr++;
            int key = prevFrame?.Target ?? Loop;
            prof.Excl[key] = prof.Excl.GetValueOrDefault(key) + c;
            for (var f = prevFrame; f is not null; f = f.Parent)
                prof.Incl[f.Target] = prof.Incl.GetValueOrDefault(f.Target) + c;
        }

        cpu.Trace = (at, op, p) =>
        {
            int sp = cpu.Registers.SP; { if (dbg.Count >= 40) dbg.RemoveAt(0); dbg.Add($"{at:X6}:{op:X2}:sp{sp:X4}:p{p:X2}"); }
            Account(at);
            if (pushNext) { top = new Frame(at, pushSp, top); pushNext = false; if (active) prof.Calls[at] = prof.Calls.GetValueOrDefault(at) + 1; }
            while (top is not null && sp >= top.Sp) top = top.Parent;      // returned past this frame
            pushNext = op is 0x20 or 0x22 or 0xFC; pushSp = sp;
            (prevAt, prevSp, prevP, prevOp, prevFrame) = (at, sp, p, op, top);
        };

        for (int f = 0; f < frames; f++)
        {
            before(f);
            active = measure(f);
            cpu.Ram7E[0x9D] = 0;
            cpu.Ram7E[0x13]++;
            cpu.PresetWidths(true, true); cpu.PresetDbr(0x00); cpu.PresetP(0x30); cpu.PresetRegs(0, 0, 0);
            prevAt = -1; top = null; pushNext = false;
            try { cpu.CallLong(Loop, 2_000_000, anyBank: true); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            { throw new InvalidOperationException(e.Message + " after " + string.Join(' ', dbg), e); }
            Account(0xFFFF);
        }
        cpu.Trace = null;
        return prof;
    }

    /// <summary>Row of the ground top and the first column of an 11-tile stretch of it, scanning the level.</summary>
    private static (int X, int Row) FindGround(Map16Grid g)
    {
        int Ground(int x) { for (int y = 6; y < g.Height - 1; y++) if (g.Get(x, y) is >= 0x100 and < 0x200 && g.Get(x, y - 1) < 0x100) return y; return -1; }
        for (int x = 8; x < g.Width - 12; x++)
        {
            int r = Ground(x);
            if (r > 0 && Enumerable.Range(x, 11).All(i => Ground(i) == r)) return (x, r);
        }
        throw new InvalidOperationException("no flat ground");
    }

    private (string Report, int Alive, double Cycles) Scene(Rom rom, string title, int[] numbers, int[] statuses, bool marioNear)
    {
        var level = LevelParser.Parse(rom, TestRom.TestLevel);
        var grid = ObjectEngine.Render(rom, level);
        var cpu = ObjectEngine.LastCpu!;
        var (gx, gr) = FindGround(grid);
        int groundY = gr * 16 - 16;
        int camX = gx * 16 - 0x10;
        void W16(int lo, int hi, int v, int i = 0) { cpu.Ram7E[lo + i] = (byte)v; cpu.Ram7E[hi + i] = (byte)(v >> 8); }
        W16(0x1A, 0x1B, camX); W16(0x1C, 0x1D, Math.Max(0, groundY - 0x90));
        W16(0x1462, 0x1463, camX); W16(0x1464, 0x1465, Math.Max(0, groundY - 0x90));
        cpu.Ram7E[0x1692] = 0;
        cpu.Ram7E[0x5D] = (byte)level.Header.Screens;     // sprite<->block interaction treats a column past it as empty
        cpu.Ram7E[0x71] = 0;
        // Mario: beside the group and out of reach, or inside the contact window but above the sprites.
        int mx = marioNear ? gx * 16 + 0x40 : gx * 16 + 0xE8, my = marioNear ? groundY - 0x38 : groundY;
        W16(0x94, 0x95, mx); W16(0x96, 0x97, my);
        W16(0xD1, 0xD2, mx); W16(0xD3, 0xD4, my);          // sprites read Mario's position from these copies

        for (int s = 0; s < 12; s++) cpu.Ram7E[0x14C8 + s] = 0;
        for (int i = 0; i < numbers.Length; i++)
        {
            int slot = 11 - i;                                           // the game fills from the top slot down
            cpu.Ram7E[0x9E + slot] = (byte)numbers[i];
            cpu.PresetWidths(true, true); cpu.PresetDbr(1); cpu.PresetCallerFrames(3); cpu.PresetP(0x30); cpu.PresetRegs(0, slot, 0);
            cpu.CallLong(InitSpriteTables, 300_000, anyBank: true);
            W16(0xE4, 0x14E0, gx * 16 + 0x10 + i * 0x1C, slot);
            W16(0xD8, 0x14D4, groundY - 0x10, slot);
            cpu.Ram7E[0x14C8 + slot] = (byte)statuses[i];
            if (statuses[i] == 0x0A) cpu.Ram7E[0xB6 + slot] = 0x30;
        }

        var trail = new List<string>();
        var prof = Run(cpu, f => f >= Warm, Warm + Measured, f =>
        {
            // A kicked shell leaves the screen in ~30 frames and is erased; hold it where it is so the scene keeps its shells.
            for (int i = 0; i < numbers.Length; i++)
                if (statuses[i] == 0x0A) { int sl = 11 - i; cpu.Ram7E[0x14C8 + sl] = 0x0A; cpu.Ram7E[0xB6 + sl] = 0x30; W16(0xE4, 0x14E0, gx * 16 + 0x10 + i * 0x1C, sl); }
            if (f % 10 == 0 && f < 130 && numbers.Length > 0) trail.Add($"f{f}: last(slot {12 - numbers.Length}) st{cpu.Ram7E[0x14C8 + 12 - numbers.Length]:X} x{cpu.Ram7E[0xE4 + 12 - numbers.Length] | cpu.Ram7E[0x14E0 + 12 - numbers.Length] << 8:X}; st{cpu.Ram7E[0x14C8+11]:X} obj{cpu.Ram7E[0x1588+11]:X} spd{cpu.Ram7E[0xAA+11]:X} x{cpu.Ram7E[0xE4+11] | cpu.Ram7E[0x14E0+11] << 8:X} y{cpu.Ram7E[0xD8+11] | cpu.Ram7E[0x14D4+11] << 8:X} cam{cpu.Ram7E[0x1A] | cpu.Ram7E[0x1B] << 8:X},{cpu.Ram7E[0x1C] | cpu.Ram7E[0x1D] << 8:X}"); });
        int alive = Enumerable.Range(0, 12).Count(s => cpu.Ram7E[0x14C8 + s] is 8 or 9 or 0x0A);

        var sb = new StringBuilder();
        if (trail.Count > 0) sb.AppendLine(string.Join(" | ", trail));
        sb.AppendLine($"\n=== {title}: {numbers.Length} sprites, Mario {(marioNear ? "near" : "far")}; level ${TestRom.TestLevel:X}, ground row {gr} at col {gx}; {alive} alive after {Warm + Measured} frames");
        sb.AppendLine($"per frame: {prof.Cycles / (double)Measured:F0} cycles, {prof.Instr / (double)Measured:F0} instructions");
        sb.AppendLine("  inclusive cycles/frame            exclusive  calls/frame");
        foreach (var (t, c) in prof.Incl.OrderByDescending(kv => kv.Value).Take(22))
            sb.AppendLine($"  {c / (double)Measured,8:F0}  {(100.0 * c / prof.Cycles),5:F1}%   {prof.Excl.GetValueOrDefault(t) / (double)Measured,7:F0}  {prof.Calls.GetValueOrDefault(t) / (double)Measured,6:F1}   {Name(t)}");
        return (sb.ToString(), alive, prof.Cycles / (double)Measured);
    }

    private static string PatchFile(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tools", "patches", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(name);
    }

    private static Rom Variant(params string[] patches)
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        foreach (var p in patches)
        {
            var r = Asar.Patch(rom, PatchFile(p));
            Assert.True(r.Ok, p + ": " + string.Join("\n", r.Errors));
        }
        return rom;
    }

    [AsarRomFact]
    public void where_a_frame_of_common_enemies_goes()
    {
        int[] mix = [0x04, 0x04, 0x05, 0x0F, 0x0F, 0x0F];
        int[] st = [8, 8, 8, 8, 8, 8];
        var scenes = new (string, int[], int[], bool)[]
        {
            ("empty (fixed cost)",                 [],                      [],                  false),
            ("Koopas + Goombas, Mario far",        mix,                     st,                  false),
            ("Koopas + Goombas, Mario near",       mix,                     st,                  true),
            ("Koopas + Goombas + 2 kicked shells", [.. mix, 0x04, 0x05],    [.. st, 0x0A, 0x0A], false),
        };
        var variants = new (string, string[])[]
        {
            ("vanilla", []),
            ("collision patch", ["sprite_collision_speed.asm"]),
            ("all patches", ["sprite_speed.asm", "sprite_collision_speed.asm"]),
        };
        var sb = new StringBuilder();
        var table = new StringBuilder("cycles per frame (vs vanilla)\n");
        var baseline = new double[scenes.Length];
        foreach (var (vname, patches) in variants)
        {
            sb.AppendLine($"\n######## {vname}");
            for (int i = 0; i < scenes.Length; i++)
            {
                var (title, nums, sts, near) = scenes[i];
                var (rep, alive, cycles) = Scene(Variant(patches), title, nums, sts, near);
                sb.Append(rep);
                Assert.True(alive >= sts.Length - 2, $"{vname} / {title}: only {alive} of {sts.Length} sprites survived, the scene is not what it claims");
                if (vname == "vanilla") baseline[i] = cycles;
                table.AppendLine($"  {vname,-16} {title,-38} {cycles,8:F0}  {(vname == "vanilla" ? "" : $"{100 * (cycles - baseline[i]) / baseline[i]:+0.0;-0.0}%  ({cycles - baseline[i]:+0;-0}")}{(vname == "vanilla" ? "" : ")")}");
            }
        }
        log.WriteLine(table.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sprite_frame_cost.txt"), table + sb.ToString());
    }
}
