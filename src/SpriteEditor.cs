using System.Numerics;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

// ---- sprite editing (sprite mode) ----
// Place/move/duplicate/delete sprites, the drag ghost, the overlay rebuild, sprite undo
// snapshots, and the Sprites palette tab + its catalog atlas.
internal sealed class SpriteEditor(EditorApp app)
{
    private readonly EditorApp app = app;

    internal Texture? sprGhostTex;                       // drag ghost: the selected sprites' pixels
    internal int sprGhostW, sprGhostH, sprGhostX, sprGhostY;   // ghost size + level-px origin
    internal HashSet<int>? hiddenSprites;                // sprites hidden from the canvas mid-drag

    // Sprite catalog (all insertable sprite numbers), LM-style "loaded only" filter.
    private Texture? catThumbTex;    // catalog thumbnail atlas
    private int[] catNumbers = Array.Empty<int>();
    private int[] levelSpFiles = new int[4];
    private bool catalogLoadedOnly = true;

    /// <summary>Construct a sprite at a display cell (inverse of Sprite.Cell).</summary>
    internal static Sprite SpriteAt(int number, int extra, int cx, int cy, bool vert, byte[]? extraBytes = null)
    {
        int abs = vert ? cy : cx, y = vert ? cx : cy;
        return new Sprite(Screen: (abs >> 4) & 0x1F, XNibble: abs & 15, Y: y & 0x1F,
                          Extra: extra, Number: number, ExtraBytes: extraBytes);
    }

    internal int? SpriteIndexAt(int cx, int cy, bool vert)
    {
        if (app.sprites is null) return null;
        for (int i = 0; i < app.sprites.Sprites.Count; i++)
            if (app.sprites.Sprites[i].Cell(vert) == (cx, cy)) return i;
        return null;
    }

    // A sprite changed at (around) this cell: recompose its neighborhood on next flush.
    internal void MarkSpriteCells(int cx, int cy)
    {
        for (int dy = -2; dy <= 4; dy++)
            for (int dx = -2; dx <= 4; dx++)
                app.canvas.MarkDirty(cx + dx, cy + dy);
    }

    internal void RebuildSpriteOverlay()
    {
        app.spriteOverlay = app.rom is not null && app.level is not null && app.sprites is not null
            ? SpriteOverlay.Build(app.rom, app.sprites, app.level.Header, app.levelNum) : null;
        app.levelDirty = true;
    }

