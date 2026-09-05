namespace PipeDream.Services;

// EditorSession — where the player arrives: the main and midway entrance record, the 512
// global secondary entrances, and moving any of them on the canvas. The rest of the class:
// EditorSession.cs and the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- main entrance ----

    public MainEntrance? MainEntrance => Rom?.ReadMainEntrance(LevelNum);

    /// <summary>
    /// Write the main-entrance record. It is per level but lives OUTSIDE the level's data, in
    /// its own bank-05 tables, so like Map16 it is written straight into the session ROM and
    /// re-read from there at save time rather than being carried in the level state.
    /// </summary>
    public void ApplyEntry(MainEntrance entry)
    {
        if (Rom is null) return;
        var had = Rom.ReadMainEntrance(LevelNum);
        if (had == entry) return;
        // LM's level height trades width for height: W columns of LUT[H] bytes must fit the
        // 0x3800-byte tilemap, or the engine writes past RAM. Refuse rather than build a crash.
        if (entry.HeightIndex != had.HeightIndex && Header is { } hdr)
        {
            int px = Rom.HasLmLevelHeight
                ? Rom.ReadValue(Rom.LmLevelHeightTable + 0x200 + entry.HeightIndex * 2, 2) : 0x1B0;
            if (hdr.Screens * px > 0x3800)
            {
                Report($"{hdr.Screens} screens x {px:X} px does not fit the tilemap (max 0x3800) — height not changed");
                return;
            }
        }
        Rom.WriteMainEntrance(LevelNum, entry);
        if (Project is not null)
        {
            Project.Data.Level(LevelNum).MainEntrance = Convert.ToHexString(entry.ToBytes());
            Project.MarkDirty();
        }
        touched.Add(LevelNum);
        // A new height is a new canvas: the engine sizes its grid to it, so reparse like a header.
        if (entry.HeightIndex != had.HeightIndex) { StashCurrent(); ShowLevel(LevelNum); }
        // The Layer 3 Option picks which tilemap the level draws — including none — and the level
        // canvas draws it, so the picture has to follow the dropdown.
        else if (entry.Layer3Option != had.Layer3Option) RecomposeScene();
    }

    // ---- secondary entrances ----
    // The destination side of a secondary screen exit. There are 512, they are GLOBAL (any
    // level's exit may point at any index), and like Map16 definitions they are written straight
    // into the session ROM with the index recorded in the project — the bytes are re-read at save
    // time, so nothing has to be carried in the level state.

    public static int SecondaryEntranceCount => Rom.SecondaryEntranceCount;

    public SecondaryEntrance? ReadEntrance(int index)
        => Rom is { } r && index >= 0 && index < Rom.SecondaryEntranceCount
            ? r.ReadSecondaryEntrance(index) : null;

    /// <summary>
    /// Every entrance that lands in THIS level, as positions on the canvas: the main entrance,
    /// the midway one, and every secondary record pointing here.
    ///
    /// "Pointing here" is the low byte only on a vanilla base — a record's destination is 8 bits
    /// and its ninth comes from the submap the player crossed ($05F800's own doc), so $005 and
    /// $105 share a set. With Lunar Magic's secondary routine in, bit 3 of $05FE00 is that ninth
    /// bit and the match is exact.
    /// </summary>
    public IReadOnlyList<LevelEntrance> Entrances()
    {
        if (Rom is not { } rom || !HasLevel || MainEntrance is not { } main) return [];
        // Method 2 (LM's, prep v10's) reinterprets the record's two index nibbles as 16px steps;
        // otherwise they index vanilla's tables. Same record either way — the flag decides.
        var mainAt = main.Method2 != 0
            ? (EntrancePlacement.Method2X(main.ReservedMode, main.MarioX, main.XHigh),
               EntrancePlacement.Method2Y(main.MarioY, main.YHigh))
            : (EntrancePlacement.X(rom, main.ReservedMode, main.MarioX),
               EntrancePlacement.Y(rom, main.MarioY));
        // The midway carries only a screen and borrows the main's spot inside it — unless LM's
        // separate midway settings are on for this level, which give it a 16px position of its own.
        int midScreen = main.ReservedBoundary | (main.MidwayScreenHigh << 4);
        var midAt = main.MidwaySeparate != 0
            ? ((midScreen << 8) | (main.MidwayX << 4),                    // one nibble = X bits 4-7
               EntrancePlacement.Method2Y(main.MidwayY, main.MidwayYHigh))
            : main.Method2 != 0
            ? (EntrancePlacement.Method2X(midScreen, main.MarioX, main.XHigh), mainAt.Item2)
            : (EntrancePlacement.X(rom, midScreen, main.MarioX), mainAt.Item2);
        var list = new List<LevelEntrance>
        {
            new(EntranceKind.Main, LevelNum, mainAt.Item1, mainAt.Item2) { Free = rom.HasFreeEntrancePositions },
            new(EntranceKind.Midway, LevelNum, midAt.Item1, midAt.Item2) { Free = rom.HasFreeMidwayPosition },
        };
        bool secFree = rom.HasFreeSecondaryPositions;
        for (int i = 0; i < Rom.SecondaryEntranceCount; i++)
        {
            var e = rom.ReadSecondaryEntrance(i);
            if (e.DestinationLevel != (LevelNum & 0xFF)) continue;
            if (secFree && e.DestinationHigh != (LevelNum >> 8)) continue;
            var at = e.Method2 != 0
                ? (EntrancePlacement.Method2X(e.ReservedX, e.MarioX, e.XHigh), EntrancePlacement.Method2Y(e.MarioY, e.YHigh))
                : (EntrancePlacement.X(rom, e.ReservedX, e.MarioX), EntrancePlacement.Y(rom, e.MarioY));
            list.Add(new LevelEntrance(EntranceKind.Secondary, i, at.Item1, at.Item2) { Free = secFree });
        }
        return list;
    }

    /// <summary>
    /// Move an entrance to the nearest position the ROM can express: a 16px step with method 2,
    /// one of vanilla's 8 x 16 table spots without. Returns false when nothing changed —
    /// including a midway dragged within its own screen, which has nowhere to store the move
    /// (see <see cref="LevelEntrance.ScreenOnly"/>).
    /// </summary>
    public bool MoveEntrance(EntranceKind kind, int index, int px, int py)
    {
        if (Rom is not { } rom) return false;

        if (kind == EntranceKind.Secondary)
        {
            if (ReadEntrance(index) is not { } e) return false;
            if (rom.HasFreeSecondaryPositions)
            {
                var f = EntrancePlacement.Method2Fields(px, py);
                return WriteEntrance(index, e with { Method2 = 1, ReservedX = f.Screen, MarioX = f.XIndex,
                                                     XHigh = f.XHigh, MarioY = f.YIndex, YHigh = f.YHigh });
            }
            var (sScreen, sX) = EntrancePlacement.NearestX(rom, px);
            return WriteEntrance(index, e with { ReservedX = sScreen, MarioX = sX, MarioY = EntrancePlacement.NearestY(rom, py) });
        }

        if (MainEntrance is not { } main) return false;
        MainEntrance moved;
        if (kind == EntranceKind.Midway && rom.HasFreeMidwayPosition)
        {
            var f = EntrancePlacement.Method2Fields(px, py);
            // First opt-in: the separate record starts as a copy of what the midway had been
            // using — the main's action and FG/BG settings — so only the position changes.
            // MidwayYHigh bit 6 is what LM writes on every separate record; kept for parity.
            bool first = main.MidwaySeparate == 0;
            moved = main with
            {
                MidwaySeparate = 1, ReservedBoundary = f.Screen & 0x0F, MidwayScreenHigh = f.Screen >> 4,
                MidwayX = (px >> 4) & 0x0F, MidwayY = f.YIndex,
                MidwayYHigh = 0x40 | f.YHigh,
                MidwayAction = first ? main.EntranceAction : main.MidwayAction,
                MidwayFgBg = first ? main.VerticalScroll | (main.ScreenBoundaryY << 2) : main.MidwayFgBg,
            };
        }
        else if (kind == EntranceKind.Midway)
        {
            // A screen is all the midway record holds without LM's separate settings.
            moved = main with { ReservedBoundary = Math.Clamp(px, 0, 0x0FFF) >> 8 };
            if (moved == main)
            {
                Report("the midway entrance moves a screen at a time; it shares the main entrance's spot");
                return false;
            }
        }
        else if (rom.HasFreeEntrancePositions)
        {
            var f = EntrancePlacement.Method2Fields(px, py);
            moved = main with { Method2 = 1, ReservedMode = f.Screen, MarioX = f.XIndex, XHigh = f.XHigh,
                                MarioY = f.YIndex, YHigh = f.YHigh };
        }
        else
        {
            var (screen, xIndex) = EntrancePlacement.NearestX(rom, px);
            moved = main with { ReservedMode = screen, MarioX = xIndex, MarioY = EntrancePlacement.NearestY(rom, py) };
        }
        if (moved == main) return false;
        ApplyEntry(moved);
        return true;
    }

    /// <summary>Write one secondary entrance. Returns false when it already said that.</summary>
    public bool WriteEntrance(int index, SecondaryEntrance entrance)
    {
        if (Rom is not { } r || index < 0 || index >= Rom.SecondaryEntranceCount) return false;
        if (r.ReadSecondaryEntrance(index) == entrance) return false;
        r.WriteSecondaryEntrance(index, entrance);
        Project?.Data.Entrances.TryAdd(index.ToString("X3"), "");   // captured; bytes re-read at save
        Project?.MarkDirty();
        return true;
    }
}
