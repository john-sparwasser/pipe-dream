namespace PipeDream.Services;

// EditorSession — the overworld's colours: which ROM word a submap's swatch is loaded from, and
// editing that word. Lunar Magic's Overworld Palette Editor. The level's palette, and the stroke
// and history both tabs share, are EditorSession.Palette.cs.

public sealed partial class EditorSession
{
    /// <summary>The SNES address of the colour word behind a submap's swatch, or null for one the
    /// overworld does not set (<see cref="Palette.OverworldSource"/>).</summary>
    public int? OwPaletteSource(int submap, int index)
        => Rom is { } r && index is >= 0 and < 256 ? Palette.OverworldSource(r, submap, index) : null;

    /// <summary>Every submap a swatch's colour word also colours, this one included.</summary>
    public int[] OwPaletteSharers(int submap, int index)
        => Rom is { } r && OwPaletteSource(submap, index) is { } at ? Palette.OverworldSharers(r, index, at) : [];

    public bool IsOwPaletteEdited(int submap, int index)
        => Rom is { } r && OwPaletteSource(submap, index) is { } at && r.OwPaletteEdits.ContainsKey(at);

    public int OwPaletteEditCount => Rom?.OwPaletteEdits.Count ?? 0;

    /// <summary>
    /// Change the colour word behind a submap's swatch. Every submap that loads the same word
    /// changes with it — on vanilla most of the overworld's colours are one table for all seven,
    /// and that is the ROM's shape, not a choice made here. Same bargain as the level's
    /// <see cref="SetPaletteColor"/>: inside a stroke the history entry waits for the stroke to
    /// end, and putting the ROM's own colour back removes the edit.
    /// </summary>
    public bool SetOwPaletteColor(int submap, int index, ushort bgr)
    {
        if (Rom is not { } rom || Overworld is not { } ow || OwPaletteSource(submap, index) is not { } at) return false;
        if (ow.PaletteOf(submap).Bgr[index] == bgr) return false;
        ushort? after = bgr == Palette.RomWord(rom, at) ? null : bgr;
        if (stroke is { } s) s.TryAdd(at, Edited(at));
        else PushPalette([(at, Edited(at), after)]);
        SetOwEdit(at, after);
        OwPaletteChanged();
        return true;
    }

    /// <summary>Drop every overworld colour edit, all submaps at once — on vanilla they share most
    /// words, so a per-submap reset would reset its neighbours anyway. One history entry.</summary>
    public bool ResetOwPalette()
    {
        if (Rom is not { } rom || rom.OwPaletteEdits.Count == 0) return false;
        PushPalette([.. rom.OwPaletteEdits.Select(kv => (kv.Key, (ushort?)kv.Value, (ushort?)null))]);
        rom.OwPaletteEdits.Clear();
        OwPaletteChanged();
        return true;
    }

    /// <summary>The palette history holds both tabs' edits side by side: a level colour by its
    /// CGRAM index (0-FF), an overworld colour by its word's SNES address, which is never that low.</summary>
    private static bool IsOwKey(int key) => key > 0xFF;

    private void SetOwEdit(int at, ushort? value)
    {
        if (value is { } v) Rom!.OwPaletteEdits[at] = v;
        else Rom!.OwPaletteEdits.Remove(at);
    }

    /// <summary>After any change to the overworld's colours: into the project under its own key,
    /// and the map's cached pictures dropped so the next draw is in the new colours.</summary>
    private void OwPaletteChanged()
    {
        if (Project is not null)
        {
            Project.Data.Overworld.Palette = Rom!.OwPaletteEdits.ToDictionary(kv => kv.Key, kv => (int)kv.Value);
            Project.MarkDirty();
        }
        Overworld?.InvalidatePalette();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
