using PipeDream.Music;
using Xunit;

namespace PipeDream.Tests;

/// <summary>The main CPU's side of the music: SMW's code and ours in the 65816 emulator, talking to the SPC700 interpreter
/// through the four ports, the SPC's boot ROM modelled from its documented behaviour.</summary>
public class MusicSnesTests(Xunit.Abstractions.ITestOutputHelper log)
{
    /// <summary>A 65816 and an SPC700 wired together, the SPC running a couple of cycles per 65816 instruction.</summary>
    internal static Cpu65816 Wired(Rom rom, Spc700 spc)
        => new(rom) { ApuRead = p => spc.ToCpu[p], ApuWrite = (p, v) => spc.FromCpu[p] = v, EachStep = () => spc.Run(2) };

    [Fact]
    public void smws_own_upload_routine_loads_its_engine_through_the_boot_rom()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var rom = Rom.Load(ReferenceRoms.Vanilla);
        var spc = Spc700.PoweredOn();
        Wired(rom, spc).CallNear(0x0080E8);                      // UploadSPCEngine
        spc.Run(2000);
        var want = SmwSound.Upload(rom, SmwSound.EngineStream);
        Assert.Equal(want[0x0500..0x1000], spc.Ram[0x0500..0x1000]);
        Assert.Equal(want[0x5570..0x5FD0], spc.Ram[0x5570..0x5FD0]);
        Assert.InRange(spc.Pc, 0x0500, 0x1400);                  // jumped into the engine it loaded
    }

    /// <summary>A vanilla ROM with music inserted: two local songs ($0A, $0B), a global one ($05); SMW's own samples and effects.</summary>
    internal static (Rom Rom, IReadOnlyDictionary<string, int> Labels) Inserted()
    {
        var rom = Rom.Load(ReferenceRoms.Vanilla);
        var p = new ProjectFile();
        p.Music.Songs["a.mml"] = "#amk 2\n#0 t60 @0 o4 c4 d4 e4 f4";
        p.Music.Songs["b.mml"] = "#amk 2\n#0 t60 @1 o5 g8 a8 b8";
        p.Music.Songs["star.mml"] = "#amk 2\n#0 t80 @2 o3 c16 c16 c16";
        p.Music.SongList.Add(new MusicState.SongSlot { Number = 0x0A, Path = "a.mml" });
        p.Music.SongList.Add(new MusicState.SongSlot { Number = 0x0B, Path = "b.mml" });
        p.Music.SongList.Add(new MusicState.SongSlot { Number = 0x05, Path = "star.mml", Global = true });
        var r = MusicInsert.Insert(rom, p);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        return (rom, r.Labels);
    }

    [Fact]
    public void a_project_effect_takes_its_slot_and_smws_keep_the_rest()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var rom = Rom.Load(ReferenceRoms.Vanilla);
        var p = new ProjectFile();
        p.Music.Songs["a.mml"] = "#amk 2\n#0 r1";
        p.Music.SongList.Add(new MusicState.SongSlot { Number = 0x0A, Path = "a.mml" });
        p.Music.SfxSources["1DF9/mine.txt"] = "@10 o4 a8";
        p.Music.Sfx1DF9.Add(new MusicState.SfxSlot { Slot = 0x01, File = "mine.txt" });
        var r = MusicInsert.Insert(rom, p);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var spc = Spc700.PoweredOn();
        var cpu = Wired(rom, spc);
        cpu.CallNear(0x0080E8);
        spc.Run(40_000);
        int First(byte effect)
        {
            cpu.Ram7E[0x1DF9] = effect;
            var t = Frames(cpu, spc, r.Labels["Nmi"], 20);
            return t.KeyOns.First(k => k.Voice == 6).Pitch;
        }
        int mine = First(0x01);
        Frames(cpu, spc, r.Labels["Nmi"], 30);
        int smws = First(0x02);
        Assert.NotEqual(0x0190, mine);                                   // not SMW's $01 (which starts at $0190)
        Assert.NotEqual(mine, smws);                                     // and SMW's $02 is still there
    }

    /// <summary>The NMI hook rejoins SMW's NMI at $0081AA. Asar takes a number after a branch as the offset itself, so a
    /// `BRA $0081AA` there assembled as `80 AA` and the game crashed on its first frame with sound (measured in Mesen); the
    /// tests above call Nmi directly and never ran the hook's own two instructions.</summary>
    [AsarFact]
    public void the_nmi_hook_rejoins_smws_nmi_after_its_sound_code()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var (rom, labels) = Inserted();
        Assert.Equal(0x22, rom.ReadByte(0x008179));                                       // JSL Nmi
        Assert.Equal(labels["Nmi"], rom.ReadValue(0x00817A, 3));
        Assert.Equal([0x80, 0x2B], rom.Data[rom.FileOffset(0x00817D)..][..2]);           // BRA to $0081AA
    }

    [Fact]
    public void smws_song_numbers_become_addmusicks()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var (rom, _) = Inserted();
        Assert.Equal(0x01, rom.ReadByte(0x00F60B));                                       // death: SMW's $09 is AddmusicK's Miss
        Assert.Equal(0x05, rom.ReadByte(0x01C586));                                       // the star
        Assert.Equal(new byte[] { 0x0B, 0x0F, 0x0A, 0x11, 0x10, 0x0C, 0x0E, 0x12 },        // the level header's eight songs
            Enumerable.Range(0, 8).Select(i => rom.ReadByte(0x0584DB + i)).ToArray());
        Assert.Equal(0x15, rom.ReadByte(0x0096C7));                                       // the title: the map's $01
        Assert.Equal(0x26, rom.ReadByte(0x0C9447));                                       // the staff roll: the ending's $09

        var patched = Rom.Load(ReferenceRoms.Vanilla);
        patched.Data[patched.FileOffset(0x00F60B)] = 0x30;                                // a patch asked for its own song
        var p = new ProjectFile();
        p.Music.Songs["a.mml"] = "#amk 2\n#0 c4";
        p.Music.SongList.Add(new MusicState.SongSlot { Number = 0x0A, Path = "a.mml" });
        var r = MusicInsert.Insert(patched, p);
        Assert.Equal(0x30, patched.ReadByte(0x00F60B));
        Assert.Contains(r.Warnings, w => w.Contains("$00F60B"));

        var foreign = Rom.Load(ReferenceRoms.Vanilla);
        foreign.Data[foreign.FileOffset(0x0080E8)] = 0x22;                                // someone else's JSL in SMW's upload
        var f = MusicInsert.Insert(foreign, p);
        Assert.False(f.Ok);
        Assert.Contains("$0080E8", Assert.Single(f.Errors));
    }

    [Fact]
    public void a_song_exports_as_an_spc_that_plays_from_the_file_alone()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var rom = Rom.Load(ReferenceRoms.Vanilla);
        const string mml = "#amk 2\n#SPC\n{\n#title \"Tune\"\n#author \"Someone\"\n#length \"1:05\"\n}\n#0 t60 @0 o4 c4 d4";
        var (spc, errors) = MusicInsert.Spc(rom, new ProjectFile(), "tune.mml", mml);
        Assert.True(spc is not null, string.Join("; ", errors));
        var f = SpcFile.Parse(spc.ToBytes());
        Assert.Equal(("Tune", "Someone", "Pipe Dream"), (f.Title, f.Artist, f.Dumper));
        var t = SpcTrace.Run(Spc700.FromSpc(f), 1_024_000);
        Assert.Equal([0x0C8A, 0x0E16], t.KeyOns.Where(k => k.Voice == 0).Take(2).Select(k => k.Pitch));
    }

    /// <summary>Frames of the game: the NMI's music part once a frame (17066 SPC cycles), what the SPC keyed on meanwhile.</summary>
    private static SpcTrace Frames(Cpu65816 cpu, Spc700 spc, int nmi, int frames)
    {
        var t = new SpcTrace();
        long start = spc.Cycles;
        for (int f = 0; f < frames; f++)
        {
            cpu.PresetWidths(true, true);
            cpu.PresetDbr(0);
            long before = spc.Cycles;
            cpu.CallLong(nmi);                                    // (an upload runs the SPC inside this)
            long after = spc.Cycles;
            t.Append(SpcTrace.Run(spc, Math.Max(0, 17_066 - (after - before))), after - start);
        }
        return t;
    }

    [AsarFact]
    public void a_built_rom_boots_our_engine_and_plays_its_songs_and_effects()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var (rom, labels) = Inserted();
        var spc = Spc700.PoweredOn();
        var cpu = Wired(rom, spc);
        cpu.CallNear(0x0080E8);                                   // power-on: UploadSPCEngine, now ours
        spc.Run(40_000);
        Assert.InRange(spc.Pc, 0x0400, 0x3000);                   // in our engine

        int nmi = labels["Nmi"];
        cpu.Ram7E[0x1DFB] = 0x0A;                                 // the level's song: uploaded, then played
        var a = Frames(cpu, spc, nmi, 90);
        Assert.Equal([0x0C8A, 0x0E16], a.KeyOns.Where(k => k.Voice == 0).Take(2).Select(k => k.Pitch));

        cpu.Ram7E[0x1DFB] = 0x0B;                                 // another: a new upload
        var b = Frames(cpu, spc, nmi, 60);
        Assert.Equal(0x01, b.KeyOns.First(k => k.Voice == 0).Srcn);

        cpu.Ram7E[0x1DFB] = 0x05;                                 // the global one: no upload, straight away
        var c = Frames(cpu, spc, nmi, 30);
        Assert.Equal(0x02, c.KeyOns.First(k => k.Voice == 0).Srcn);

        cpu.Ram7E[0x1DF9] = 0x01;                                 // a sound effect on voice 6
        var d = Frames(cpu, spc, nmi, 20);
        Assert.Contains(d.KeyOns, k => k.Voice == 6);

        cpu.Ram7E[0x1DF9] = 0xFF;                                 // hurry up: the tempo 10 faster
        Frames(cpu, spc, nmi, 5);
        Assert.Equal(10, spc.Ram[0xE2]);
        cpu.CallNear(0x008134);                                   // a level entered from the map: its song uploaded again,
        cpu.Ram7E[0x1DFB] = 0x0B;                                 // the engine started afresh
        var e = Frames(cpu, spc, nmi, 60);
        Assert.Equal(0, spc.Ram[0xE2]);
        Assert.Contains(e.KeyOns, k => k.Voice == 0 && k.Srcn == 0x01);

        cpu.Ram7E[0x1DFB] = 0x33;                                 // a song the ROM does not have: the music fades
        Frames(cpu, spc, nmi, 5);
        Assert.NotEqual(0, spc.Ram[0x1C]);                        // the global volume fade's ticks left
        Assert.Equal(0x0B, cpu.Ram7E[0x1DFD]);                    // and the song in ARAM is still the one there
        foreach (var (name, t) in new[] { ("a", a), ("b", b), ("c", c), ("d", d) })
            log.WriteLine($"{name}: {string.Join(" ", t.KeyOns.Take(6).Select(k => $"v{k.Voice}:{k.Pitch:X4}/{k.Srcn:X2}@{k.Cycle / 17066}"))}");
    }
}
