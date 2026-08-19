namespace PipeDream;

/// <summary>
/// Where Map16 tiles sit on screen: bank sizes, which tiles a bank can hold, and which slice
/// of the shared tile sheet belongs to it.
///
/// This is geometry, not UI. It lived on the ImGui editor class, which made it invisible to
/// any other front end and awkward to test — and "bank 1 renders nothing" shipped because of
/// exactly that. Both UIs now compute it here, so a second front end cannot disagree with the
/// first about where a tile is.
/// </summary>
public static class Map16Layout
{
    /// <summary>A bank is 0x2000 tiles shown 16 per row.</summary>
    public const int BankTiles = 0x2000, Cols = 16, BankRows = BankTiles / Cols;

    /// <summary>
    /// Whether a tile's page can be brought into existence. Banks 0-1 (tiles 0x200-0x3FFF)
    /// are the four lookup-ladder ranges EnsureMap16Tiles can allocate and prep v3 dispatches
    /// to; bank 2 is the BG table, a fixed 0x200 defs at $0D9100 that cannot grow at all.
    /// Whether a specific BASE honours a range is EnsureMap16Tiles' answer, not this one.
    /// </summary>
    public static bool CanAllocate(int tile) => tile is >= 0x200 and < 0x4000;

    /// <summary>Tiles of a bank the user may paint: an FG bank end to end (its top tile is
    /// allocatable, and painting an empty page is what creates it), anything else only where
    /// it is already backed.</summary>
    public static int PaintableIn(int bank, int realCount) =>
        CanAllocate(bank * BankTiles + BankTiles - 1) ? BankTiles : realCount;

    /// <summary>
    /// Which rows of the shared FG sheet belong to <paramref name="bank"/>, and how many of
    /// its tiles are allocated: (v0, v1, rows, count). Rows 0 means the bank shows nothing.
    ///
    /// The sheet is ONE image covering every allocated tile, so a bank is a window into it.
    /// Only bank 0 ever drew it once, which made every tile past 0x1FFF paintable and
    /// permanently invisible.
    /// </summary>
    public static (float V0, float V1, int Rows, int Count) SheetWindow(int bank, int sheetH, int tileCount)
    {
        if (bank is < 0 or > 1 || sheetH <= 0) return default;
        int rows = Math.Clamp(sheetH / 16 - bank * BankRows, 0, BankRows);
        if (rows <= 0) return default;
        int count = Math.Clamp(tileCount - bank * BankTiles, 0, BankTiles);
        return (bank * BankRows * 16f / sheetH, (bank * BankRows + rows) * 16f / sheetH, rows, count);
    }

    /// <summary>What to say over a page that CANNOT be painted. The FG banks never need this
    /// — an empty page there is drawn as ordinary empty tiles and comes into existence when
    /// painted — so this only explains the two genuinely fixed cases.</summary>
    public static string UnusedPageNote(int bank, int page) =>
        bank == 2 ? "BG definitions are a fixed table"
        : bank < 2 ? "empty — paint to fill it"
        : "past the supported 0x3FFF tiles";
}
