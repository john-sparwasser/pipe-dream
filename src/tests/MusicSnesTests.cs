using PipeDream.Music;
using Xunit;

namespace PipeDream.Tests;

/// <summary>The main CPU's side of the music: SMW's code and ours in the 65816 emulator, talking to the SPC700 interpreter
/// through the four ports, the SPC's boot ROM modelled from its documented behaviour.</summary>
public class MusicSnesTests
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
}