    // Compose the selected sprites into one texture for the drag ghost (built when the
    // move starts, disposed when it ends).
    internal void BuildSpriteGhost()
    {
        DropSpriteGhost();
        if (app.spriteOverlay is null || app.selSprites.Count == 0) return;
        try
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (int i in app.selSprites)
                if (app.spriteOverlay.PixelBounds(i) is { } b)
                {
                    minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY);
                    maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY);
                }
            if (minX > maxX) return;                      // badge-only selection: no ghost
            int W = maxX - minX, H = maxY - minY;
            var img = new uint[W * H];
            var pal = app.paletteEditor.EditedPalette(0)!;
            foreach (int i in app.selSprites) app.spriteOverlay.DrawOne(i, img, W, H, pal, -minX, -minY);
            sprGhostTex = new Texture(app.GraphicsDevice, W, H, MemoryMarshal.AsBytes(img.AsSpan()));
            sprGhostW = W; sprGhostH = H; sprGhostX = minX; sprGhostY = minY;
        }
        catch { DropSpriteGhost(); }
    }

    internal void DropSpriteGhost()
    {
        sprGhostTex?.Dispose(); sprGhostTex = null;
    }

    // Un-hide sprites that were suppressed during a drag (their cells recompose).
    internal void ClearHiddenSprites()
    {
        if (hiddenSprites is null) return;
        if (app.sprites is not null)
        {
            bool vert = app.rom is not null && app.level is not null && app.rom.IsVerticalMode(app.level.Header.LevelMode);
            foreach (int i in hiddenSprites)
                if (i < app.sprites.Sprites.Count)
                {
                    var (cx, cy) = app.sprites.Sprites[i].Cell(vert);
                    MarkSpriteCells(cx, cy);
                }
        }
        hiddenSprites = null;
        app.levelDirty = true;
    }

    internal void PlaceSprite(int number, int cx, int cy, bool vert)
    {
        if (app.sprites is null) return;
        var before = new List<Sprite>(app.sprites.Sprites);
        app.sprites.Sprites.Add(SpriteAt(number, 0, cx, cy, vert));
        MarkSpriteCells(cx, cy);
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    internal void MoveSelectedSprites(int dx, int dy, bool vert)
    {
        if (app.sprites is null) return;
        var before = new List<Sprite>(app.sprites.Sprites);
        foreach (int i in app.selSprites)
        {
            var s = app.sprites.Sprites[i];
            var (cx, cy) = s.Cell(vert);
            MarkSpriteCells(cx, cy);
            app.sprites.Sprites[i] = SpriteAt(s.Number, s.Extra, cx + dx, cy + dy, vert, s.ExtraBytes);
            MarkSpriteCells(cx + dx, cy + dy);
        }
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    // Duplicate the selection with its top-left-most cell at the cursor; the copies
    // become the new selection (LM-style stamp-and-continue).
    internal void DuplicateSelection(int cx, int cy, bool vert)
    {
        if (app.sprites is null || app.selSprites.Count == 0) return;
        var before = new List<Sprite>(app.sprites.Sprites);
        var cells = app.selSprites.Select(i => (i, cell: app.sprites.Sprites[i].Cell(vert))).ToList();
        int ax = cells.Min(c => c.cell.X), ay = cells.Min(c => c.cell.Y);
        var added = new List<int>();
        foreach (var (i, cell) in cells)
        {
            var s = app.sprites.Sprites[i];
            int nx = cx + cell.X - ax, ny = cy + cell.Y - ay;
            added.Add(app.sprites.Sprites.Count);
            app.sprites.Sprites.Add(SpriteAt(s.Number, s.Extra, nx, ny, vert, s.ExtraBytes));
            MarkSpriteCells(nx, ny);
        }
        app.selSprites.Clear();
        foreach (int i in added) app.selSprites.Add(i);
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    internal void DeleteSelectedSprites(bool vert)
    {
        if (app.sprites is null || app.selSprites.Count == 0) return;
        var before = new List<Sprite>(app.sprites.Sprites);
        foreach (int i in app.selSprites.OrderByDescending(i => i))
        {
            var (cx, cy) = app.sprites.Sprites[i].Cell(vert);
            MarkSpriteCells(cx, cy);
            app.sprites.Sprites.RemoveAt(i);
        }
        app.selSprites.Clear();
        PushSpriteEdit(before);
        RebuildSpriteOverlay();
    }

    // Sprite/object/palette edits are before/after snapshots (the lists/dicts are small).
    internal void PushSpriteEdit(List<Sprite> before)
    {
        if (app.sprites is null) return;
        app.currentLevelTouched = true;
        var after = new List<Sprite>(app.sprites.Sprites);
        app.history.Push(() => RestoreSprites(before), () => RestoreSprites(after));
    }

    private void RestoreSprites(List<Sprite> list)
    {
        if (app.sprites is null) return;
        app.currentLevelTouched = true;
        bool vert = app.rom is not null && app.level is not null && app.rom.IsVerticalMode(app.level.Header.LevelMode);
        foreach (var s in app.sprites.Sprites.Concat(list))
        {
            var (cx, cy) = s.Cell(vert);
            MarkSpriteCells(cx, cy);
        }
        app.sprites.Sprites.Clear();
        app.sprites.Sprites.AddRange(list);
        app.selSprites.Clear();
        DropSpriteGhost();
        hiddenSprites = null;
        RebuildSpriteOverlay();
    }

    // Sprites palette tab: the level's sprite list. Selection is groundwork for sprite
    // placement editing later; today it's an inspector.
    // Sprites available to place in this level; "Loaded only" = LM's "sprites available
    // with the current sprite GFX" filter, from the table's per-slot file requirements.
    internal void DrawSpritesTab()
    {
        if (app.rom is null || app.level is null) { ImGui.TextDisabled("No level."); return; }
        ImGui.Checkbox("Loaded only", ref catalogLoadedOnly);
        ImGui.SameLine();
        ImGui.TextDisabled($"SP {string.Join(" ", levelSpFiles.Select(f => f.ToString("X2")))}");
        if (ImGui.BeginChild("sprcat"))
        {
            for (int i = 0; i < catNumbers.Length; i++)
            {
                int num = catNumbers[i];
                bool loaded = SpriteDisplay.IsLoaded(num, levelSpFiles);
                if (catalogLoadedOnly && !loaded) continue;
                if (catThumbTex is not null)
                {
                    ImGui.Image(app.imgui!.GetTextureID(catThumbTex), new Vector2(32, 32),
                                new Vector2(0, (float)i / catNumbers.Length),
                                new Vector2(1, (float)(i + 1) / catNumbers.Length));
                    ImGui.SameLine();
                }
                if (ImGui.Selectable($"{num:X2}  {SpriteDisplay.NameOf(num)}{(loaded ? "" : "  (GFX not loaded)")}###cat{num}",
                                     app.selectedCatalog == num, ImGuiSelectableFlags.None, new Vector2(0, 32)))
                    app.selectedCatalog = num;
            }
            ImGui.EndChild();
        }
    }

    // Catalog atlas: one thumbnail per table sprite, drawn with THIS level's GFX/palette.
    internal void BuildSpriteCatalog()
    {
        catThumbTex?.Dispose(); catThumbTex = null;
        catNumbers = Array.Empty<int>();
        if (app.rom is null || app.level is null) return;
        try
        {
            levelSpFiles = SpriteRender.ResolveSpFiles(app.rom, app.level.Header, app.levelNum);
            var sp = SpriteRender.LoadSpTiles(app.rom, app.level.Header, app.levelNum);
            var pal = app.paletteEditor.EditedPalette(0)!;
            catNumbers = SpriteDisplay.Numbers.ToArray();
            const int cell = 32;
            int n = catNumbers.Length;
            if (n == 0) return;
            var img = new uint[cell * cell * n];
            for (int i = 0; i < n; i++)
                if (SpriteDisplay.TryGet(catNumbers[i], out var rel))
                    SpriteRender.Draw(img, cell, cell * n,
                        rel.Select(o => o with { X = o.X + 8, Y = o.Y + i * cell + 16 }).ToList(), sp, pal);
            catThumbTex = new Texture(app.GraphicsDevice, cell, cell * n, MemoryMarshal.AsBytes(img.AsSpan()));
        }
        catch { catThumbTex?.Dispose(); catThumbTex = null; catNumbers = Array.Empty<int>(); }
    }
}
