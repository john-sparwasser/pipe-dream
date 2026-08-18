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
        var scene = LevelScene.Build(rom, 0x105, showSprites: false);

        var dlg = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105), false);
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
        var scene = LevelScene.Build(rom, 0x105, showSprites: false);

        var plain = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105), false);
        plain.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(plain.GetControl<Button>("RevertBtn").IsEnabled);
        plain.Close();

        var edited = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105), true);
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
        var scene = LevelScene.Build(rom, 0x105, showSprites: false);
        var h = scene.Level.Header;

        var dlg = new LevelPropertiesWindow(h, rom.ReadMainEntrance(0x105), false);
        dlg.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Convert.ToHexString(h.ToBytes()),
                        dlg.GetControl<TextBlock>("HeaderBytes").Text);
        dlg.Close();
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
