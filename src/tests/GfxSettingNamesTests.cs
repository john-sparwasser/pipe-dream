using Xunit;

namespace PipeDream.Tests;

public class GfxSettingNamesTests
{
    /// <summary>One name per setting 0-F, so a hand edit of the tables cannot shift every name
    /// onto the wrong setting.</summary>
    [Fact]
    public void each_list_names_all_sixteen_settings()
    {
        Assert.Equal(16, GfxSettingNames.Layer1.Length);
        Assert.Equal(16, GfxSettingNames.Sprites.Length);
    }

    /// <summary>The graphics-header dialog shows a setting's name between its number and its files.</summary>
    [Fact]
    public void the_header_choices_carry_the_name_when_there_is_one()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = new PipeDream.Services.EditorSession();
        s.OpenRom(ReferenceRoms.Vanilla);
        string was = GfxSettingNames.Layer1[1];
        try
        {
            GfxSettingNames.Layer1[1] = "Test name";
            var (layer1, sprites) = s.GfxHeaderChoices();
            Assert.Matches(@"^1 — Test name · GFX [0-9A-F]{2} [0-9A-F]{2} [0-9A-F]{2} [0-9A-F]{2}$", layer1[1]);
            Assert.Equal(16, sprites.Count);
        }
        finally { GfxSettingNames.Layer1[1] = was; }
    }
}
