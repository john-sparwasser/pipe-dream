using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PipeDream.Ui;

/// <summary>
/// Map16 mode: the tile sheet canvas, the 8x8 CHR picker it stamps from, and the tile
/// properties inspector in the canvas header (acts-like, priority, palette). The canvas is
/// <see cref="Map16CanvasView"/>; definitions are edited through the session's Map16Edit.
/// </summary>
public partial class MainWindow
{
    private Map16CanvasView map16Canvas = null!;
    private ChrPaletteView chr = null!;
    private ToggleButton grain16 = null!, grain8 = null!;

    /// <summary>Which CGRAM row the 8x8 picker draws in and stamps with. ponytail: a constant
    /// while the Map16 drawer has no controls — the row picker, and the flip/priority flags that
    /// sat beside it, came off the drawer header pending a new home. The flags are still
    /// reachable on the canvas itself, where X / Y / P toggle them per quadrant.</summary>
    private const int ChrPalRow = 2;
    /// <summary>Map16 definition editing. Owned by the session, because it is rebuilt whenever
    /// the level's tileset changes and the window has no way to know when that happened.</summary>
    private Map16Edit? map16 => session.Map16;

    private TextBlock m16SelLabel = null!, m16ActsNote = null!, m16Unallocated = null!;
    private StackPanel m16Fields = null!;
    private TextBox m16Acts = null!;
    private Border m16ActsTip = null!;

    private TextBlock m16ActsTipText = null!, m16TileTipText = null!;
    private ToggleButton m16Priority = null!;
    private ComboBox m16Palette = null!;
    private Border m16PaletteBar = null!;
    private PaletteGridView m16Colors = null!;

    /// <summary>Guard so filling the fields from the selection does not read back as edits.</summary>
    private bool loadingM16Props;

