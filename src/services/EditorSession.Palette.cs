namespace PipeDream.Services;

// EditorSession — palette editing for the current level: CGRAM colour edits, the live
// recolour, strokes and history, and the eyedropper. The rest of the class: EditorSession.cs
// and the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- palette editing ----
    // CGRAM index → BGR555. Held per level and applied on every compose, which is why it lives
    // here and not in a control: the tile CACHES are built from the palette, so an edited colour
    // has to be in place before composition rather than tinted afterwards.

    private readonly Dictionary<int, ushort> paletteEdits = [];
    /// <summary>A palette change the project snapshot does not have yet. Set by every edit, undo
    /// and reset; cleared when the level is stashed or its edits are hydrated back in.</summary>
    private bool paletteDirty;

    public int PaletteEditCount => paletteEdits.Count;

    /// <summary>True when this level's colours come from an LM custom palette rather than being
    /// assembled from the header's palette fields — worth showing, because it changes what an
    /// edit will eventually be saved as.</summary>
    public bool HasCustomPalette => Rom?.LmCustomPalette(LevelNum) is not null;

    /// <summary>The level's 256 CGRAM colours as RGBA, edits included.</summary>
    public uint[] PaletteRgba => Scene?.Palettes[0]?.Rgba ?? new uint[256];

    /// <summary>One colour as the SNES stores it, BGR555.</summary>
    public ushort PaletteBgr(int index)
        => Scene?.Palettes[0] is { } p && index is >= 0 and < 256 ? p.Bgr[index] : (ushort)0;

    public bool IsPaletteEdited(int index) => paletteEdits.ContainsKey(index);

    /// <summary>A BGR555 colour as screen RGBA, so a swatch can be previewed without paying for
    /// a recompose.</summary>
    public static uint Rgba(ushort bgr) => Palette.ToRgba(bgr);

    /// <summary>
    /// Change one CGRAM colour and recompose. Returns false when nothing changed, so dragging a
    /// slider across a value it already has does not pay for a full recompose.
    ///
    /// Session-only until the LM custom-palette save path lands (CONTRACT §7e); the edit IS
    /// recorded in the project, so it survives save and reopen.
    /// </summary>
    public bool SetPaletteColor(int index, ushort bgr)
    {
        if (index is < 0 or > 255 || PaletteBgr(index) == bgr) return false;
        // Inside a stroke the history entry is deferred to EndPaletteStroke, so a whole session
        // with the picker open is ONE undo rather than one per colour the drag passed through.
        // Putting a colour back to what the ROM has there REMOVES the edit rather than recording
        // one that happens to match: otherwise the swatch keeps its edited marker, the level
        // stays dirty, and a picker opened and closed on the same colour leaves a history entry
        // that changes nothing.
        ushort? after = bgr == RomPaletteBgr(index) ? null : bgr;
        if (stroke is { } s) s.TryAdd(index, Edited(index));
        else PushPalette([(index, Edited(index), after)]);
        if (after is { } v) paletteEdits[index] = v; else paletteEdits.Remove(index);
        paletteDirty = true;
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Recolour(livePhaseOnly: InPaletteStroke);
        return true;
    }

    /// <summary>
    /// Repaint for the current palette edits WITHOUT rebuilding the scene. A colour cannot have
    /// changed the level's objects, sprites or graphics, so re-parsing them is pure cost —
    /// see <see cref="LevelScene.Repalette"/>. This is what makes dragging a colour live.
    /// </summary>
    private void Recolour(bool livePhaseOnly = false)
    {
        if (Rom is null || Scene is null) return;
        Scene.Repalette(Rom, LevelNum, paletteEdits, livePhaseOnly ? LivePhase : null);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Which animation phase the canvas is showing. Mid-drag only that one is worth
    /// recomposing; the rest catch up when the stroke ends.</summary>
    public int LivePhase { get; set; }

    // ---- palette strokes ----
    // A drag through the colour picker fires many colour changes and must land as ONE undo, the
    // same bargain every other editor here makes with its stroke. Open/close of the picker is
    // the boundary, which is exactly where the ImGui editor snapshotted too.

    /// <summary>Index → the value it had BEFORE the stroke started (null = no edit).</summary>
    private Dictionary<int, ushort?>? stroke;

    public bool InPaletteStroke => stroke is not null;

    public void BeginPaletteStroke() => stroke ??= [];

    /// <summary>Close the stroke and record its net effect as one history entry. A stroke that
    /// ended back on the colour it started from records nothing.</summary>
    public void EndPaletteStroke()
    {
        if (stroke is not { } s) return;
        stroke = null;
        var entry = s.Where(kv => Edited(kv.Key) != kv.Value)
                     .Select(kv => (kv.Key, kv.Value, Edited(kv.Key)))
                     .ToArray();
        if (entry.Length == 0) return;
        PushPalette(entry);
        Recolour();                    // the phases the drag skipped catch up here
    }

    /// <summary>Drop every palette edit on this level and go back to the ROM's colours.</summary>
    public bool ResetPalette()
    {
        if (paletteEdits.Count == 0) return false;
        // Reset is one history entry, so it is undoable rather than a cliff.
        PushPalette([.. paletteEdits.Select(kv => (kv.Key, (ushort?)kv.Value, (ushort?)null))]);
        paletteEdits.Clear();
        paletteDirty = true;
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Recolour();
        return true;
    }

    // ---- palette history ----
    // Same shape as Map16Edit and GfxEdit: an array of (where, before, after) per entry, applied
    // forwards for redo and backwards for undo. A colour is a scalar write, so it fits that
    // model exactly. `null` means "no edit at this index" — undoing back past a colour's FIRST
    // edit has to REMOVE the entry, not write the ROM's own colour in as an edit, or the swatch
    // would keep its edited marker and the level would count as touched forever.

    private readonly Stack<(int Index, ushort? Before, ushort? After)[]> palUndo = new();
    private readonly Stack<(int Index, ushort? Before, ushort? After)[]> palRedo = new();

    public int PaletteUndoDepth => palUndo.Count;
    public bool CanUndoPalette => palUndo.Count > 0;
    public bool CanRedoPalette => palRedo.Count > 0;

    private ushort? Edited(int index) => paletteEdits.TryGetValue(index, out var v) ? v : null;

    /// <summary>What the ROM itself has at this CGRAM index — the level's colours with no editor
    /// edits on top, including any LM custom palette.</summary>
    private ushort RomPaletteBgr(int index)
        => Rom is { } r && Scene is { } s && index is >= 0 and < 256
            ? Palette.Load(r, s.Level.Header, LevelNum).Bgr[index]
            : (ushort)0;

    private void PushPalette((int Index, ushort? Before, ushort? After)[] entry)
    {
        palUndo.Push(entry);
        palRedo.Clear();
    }

    public bool PaletteUndo() => StepPalette(palUndo, palRedo, redo: false);
    public bool PaletteRedo() => StepPalette(palRedo, palUndo, redo: true);

    private bool StepPalette(Stack<(int Index, ushort? Before, ushort? After)[]> from,
                             Stack<(int Index, ushort? Before, ushort? After)[]> to, bool redo)
    {
        if (from.Count == 0) return false;
        var entry = from.Pop();
        foreach (var (i, before, after) in entry)
        {
            if ((redo ? after : before) is { } c) paletteEdits[i] = c;
            else paletteEdits.Remove(i);
        }
        paletteDirty = true;
        to.Push(entry);
        Project?.MarkDirty();
        touched.Add(LevelNum);
        Recolour();
        return true;
    }

    /// <summary>
    /// The CGRAM index a composed pixel came from — the eyedropper. Goes through the Map16 tile
    /// rather than matching RGB alone: the same colour appears in several palette rows (black is
    /// in all of them), so "which entry is this tile actually using" is the only answer worth
    /// giving. The tile's quadrant word carries the palette row, and the colour is matched
    /// inside that row.
    ///
    /// Falls back to a search of all 256 when the pixel belongs to something drawn OVER the
    /// tile — a sprite, an overlay — whose row the layer-1 tile cannot know.
    /// </summary>
    public int? SampleCgramIndex(int px, int py)
    {
        if (Scene is not { } sc || sc.Palettes[0] is not { } pal) return null;
        if (px < 0 || py < 0 || px >= PxW || py >= sc.Height) return null;
        if (Phases.Length == 0 || Phases[0] is not { } pixels) return null;
        uint want = pixels[py * PxW + px];

        int tile = sc.Grid.Get(px / 16, py / 16);
        if (tile != Map16Grid.Empty && Map16?.ReadDef(tile) is { } def)
        {
            // Quadrants are stored in visual order: TL, TR, BL, BR.
            int quad = (py % 16 / 8) * 2 + (px % 16 / 8);
            int base16 = def[quad].Palette * 16;
            for (int c = 0; c < 16; c++)
                if (pal.Rgba[base16 + c] == want) return base16 + c;
        }
        for (int i = 0; i < 256; i++) if (pal.Rgba[i] == want) return i;
        return null;
    }
}
