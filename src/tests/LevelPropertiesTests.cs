using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Level properties: the five header bytes and the main entrance.
///
/// The behaviour worth pinning is that edits are STAGED. Every header field forces a full
/// reparse — the tileset drives object dispatch, the palette fields drive every tile cache —
/// so a dialog that applied live would reparse on each tick of a slider.
/// </summary>
public class LevelPropertiesTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(RomPath);

    [AvaloniaFact]
    public void cancel_applies_nothing()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(RomPath);
        var scene = LevelScene.Build(rom, 0x105, LevelScene.SpriteDraw.Skip);

        var dlg = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105), false, Services.LevelChoices.Default);
        dlg.Show();
        Dispatcher.UIThread.RunJobs();
        dlg.GetControl<Button>("RevertBtn");                   // exists

        // Closing without Apply leaves nothing staged for the caller to act on.
        dlg.Close();
        Assert.Null(dlg.AppliedHeader);
        Assert.Null(dlg.AppliedEntry);
        Assert.False(dlg.RevertRequested);
    }

    /// <summary>Revert is only offered once the level actually carries a header edit —
    /// otherwise there is nothing to revert TO.</summary>
    [AvaloniaFact]
    public void revert_is_disabled_until_the_level_has_a_header_override()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(RomPath);
        var scene = LevelScene.Build(rom, 0x105, LevelScene.SpriteDraw.Skip);

        var plain = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105), false, Services.LevelChoices.Default);
        plain.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(plain.GetControl<Button>("RevertBtn").IsEnabled);
        plain.Close();

        var edited = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105), true, Services.LevelChoices.Default);
        edited.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(edited.GetControl<Button>("RevertBtn").IsEnabled);
        edited.Close();
    }

    /// <summary>The header round-trips through its byte form: what the dialog shows is what
    /// would be written.</summary>
    [AvaloniaFact]
    public void the_readout_shows_the_bytes_that_would_be_written()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(RomPath);
        var scene = LevelScene.Build(rom, 0x105, LevelScene.SpriteDraw.Skip);
        var h = scene.Level.Header;

        var dlg = new LevelPropertiesWindow(h, rom.ReadMainEntrance(0x105), false, Services.LevelChoices.Default);
        dlg.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Convert.ToHexString(h.ToBytes()),
                        dlg.GetControl<TextBlock>("HeaderBytes").Text);
        dlg.Close();
    }

    /// <summary>
    /// The fields are grouped by what they DO, and each gets the control its values call for:
    /// a list of meanings is a list, a single bit is a checkbox, a bounded number that means
    /// only itself stays a number. The GFX sets are not here at all — they are the Graphics
    /// tab's Header button, beside the files they choose.
    /// </summary>
    [AvaloniaFact]
    public void every_field_gets_the_control_its_values_call_for()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(RomPath);
        var scene = LevelScene.Build(rom, 0x105, LevelScene.SpriteDraw.Skip);

        var dlg = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105),
                                            false, Services.LevelChoices.Of(rom));
        dlg.Show();
        Dispatcher.UIThread.RunJobs();
        var fields = dlg.GetControl<StackPanel>("Fields");

        string[] Labels<T>() where T : Control => [.. fields.Children.OfType<StackPanel>()
            .Where(r => r.Children.OfType<T>().Any())
            .Select(r => $"{r.Children.OfType<TextBlock>().First().Text}")];

        Assert.Equal(["Level", "Base palettes", "Play", "Scrolling", "Entry", "Sprites"],
                     fields.Children.OfType<TextBlock>().Select(t => $"{t.Text}"));
        // Named values: a list. Bare numbers: a spinner. Single bits: a box carrying its words.
        Assert.Equal(["Level mode", "Height", "Music", "Time", "Item memory",
                      "Layer 1 vertical", "Layer 2 rate", "Spawn range"], Labels<ComboBox>());
        Assert.Equal(["Screens", "FG palette", "BG palette", "Sprite palette", "Back area color",
                      "BG height (tiles)"], Labels<NumericUpDown>());
        Assert.Equal(["Skip the entrance walk", "Vertical entrance positioning", "Vertical level",
                      "Unknown vertical level", "Smart spawn"],
                     fields.Children.OfType<CheckBox>().Select(c => $"{c.Content}"));

        // The level's graphics are the Graphics tab's, and nothing here offers them.
        var everything = string.Join("|", Labels<ComboBox>().Concat(Labels<NumericUpDown>()));
        Assert.DoesNotContain("ileset", everything);
        Assert.DoesNotContain("prite set", everything);
        dlg.Close();
    }

    /// <summary>The choices name the value, and the names come from the ROM's own tables, so a
    /// hack that moved one is followed rather than guessed at.</summary>
    [AvaloniaFact]
    public void the_choices_are_named_from_the_roms_own_tables()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(RomPath);
        var c = Services.LevelChoices.Of(rom);

        // TimerTable $0584D7 is 00/02/03/04 — the timer's hundreds digit, 0 meaning no timer.
        Assert.Equal(["0 — no timer", "1 — 200", "2 — 300", "3 — 400"], c.Time);
        // LevelMusicTable $0584DB: eight track numbers, which is all the ROM knows them by.
        Assert.Equal("0 — track 02", c.Music[0]);
        // Layer 2's rate index picks from BOTH $05D720 (H) and $05D710 (V): index 3 fixes the
        // layer, index 0 is 1:2 across and 1:16 down.
        Assert.Equal("3 — H fixed, V fixed", c.Layer2Scroll[3]);
        Assert.Equal("0 — H 1:2, V 1:16", c.Layer2Scroll[0]);
        // VerticalTable $058417 bit 0 says which modes are vertical — 3,4,7,8,A,D — and the
        // second object pass says which carry level data on layer 2 rather than an image.
        Assert.Equal("0A — vertical", c.LevelMode[0x0A]);
        Assert.Equal("0C — horizontal", c.LevelMode[0x0C]);
        Assert.Equal("07 — vertical, layer 2 objects", c.LevelMode[0x07]);
        Assert.Equal("02 — horizontal, layer 2 objects", c.LevelMode[0x02]);
        Assert.Contains("do not use", c.LevelMode[0x15]);
        // A vanilla base has no LM level-height engine, so there is one height to choose.
        Assert.Equal(["0 — 27 rows (vanilla)"], c.Height);
    }

    /// <summary>Applying a header goes through the session, which reparses the level: the
    /// tileset alone changes which objects render, so a stale scene would be wrong.</summary>
    [Fact]
    public void applying_a_header_reparses_the_level()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(RomPath));
        s.ShowLevel(0x105);

        var before = s.Scene!.Level.Header;
        Assert.False(s.HasHeaderOverride);

        s.ApplyHeader(before with { BackAreaColor = (before.BackAreaColor + 1) & 7 });

        Assert.True(s.HasHeaderOverride);
        Assert.NotEqual(before.BackAreaColor, s.Scene!.Level.Header.BackAreaColor);

        s.RevertHeader();
        Assert.False(s.HasHeaderOverride);
        Assert.Equal(before.BackAreaColor, s.Scene!.Level.Header.BackAreaColor);
    }

    /// <summary>The main entrance lives outside the level's data, so it is written straight
    /// into the session ROM and read back from there.</summary>
    [Fact]
    public void applying_entry_settings_writes_them_into_the_rom()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(RomPath));
        s.ShowLevel(0x105);

        var e = s.Rom!.ReadMainEntrance(0x105);
        var changed = e with { MarioY = (e.MarioY + 1) & 15 };
        s.ApplyEntry(changed);

        Assert.Equal(changed, s.Rom!.ReadMainEntrance(0x105));
    }
}