    /// <summary>Map16 mode: the sheet, the 8x8 picker and the tile fields in the canvas header.</summary>
    private void WireMap16()
    {
        // ---- Map16 canvas mode ----
        map16Canvas = this.GetControl<Map16CanvasView>("Map16Canvas");
        chr = this.GetControl<ChrPaletteView>("Chr");
        grain16 = this.GetControl<ToggleButton>("Grain16");
        grain8 = this.GetControl<ToggleButton>("Grain8");
        chr.BrushChanged += (_, _) =>
        {
            AdoptChrBrush();
            // Picking an 8x8 tile IS the 8x8 grain — the pick means nothing at the other one, so
            // taking one switches rather than arming a brush that right-click will ignore.
            SetGrain(quad8: true);
        };
        map16Canvas.QuadPainted += (_, q) =>
        {
            if (map16 is null) return;
            // Painting an empty page CREATES it; the allocation relocates the def region, so
            // it has to happen before the quadrant offset is taken.
            if (map16.EnsurePage(q.Tile) is { } why) return;
            map16.StampQuad(q.Tile, q.Quad, GfxBrushWord(q.Bx, q.By));
        };
        map16Canvas.StrokeEnded += (_, _) => map16?.EndStroke();
        map16Canvas.QuadFlagToggled += (_, f) =>
        {
            if (map16?.ReadDef(f.Tile) is not { } def) return;
            map16.StampQuad(f.Tile, f.Quad, (ushort)(def[f.Quad].Raw ^ f.Bit));
            map16.EndStroke();
        };
        map16Canvas.TilePicked += (_, tile) =>
        {
            selLabel.Text = $"0x{tile:X4}";
            SetBrush(null, 1, 1);
        };
        // A refused move or copy wrote nothing, so there is no stroke to seal — and sealing is
        // what repaints the sheet, so the success path must not skip it.
        map16Canvas.MoveRequested += (_, m) =>
        {
            if (map16?.MoveTiles(map16Canvas.Bank, m.X, m.Y, m.W, m.H, m.Dx, m.Dy) is not null) return;
            map16?.EndStroke();
        };
        map16Canvas.DuplicateRequested += (_, m) =>
        {
            if (map16?.CopyQuads(map16Canvas.Bank, m.X, m.Y, m.W, m.H, m.Dx, m.Dy) is not null) return;
            map16?.EndStroke();
        };
        // Subscribed once, on the session: a committed definition change invalidates the tile
        // caches behind the level, the picker and the sheet alike.
        session.Map16Committed += (_, _) => OnMap16Committed();

        // ---- Map16 tile fields, in the canvas header ----
        m16SelLabel = this.GetControl<TextBlock>("M16SelLabel");
        m16Fields = this.GetControl<StackPanel>("M16Fields");
        m16Unallocated = this.GetControl<TextBlock>("M16Unallocated");
        m16Acts = this.GetControl<TextBox>("M16Acts");
        m16ActsTip = this.GetControl<Border>("M16ActsTip");
        m16ActsTipText = this.GetControl<TextBlock>("M16ActsTipText");
        m16TileTipText = this.GetControl<TextBlock>("M16TileTipText");
        map16Canvas.PointerMoved += (_, e) => ShowActsTip(e.KeyModifiers);
        map16Canvas.PointerExited += (_, _) => m16ActsTip.IsVisible = false;
        m16ActsNote = this.GetControl<TextBlock>("M16ActsNote");
        m16Priority = this.GetControl<ToggleButton>("M16Priority");
        m16Palette = this.GetControl<ComboBox>("M16Palette");
        for (int i = 0; i < 8; i++) m16Palette.Items.Add($"{i}");
        m16PaletteBar = this.GetControl<Border>("M16PaletteBar");
        m16Colors = this.GetControl<PaletteGridView>("M16Colors");
        m16Colors.Rows = 1;
        m16Colors.Cell = 20;
        m16Colors.Selectable = false;      // it shows the row, it does not choose within it

        map16Canvas.SelectionChanged += (_, _) => RefreshMap16Props();
        map16Canvas.TilePicked += (_, _) => RefreshMap16Props();
        // Clicking the desk beside the sheet drops the selection. The canvas is only as wide as
        // the tile column, so a click that misses it never reaches it at all — the miss has to be
        // caught out here. A scrollbar is not a miss.
        this.GetControl<ScrollViewer>("Map16Scroll").PointerPressed += (_, e) =>
        {
            if (e.Source is not Control src || ReferenceEquals(src, map16Canvas)
                || src.FindAncestorOfType<ScrollBar>() is not null) return;
            map16Canvas.ClearSelection();
            chr.ClearSelection();          // one deselect covers both surfaces, at either grain
            AdoptChrBrush();               // ...including the footprint the cursor was drawing
            RefreshMap16Props();
        };
        // Committed on Enter or on leaving the field, not per keystroke: half a hex number is
        // still a number, and every commit rewrites the ROM.
        m16Acts.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyM16Acts(); e.Handled = true; } };
        m16Acts.LostFocus += (_, _) => ApplyM16Acts();
        m16Priority.IsCheckedChanged += (_, _) =>
        {
            if (loadingM16Props) return;
            bool on = m16Priority.IsChecked == true;
            map16?.Transform(map16Canvas.SelectedTiles(),
                             w => (ushort)(on ? w.Raw | 0x2000 : w.Raw & ~0x2000));
        };
        m16Palette.SelectionChanged += (_, _) =>
        {
            if (loadingM16Props || m16Palette.SelectedIndex < 0) return;
            int row = m16Palette.SelectedIndex;
            map16?.Transform(map16Canvas.SelectedTiles(),
                             w => (ushort)((w.Raw & ~0x1C00) | (row << 10)));
            RefreshM16Colors(row);
        };

        var m16Pages = this.GetControl<ToggleButton>("M16Pages");
        m16Pages.IsCheckedChanged += (_, _) =>
        {
            map16Canvas.ShowPages = m16Pages.IsChecked == true;
            map16Canvas.InvalidateVisual();
        };
    }

    /// <summary>Which tile is hovered is already in the header, so the gutter spends its width on
    /// the thing nothing else says: what the tile's acts-as code makes it DO. Codes the table has
    /// nothing sourced for read as the bare number — see <see cref="ActsAs"/>.</summary>
    private string Map16Readout()
    {
        if (map16Canvas.HoverQuad is not { } h || map16 is not { } m16) return "";
        if (m16.ReadDef(h.Tile) is null) return "unallocated";
        // Same two reasons the header greys its acts-as box: say which, rather than going blank
        // and leaving the gutter looking broken.
        if (m16.ActsAs(h.Tile) is not { } a)
            return h.Tile >= 0x4000 ? "acts-like: n/a for BG tiles" : "acts-like: no LM table";
        string what = ActsAs.Describe(a);
        return what.Length > 0 ? $"acts 0x{a:X3}  {what}" : $"acts 0x{a:X3}";
    }

    // ---- the sheet and the 8x8 picker ----

    private void RefreshMap16Sheet()
    {
        if (!session.HasLevel) return;
        var (px, w, h) = session.SheetPhases();
        map16Canvas.SetSheet(px, w, h, session.Map16TileCount);
        map16Canvas.SetPlaceholder(session.PlaceholderPhases());
        var (bgPx, bgW, bgH) = session.BgSheetPhases();
        map16Canvas.SetBgSheet(bgPx, bgW, bgH);
        map16Canvas.Bank = Math.Max(0, bankBox.SelectedIndex);
        RebuildChrSheet();
    }

    private void RebuildChrSheet()
    {
        var (px, w, h) = session.ChrPhases(ChrPalRow);
        if (px[0] is not null) chr.SetSheet(px, w, h);
    }

    /// <summary>Rebuild everything a committed Map16 edit invalidates: the tile caches feed
    /// both the level canvas and the picker, so a def change has to reach all three.</summary>
    private void OnMap16Committed()
    {
        if (!session.HasLevel) return;
        session.RecomposeAfterMap16();
        AdoptSession();
        RefreshMap16Sheet();
    }

    /// <summary>The Map16 cursor draws the 8x8 brush's footprint, so it follows the picker —
    /// when the pick changes and when it is dropped.</summary>
    private void AdoptChrBrush()
    {
        map16Canvas.BrushW = chr.Brush.W;
        map16Canvas.BrushH = chr.Brush.H;
        map16Canvas.InvalidateVisual();
    }

    /// <summary>Radio behaviour without a group, as the canvas modes do it: exactly one grain.
    /// The canvas is the one that acts on it — this pair is only how it gets said.</summary>
    private void OnGrain(object? sender, RoutedEventArgs e) => SetGrain(ReferenceEquals(sender, grain8));

    private void SetGrain(bool quad8)
    {
        grain8.IsChecked = quad8;
        grain16.IsChecked = !quad8;
        map16Canvas.Grain = quad8 ? Map16CanvasView.TileGrain.Quad8 : Map16CanvasView.TileGrain.Tile16;
        map16Canvas.InvalidateVisual();
    }

    /// <summary>
    /// The Map16 word a brush cell stamps: the 8x8 tile number in the low 10 bits, then the
    /// palette row. This packing IS the Map16 format (CONTRACT §5), which is why the row lives
    /// with the brush rather than being applied afterwards — and why the flip and priority bits
    /// belong here too, once <see cref="ChrPalRow"/>'s controls have somewhere to live again.
    /// </summary>
    private ushort GfxBrushWord(int bx, int by)
        => (ushort)((chr.TileOfBrushCell(bx, by) & 0x3FF) | (ChrPalRow << 10));

    // ---- Map16 properties inspector ----

    /// <summary>
    /// Show the selected tile's properties. The controls reflect the FIRST tile of a selection and
    /// apply to all of it — the ImGui behaviour, and the only sane one when a lasso can cover
    /// tiles that disagree.
    /// </summary>
    private void RefreshMap16Props()
    {
        if (map16 is not { } m16) return;
        var tiles = map16Canvas.SelectedTiles().ToList();

        // Nothing selected: the row stays put, greyed and blanked out. Hiding it would take the
        // bar's height with it, and there is nothing to say about a tile that is not there.
        m16SelLabel.IsEnabled = m16Fields.IsEnabled = tiles.Count > 0;
        if (tiles.Count == 0)
        {
            loadingM16Props = true;
            m16SelLabel.Text = "Tile ######";
            m16Acts.Text = "-";
            m16ActsNote.Text = "";
            m16Priority.IsChecked = false;
            m16Palette.SelectedIndex = -1;
            RefreshM16Colors(-1);
            m16Fields.IsVisible = true;
            m16Unallocated.IsVisible = false;
            loadingM16Props = false;
            return;
        }

        int first = tiles[0];
        m16SelLabel.Text = tiles.Count > 1
            ? $"{tiles.Count} tiles selected"
            : $"Tile 0x{first:X4}";

        var def = m16.ReadDef(first);
        m16Fields.IsVisible = def is not null;
        m16Unallocated.IsVisible = def is null;
        if (def is null) return;

        loadingM16Props = true;
        // Acts-like is an FG concept and needs LM's table; say which is missing rather than
        // showing a box that does nothing.
        bool acts = m16.HasActsAs && first < 0x4000;
        m16Acts.IsEnabled = acts;
        m16Acts.Text = m16.ActsAs(first) is { } a ? $"{a:X3}" : "";
        m16ActsNote.Text = acts ? "" : first >= 0x4000 ? "n/a for BG tiles" : "no LM acts-like table";
        m16Priority.IsChecked = def[0].Priority;
        m16Palette.SelectedIndex = def[0].Palette;
        RefreshM16Colors(def[0].Palette);
        loadingM16Props = false;
    }

    private void ApplyM16Acts()
    {
        if (loadingM16Props || map16 is not { } m16) return;
        if (!int.TryParse(m16Acts.Text, System.Globalization.NumberStyles.HexNumber, null, out int v))
        { RefreshMap16Props(); return; }
        m16.SetActsAs(map16Canvas.SelectedTiles(), v);
    }

    private void OnFlipX(object? sender, RoutedEventArgs e) => FlipM16(vertical: false);
    private void OnFlipY(object? sender, RoutedEventArgs e) => FlipM16(vertical: true);

    private void FlipM16(bool vertical)
    {
        map16?.Flip(map16Canvas.SelectedTiles(), vertical);
        RefreshMap16Props();
    }

    /// <summary>The sixteen colours a palette row draws with, in the Map16 gutter. Index 0 keeps
    /// the sheet's grey convention — in a tile it means transparent, and a black swatch would read
    /// as black. A row of -1 (nothing selected) leaves the strip empty rather than showing row 0's
    /// colours as if they were the selection's.</summary>
    private void RefreshM16Colors(int row)
    {
        var colors = new uint[16];
        if (row >= 0 && session.PaletteRgba is { } pal && pal.Length >= (row + 1) * 16)
            for (int i = 0; i < 16; i++)
                colors[i] = i == 0 ? 0xFF303030u : pal[row * 16 + i];
        m16Colors.Cols = 16;
        m16Colors.Colors = colors;
        m16Colors.InvalidateVisual();
    }

    /// <summary>The hovered tile: what it is in this level's tileset, when the table knows, over
    /// what it acts as, "ID - description". A custom tile has no sentence of its own, so it
    /// borrows the one for the tile it acts as — a block set to act as 130 reads as the cement
    /// block. Nothing when there is no tile or no table under the pointer.
    ///
    /// The card sits in the canvas's lower-right corner, out of the way; with Alt or Cmd held it
    /// moves up and to the right of the hovered tile instead, to be read while sweeping tiles.
    /// </summary>
    private void ShowActsTip(KeyModifiers mods)
    {
        int? acts = map16 is { } m16 && map16Canvas.HoverQuad is { } h ? m16.ActsAs(h.Tile) : null;
        m16ActsTip.IsVisible = acts is not null;
        if (acts is not { } a) return;
        int tile = map16Canvas.HoverQuad!.Value.Tile;
        string name = Map16Tiles.Describe(tile, session.Tileset);
        if (name.Length == 0 && a != tile) name = Map16Tiles.Describe(a, session.Tileset);
        m16TileTipText.Text = name;
        m16TileTipText.IsVisible = name.Length > 0;
        string what = ActsAs.Describe(a);
        m16ActsTipText.Text = what.Length == 0 ? $"{a:X3}" : $"{a:X3} - {what}";
        PlaceActsTip(tile, ZoomChord(mods));
    }

    /// <summary>Where the card goes: the corner, or beside the tile when <paramref name="byTile"/>.
    /// Beside means its left edge just right of the tile and its bottom edge just above it,
    /// anchored by margins so no measurement of the card is needed; it is kept inside the desk
    /// so a tile at the right edge does not push it off screen.</summary>
    private void PlaceActsTip(int tile, bool byTile)
    {
        var desk = this.GetControl<Panel>("Map16Desk");
        if (!byTile)
        {
            m16ActsTip.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            m16ActsTip.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            m16ActsTip.Margin = new Thickness(14);
            return;
        }
        int idx = tile % Map16Layout.BankTiles;
        double ts = 16 * map16Canvas.Zoom;
        var corner = new Point((idx % Map16Layout.Cols + 1) * ts - map16Canvas.Origin.X, idx / Map16Layout.Cols * ts - map16Canvas.Origin.Y);
        var at = map16Canvas.TranslatePoint(corner, desk) ?? corner;
        const double gap = 6;
        double left = Math.Max(0, Math.Min(at.X + gap, desk.Bounds.Width - m16ActsTip.Bounds.Width));
        double bottom = Math.Max(0, desk.Bounds.Height - at.Y + gap);
        m16ActsTip.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        m16ActsTip.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        m16ActsTip.Margin = new Thickness(left, 0, 0, bottom);
    }
}
