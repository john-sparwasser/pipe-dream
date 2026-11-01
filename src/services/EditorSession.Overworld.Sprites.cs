namespace PipeDream.Services;

// EditorSession — the overworld's Sprites tab: Lunar Magic's custom sprite list (Overworld.Sprites)
// placed, moved and changed by canvas cell, each edit landing in the ROM's edited copy and the
// project at once. The map itself is EditorSession.Overworld.cs.
public sealed partial class EditorSession
{
    /// <summary>The custom sprites on the overworld, every submap's, in list order.</summary>
    public IReadOnlyList<Overworld.OwSprite> OwSprites => Overworld?.Sprites ?? [];

    /// <summary>The canvas cell a sprite's top-left stands on: X and Y are 8px steps over its map,
    /// and the submap map sits shifted on the canvas as layer 2 does (<see cref="OwMapCell"/>).</summary>
    public static (int Col, int Row) OwSpriteCell(Overworld.OwSprite s)
        => s.Submap == 0 ? (s.X, s.Y)
         : ((s.X + OwSubDx) & (2 * Overworld.Cols - 1), 2 * Overworld.Rows + ((s.Y + OwSubDy) & (2 * Overworld.Rows - 1)));

    /// <summary>The sprite under a canvas cell, the last placed winning as it draws on top; -1 for
    /// none. A sprite is taken to be 16x16, the size nearly every one draws at.</summary>
    public int OwSpriteAt(int col, int row)
    {
        for (int i = OwSprites.Count - 1; i >= 0; i--)
        {
            var (c, r) = OwSpriteCell(OwSprites[i]);
            if (col - c is 0 or 1 && row - r is 0 or 1) return i;
        }
        return -1;
    }

    /// <summary>Place a new sprite at a canvas cell, in the list of the submap it lands on. Returns
    /// its index, or -1 with a reason off the maps or with that submap full.</summary>
    public (int Index, string? Why) AddOwSprite(int col, int row, int number)
    {
        if (Overworld is not { } ow || Rom is not { } rom) return (-1, "no ROM open");
        if (!OwMapCell(col, row, out int x, out int y, out bool sub)) return (-1, "that is off the map");
        int submap = sub ? Overworld.SubmapAtRow8(col, row - 2 * Overworld.Rows) : 0;
        if (ow.Sprites.Count(s => s.Submap == submap) >= Overworld.MaxSpritesPerSubmap)
            return (-1, $"{OwSubmapNames[submap]} already has {Overworld.MaxSpritesPerSubmap} sprites, Lunar Magic's limit");
        ow.Sprites.Add(new(submap, number & 0x7F, x, y, 0, new byte[Overworld.SpriteExtraCount(rom, number)]));
        SaveOwSprites();
        return (ow.Sprites.Count - 1, null);
    }

    /// <summary>Move a sprite to a canvas cell. It stays in its own list: a sprite dragged from one
    /// submap's corner to another's is still the first one's. False off its map.</summary>
    public bool MoveOwSprite(int index, int col, int row, bool save = true)
    {
        if (Overworld is not { } ow || (uint)index >= ow.Sprites.Count
            || !OwMapCell(col, row, out int x, out int y, out bool sub) || sub != (ow.Sprites[index].Submap != 0)) return false;
        ow.Sprites[index] = ow.Sprites[index] with { X = x, Y = y };
        if (save) SaveOwSprites();
        return true;
    }

    /// <summary>Replace a sprite's fields — the drawer's edits. The number decides how many extra
    /// bytes it carries, so they are cut or padded to fit.</summary>
    public void SetOwSprite(int index, Overworld.OwSprite s)
    {
        if (Overworld is not { } ow || Rom is not { } rom || (uint)index >= ow.Sprites.Count) return;
        var extra = new byte[Overworld.SpriteExtraCount(rom, s.Number)];
        s.Extra.AsSpan(0, Math.Min(extra.Length, s.Extra.Length)).CopyTo(extra);
        ow.Sprites[index] = s with { Number = s.Number & 0x7F, X = s.X & 0x3F, Y = s.Y & 0x3F, Z = s.Z & 0x1F, Extra = extra };
        SaveOwSprites();
    }

    public void RemoveOwSprite(int index)
    {
        if (Overworld is not { } ow || (uint)index >= ow.Sprites.Count) return;
        ow.Sprites.RemoveAt(index);
        SaveOwSprites();
    }

    /// <summary>The list into the project, packed as Lunar Magic packs it; the build writes it.</summary>
    public void SaveOwSprites()
    {
        if (Project is null || Overworld is not { } ow) return;
        Project.Data.Overworld.Sprites = [.. ow.Sprites];
        Project.MarkDirty();
    }

    /// <summary>The overworld sprites the project's code runs, number → library sprite (kind "overworld", Custom sprites).</summary>
    public IReadOnlyDictionary<int, string> OwSpriteCode
        => Project is null ? new Dictionary<int, string>() : OwSpriteInsert.Listed(Project.Data).ToDictionary(e => e.Key, e => e.Value.Name);

    /// <summary>The overworld sprite code changed (a sprite added, edited, slotted or removed): the extra bytes each number
    /// takes follow it. The placed sprites are read first, with the sizes in force before any code came in.</summary>
    private void RefreshOwSpriteExtra()
    {
        if (Project is null || Overworld is not { } ow || Rom is not { } rom) return;
        _ = ow.Sprites;
        rom.OwSpriteExtra = OwSpriteInsert.ExtraCounts(rom, Project.Data);
    }
}
