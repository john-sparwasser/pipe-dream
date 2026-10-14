using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// Debug ▸ Sprite tiles: build a sprite's look from 8x8/16x16 tiles picked off the SP sheets, and
/// write it to the sprite display table the level overlay and catalog draw from. The table was
/// captured by running each sprite's GFX routine, and some captures come out wrong; this fixes
/// them by eye instead of by hand-editing JSON. The ticked SP slots are the sprite's "gfx":
/// which GFX files power it. It edits by sprite number, so a custom sprite needs only a table
/// entry to use it too.
/// </summary>
public partial class SpriteTilesWindow : Window
{
    private readonly EditorSession session = null!;
    private readonly IReadOnlyList<(int Number, string Name)> entries = [];
    /// <summary>Unsaved edits by sprite number — what Save writes.</summary>
    private readonly Dictionary<int, (List<SpriteTile> Tiles, int[][] Gfx)> edits = [];
    private List<SpriteTile> tiles = [];
    /// <summary>Per SP slot, the files that power this sprite (empty = not needed).</summary>
    private int[][] gfx = [[], [], [], []];
    private int number = -1;
    private int[] files = new int[4];
    private bool loading;
    /// <summary>A move in progress: where the press was, and each picked tile's position then.</summary>
    private (Point At, (int I, int X, int Y)[] From)? drag;
    /// <summary>A lasso in progress, in preview pixels; Add keeps what was already picked.</summary>
    private (Point From, Point To, bool Add)? lasso;

    // The preview: a PreviewPx-square canvas, the placement cell's top-left at Origin.
    private const int PreviewPx = 128, OriginX = 48, OriginY = 64, PreviewScale = 3;
    private const int SheetScale = 2;

    public SpriteTilesWindow() => AvaloniaXamlLoader.Load(this);

    public SpriteTilesWindow(EditorSession session) : this()
    {
        this.session = session;
        entries = session.SpriteTableEntries();
        var list = this.GetControl<ListBox>("Sprites");
        list.ItemsSource = entries.Select(e => $"{e.Number:X2}  {e.Name}").ToList();
        list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) LoadSprite(entries[list.SelectedIndex].Number); };
        WireTileFields();
        WireFiles();
        WirePreview();
        WireSheet();
        list.SelectedIndex = 0;
    }

    private void LoadSprite(int num)
    {
        number = num;
        (tiles, gfx) = edits.TryGetValue(num, out var e) ? e : (session.SpriteTilesOf(num), session.SpriteGfxOf(num));
        loading = true;
        var set = this.GetControl<ComboBox>("SetBox");
        if (set.SelectedIndex != 0) set.SelectedIndex = 0;     // each sprite opens on its own files
        for (int slot = 0; slot < 4; slot++) this.GetControl<CheckBox>($"Need{slot}").IsChecked = gfx[slot].Length > 0;
        loading = false;
        ShowSlots();
        ShowFiles(session.SpriteTileFiles(num));
        RefreshTileList(tiles.Count > 0 ? 0 : -1);
    }

    private void ShowFiles(int[] f)
    {
        files = f;
        loading = true;
        for (int slot = 0; slot < 4; slot++) this.GetControl<TextBox>($"File{slot}").Text = $"{files[slot]:X2}";
        loading = false;
        RedrawSheet();
        RefreshTileList([Selected, .. Picked]);       // redraws the preview too; the row icons draw from these files
    }

    // ---- the selected tile ----

    private ListBox TileList => this.GetControl<ListBox>("Tiles");
    /// <summary>The primary tile — the one the fields and a sheet click act on. -1 when the list
    /// still holds the last sprite's pick: LoadSprite swaps tiles before it rebuilds the list.</summary>
    private int Selected => TileList.SelectedIndex is var i && i < tiles.Count ? i : -1;
    /// <summary>Every picked tile; a drag or nudge moves them all.</summary>
    private List<int> Picked => TileList.Selection.SelectedIndexes.Where(i => i >= 0 && i < tiles.Count).ToList();

    private void WireTileFields()
    {
        TileList.SelectionChanged += (_, _) => { if (!loading) { ShowTileFields(); RedrawPreview(); } };
        foreach (var n in new[] { "TileX", "TileY", "TilePal" })
            this.GetControl<NumericUpDown>(n).ValueChanged += (_, _) => FieldsChanged();
        foreach (var n in new[] { "TileBig", "TileXFlip", "TileYFlip" })
            this.GetControl<CheckBox>(n).IsCheckedChanged += (_, _) => FieldsChanged();
    }

    private void ShowTileFields()
    {
        if (Selected < 0) return;
        var t = tiles[Selected];
        loading = true;
        this.GetControl<NumericUpDown>("TileX").Value = t.X;
        this.GetControl<NumericUpDown>("TileY").Value = t.Y;
        this.GetControl<NumericUpDown>("TilePal").Value = t.Pal;
        this.GetControl<CheckBox>("TileBig").IsChecked = t.Big;
        this.GetControl<CheckBox>("TileXFlip").IsChecked = t.XFlip;
        this.GetControl<CheckBox>("TileYFlip").IsChecked = t.YFlip;
        loading = false;
        RedrawSheet();                                 // the sheet shows the tile's palette
    }

    private void FieldsChanged()
    {
        if (loading || Selected < 0) return;
        int Num(string n) => (int)(this.GetControl<NumericUpDown>(n).Value ?? 0);
        bool Check(string n) => this.GetControl<CheckBox>(n).IsChecked == true;
        int pal = Num("TilePal");
        bool palChanged = pal != tiles[Selected].Pal;
        Edit(Selected, tiles[Selected] with
        {
            X = Num("TileX"), Y = Num("TileY"), Pal = pal,
            Big = Check("TileBig"), XFlip = Check("TileXFlip"), YFlip = Check("TileYFlip"),
        });
        if (palChanged) RedrawSheet();
    }

    /// <summary>Replace tile i and record the sprite as edited.</summary>
    private void Edit(int i, SpriteTile t)
    {
        tiles[i] = t;
        Touched();
        RefreshTileList(i);
    }

    private void Touched()
    {
        edits[number] = (tiles, gfx);
        this.GetControl<Button>("SaveButton").IsEnabled = true;
        this.GetControl<TextBlock>("Status").Text = $"{edits.Count} sprite(s) edited, unsaved";
    }

    /// <summary>Rebuild the tile list and pick <paramref name="select"/> again, the first as primary.</summary>
    private void RefreshTileList(params int[] select)
    {
        loading = true;
        TileList.ItemsSource = tiles.Select(TileRow).ToList();
        TileList.Selection.Clear();
        foreach (int i in select.Select(i => Math.Min(i, tiles.Count - 1)).Where(i => i >= 0).Distinct())
            TileList.Selection.Select(i);
        loading = false;
        ShowTileFields();
        RedrawPreview();
    }

    /// <summary>A tile list row: the tile as drawn (palette and flips, an 8x8 centred in 16x16),
    /// then its fields.</summary>
    private Control TileRow(SpriteTile t)
    {
        int o = t.Big ? 0 : 4;
        var icon = new PixelImage
        {
            Zoom = 1.5,
            Source = LevelBitmap.FromPixels(session.DrawSpriteTiles([t with { X = 0, Y = 0 }], files, 16, 16, o, o), 16, 16),
        };
        var text = new TextBlock
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Text = $"{t.Tile:X3}  {(t.Big ? 16 : 8),2}  x{t.X,4} y{t.Y,4}  pal {t.Pal}{(t.XFlip ? "  xf" : "")}{(t.YFlip ? "  yf" : "")}",
        };
        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { icon, text } };
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (number < 0) return;
        tiles.Add(Selected >= 0 ? tiles[Selected] : new SpriteTile(0, 0, 0, 0, true));
        Touched();
        RefreshTileList(tiles.Count - 1);
    }

    private void OnDelete(object? sender, RoutedEventArgs e) => DeletePicked();

    private void DeletePicked()
    {
        var picked = Picked;
        if (picked.Count == 0) return;
        foreach (int i in picked.OrderByDescending(i => i)) tiles.RemoveAt(i);
        Touched();
        RefreshTileList(picked.Min());
    }

    private void OnUp(object? sender, RoutedEventArgs e) => Move(-1);
    private void OnDown(object? sender, RoutedEventArgs e) => Move(1);

    private void Move(int by)
    {
        int i = Selected, j = i + by;
        if (i < 0 || j < 0 || j >= tiles.Count) return;
        (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        Touched();
        RefreshTileList(j);
    }

    // ---- preview: pick, lasso, move ----

    private void WirePreview()
    {
        var host = this.GetControl<Panel>("PreviewHost");
        host.PointerPressed += (_, e) =>
        {
            host.Focus();                                  // arrows and Ctrl+A act on the preview
            var p = e.GetPosition(host);
            bool add = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control)
                    || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            int hit = TileAt(p);
            var picked = Picked;
            if (hit < 0) { lasso = (p, p, add); return; }
            if (add)                                       // toggle one tile in or out, no move
            {
                if (!picked.Remove(hit)) picked.Insert(0, hit);
                RefreshTileList([.. picked]);
                return;
            }
            // Grabbing a picked tile moves the whole pick; any other tile becomes the pick.
            if (!picked.Contains(hit)) picked = [hit];
            else { picked.Remove(hit); picked.Insert(0, hit); }
            RefreshTileList([.. picked]);
            drag = (p, picked.Select(i => (i, tiles[i].X, tiles[i].Y)).ToArray());
        };
        host.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(host);
            if (lasso is { } l) { lasso = l with { To = p }; RedrawPreview(); return; }
            if (drag is not { } d) return;
            int dx = (int)Math.Round((p.X - d.At.X) / PreviewScale), dy = (int)Math.Round((p.Y - d.At.Y) / PreviewScale);
            var (i0, x0, y0) = d.From[0];
            if (tiles[i0].X == x0 + dx && tiles[i0].Y == y0 + dy) return;
            foreach (var (i, x, y) in d.From) tiles[i] = tiles[i] with { X = x + dx, Y = y + dy };
            Touched();
            RefreshTileList([.. d.From.Select(f => f.I)]);
        };
        host.PointerReleased += (_, _) =>
        {
            drag = null;
            if (lasso is not { } l) return;
            lasso = null;
            var (x0, y0, x1, y1) = LassoRect(l.From, l.To);
            var inside = Enumerable.Range(0, tiles.Count).Where(i =>
            {
                var t = tiles[i]; int sz = t.Big ? 16 : 8;
                return t.X < x1 && t.X + sz > x0 && t.Y < y1 && t.Y + sz > y0;
            });
            RefreshTileList([.. (l.Add ? Picked.Concat(inside) : inside)]);
        };
        host.KeyDown += (_, e) =>
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 8 : 1;
            (int dx, int dy) = e.Key switch
            {
                Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0),
            };
            if (dx != 0 || dy != 0) Nudge(dx, dy);
            else if (cmd && e.Key == Key.A) RefreshTileList([.. Enumerable.Range(0, tiles.Count)]);
            else if (e.Key is Key.Delete or Key.Back) DeletePicked();
            else return;
            e.Handled = true;
        };
    }

    /// <summary>The front-most tile under a preview point — the earliest in the list — or -1.</summary>
    private int TileAt(Point p)
    {
        int px = (int)(p.X / PreviewScale) - OriginX, py = (int)(p.Y / PreviewScale) - OriginY;
        return tiles.FindIndex(t => px >= t.X && py >= t.Y && px < t.X + (t.Big ? 16 : 8) && py < t.Y + (t.Big ? 16 : 8));
    }

    /// <summary>A lasso's corners as a cell-relative pixel rect, x0/y0 inclusive, x1/y1 exclusive.</summary>
    private static (int X0, int Y0, int X1, int Y1) LassoRect(Point a, Point b)
    {
        int ax = (int)Math.Floor(a.X / PreviewScale) - OriginX, ay = (int)Math.Floor(a.Y / PreviewScale) - OriginY;
        int bx = (int)Math.Floor(b.X / PreviewScale) - OriginX, by = (int)Math.Floor(b.Y / PreviewScale) - OriginY;
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx) + 1, Math.Max(ay, by) + 1);
    }

    private void Nudge(int dx, int dy)
    {
        var picked = Picked;
        if (picked.Count == 0) return;
        foreach (int i in picked) tiles[i] = tiles[i] with { X = tiles[i].X + dx, Y = tiles[i].Y + dy };
        Touched();
        picked.Remove(Selected); picked.Insert(0, Selected);    // the primary stays primary
        RefreshTileList([.. picked]);
    }

    private void RedrawPreview()
    {
        var img = this.GetControl<PixelImage>("Preview");
        img.Source = LevelBitmap.FromPixels(session.DrawSpriteTiles(tiles, files, PreviewPx, PreviewPx, OriginX, OriginY), PreviewPx, PreviewPx);
        var overlay = this.GetControl<Canvas>("PreviewOverlay");
        overlay.Children.Clear();
        overlay.Children.Add(Box(OriginX, OriginY, 16, PreviewScale, UiColors.Band, dashed: true));
        foreach (int i in Picked)
        {
            var t = tiles[i];
            overlay.Children.Add(Box(OriginX + t.X, OriginY + t.Y, t.Big ? 16 : 8, PreviewScale, UiColors.Selection));
        }
        if (lasso is { } l)
        {
            var (x0, y0, x1, y1) = LassoRect(l.From, l.To);
            var r = new Rectangle { Width = (x1 - x0) * PreviewScale, Height = (y1 - y0) * PreviewScale,
                                    Stroke = UiColors.Selection, StrokeThickness = 1, StrokeDashArray = [4, 3], Fill = UiColors.SelectionFill };
            Canvas.SetLeft(r, (OriginX + x0) * PreviewScale); Canvas.SetTop(r, (OriginY + y0) * PreviewScale);
            overlay.Children.Add(r);
        }
        ShowSlots();
    }

    private static Rectangle Box(int x, int y, int size, int scale, IBrush stroke, bool dashed = false)
    {
        var r = new Rectangle { Width = size * scale, Height = size * scale, Stroke = stroke, StrokeThickness = dashed ? 1 : 2 };
        if (dashed) r.StrokeDashArray = [4, 3];
        Canvas.SetLeft(r, x * scale); Canvas.SetTop(r, y * scale);
        return r;
    }

    // ---- the SP sheet: pick a tile ----

    private void WireFiles()
    {
        var set = this.GetControl<ComboBox>("SetBox");
        set.ItemsSource = session.SpriteTileSetChoices();
        set.SelectedIndex = 0;
        set.SelectionChanged += (_, _) =>
        {
            if (loading || number < 0 || set.SelectedIndex < 0) return;
            ShowFiles(session.SpriteTileFiles(number, set.SelectedIndex));   // looking is not editing: gfx stays
        };
        for (int slot = 0; slot < 4; slot++)
        {
            int s = slot;
            var box = this.GetControl<TextBox>($"File{s}");
            box.TextChanged += (_, _) =>
            {
                if (loading || !int.TryParse(box.Text, NumberStyles.HexNumber, null, out int f) || f == files[s]) return;
                files[s] = f;
                NeedChanged(s);
                RedrawSheet();
                RefreshTileList([Selected, .. Picked]);
            };
            this.GetControl<CheckBox>($"Need{s}").IsCheckedChanged += (_, _) => { if (!loading) NeedChanged(s); };
        }
    }

    /// <summary>Re-read one slot's tick into gfx. A ticked slot needs the file on show — kept as the
    /// whole list when that file is already in it, since vanilla lists can carry several.</summary>
    private void NeedChanged(int slot)
    {
        if (number < 0) return;
        int[] need = this.GetControl<CheckBox>($"Need{slot}").IsChecked != true ? []
                   : gfx[slot].Contains(files[slot]) ? gfx[slot] : [files[slot]];
        if (need.SequenceEqual(gfx[slot])) return;
        gfx[slot] = need;
        Touched();
        ShowSlots();
    }

    // A pinned slot reads blue everywhere — its panel and its band on the sheet; a slot that needs a
    // second look (tiles in it but unpinned, or pinned to a file other than the one on show) amber.
    private static readonly IBrush PinnedFill = new SolidColorBrush(Color.FromArgb(0x28, 0x2E, 0x7F, 0xD4));
    private static readonly IBrush Caution = new SolidColorBrush(Color.Parse("#E8B04A"));

    /// <summary>Say, per slot, whether the sprite is pinned to it, to what, and how many of its tiles
    /// come from there; outline the pinned bands and the sprite's own tiles on the sheet.</summary>
    private void ShowSlots()
    {
        var uses = new int[4];
        foreach (var t in tiles) uses[(t.Tile & 0x1FF) >> 7]++;
        for (int slot = 0; slot < 4; slot++)
        {
            bool pinned = gfx[slot].Length > 0;
            string saved = string.Join(",", gfx[slot].Select(f => f.ToString("X2")));
            string here = uses[slot] == 1 ? "1 tile here" : $"{uses[slot]} tiles here";
            var note = this.GetControl<TextBlock>($"Note{slot}");
            (note.Text, note.Foreground) =
                pinned && !gfx[slot].Contains(files[slot]) ? ($"pinned to {saved}, showing {files[slot]:X2}", Caution)
              : pinned && uses[slot] == 0 ? ($"pinned to {saved}, but no tiles here", Caution)
              : pinned ? ($"pinned to {saved} · {here}", UiColors.Selection)
              : uses[slot] > 0 ? ($"{here}, not pinned", Caution)
              : ("not used", this.FindResource("TextDimBrush") as IBrush);
            var panel = this.GetControl<Border>($"Slot{slot}");
            panel.BorderBrush = pinned ? UiColors.Accent : UiColors.PickerEdge;
            panel.Background = pinned ? PinnedFill : null;
        }

        var bands = this.GetControl<Canvas>("BandOverlay");
        bands.Children.Clear();
        const int bandPx = 8 * 8 * SheetScale;
        for (int slot = 0; slot < 4; slot++)
            if (gfx[slot].Length > 0)
            {
                var r = new Rectangle { Width = 16 * 8 * SheetScale, Height = bandPx, Stroke = UiColors.Accent, StrokeThickness = 2, Fill = PinnedFill };
                Canvas.SetTop(r, slot * bandPx);
                bands.Children.Add(r);
            }
        foreach (var t in tiles)
            bands.Children.Add(Box(t.Tile % 16 * 8, (t.Tile & 0x1FF) / 16 * 8, t.Big ? 16 : 8, SheetScale, UiColors.Sprite));
    }

    private void WireSheet()
    {
        var host = this.GetControl<Panel>("SheetHost");
        host.PointerMoved += (_, e) => HoverSheet(e.GetPosition(host));
        host.PointerExited += (_, _) => this.GetControl<Canvas>("SheetOverlay").Children.Clear();
        host.PointerPressed += (_, e) =>
        {
            if (number < 0 || SheetTileAt(e.GetPosition(host)) is not { } t) return;
            this.GetControl<CheckBox>($"Need{t >> 7}").IsChecked = true;   // a tile from a slot means it needs that slot's file
            if (Selected >= 0) Edit(Selected, tiles[Selected] with { Tile = t });
            else
            {
                tiles.Add(new SpriteTile(0, 0, t, (int)(this.GetControl<NumericUpDown>("TilePal").Value ?? 0), true));
                Touched();
                RefreshTileList(tiles.Count - 1);
            }
        };
    }

    private void RedrawSheet()
    {
        int pal = Selected >= 0 ? tiles[Selected].Pal : (int)(this.GetControl<NumericUpDown>("TilePal").Value ?? 0);
        var (px, w, h) = session.SpFilesSheet(files, pal);
        var sheet = this.GetControl<PixelImage>("Sheet");
        var host = this.GetControl<Panel>("SheetHost");
        if (px.Length == 0) { sheet.Source = null; host.Width = host.Height = 0; return; }
        sheet.Source = LevelBitmap.FromPixels(px, w, h);
        host.Width = w * SheetScale; host.Height = h * SheetScale;
    }

    private static int? SheetTileAt(Point p)
        => Lasso.CellAt(p, 8 * SheetScale, 16, 32) is { } c ? c.Y * 16 + c.X : null;

    private void HoverSheet(Point p)
    {
        var overlay = this.GetControl<Canvas>("SheetOverlay");
        overlay.Children.Clear();
        var readout = this.GetControl<TextBlock>("Readout");
        if (SheetTileAt(p) is not { } t) { readout.Text = ""; return; }
        readout.Text = $"tile {t:X3}  (SP{t / 0x80 + 1})";
        bool big = Selected < 0 || tiles[Selected].Big;
        overlay.Children.Add(Box(t % 16 * 8, t / 16 * 8, big ? 16 : 8, SheetScale, UiColors.Selection));
    }

    // ---- save ----

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var status = this.GetControl<TextBlock>("Status");
        if (session.SaveSpriteTiles(edits) is { } problem) { status.Text = problem; return; }
        status.Text = $"saved {edits.Count} sprite(s) to the display table";
        edits.Clear();
        this.GetControl<Button>("SaveButton").IsEnabled = false;
    }

    private void OnRevert(object? sender, RoutedEventArgs e)
    {
        if (number < 0 || !edits.Remove(number)) return;
        this.GetControl<Button>("SaveButton").IsEnabled = edits.Count > 0;
        this.GetControl<TextBlock>("Status").Text = edits.Count > 0 ? $"{edits.Count} sprite(s) edited, unsaved" : "";
        LoadSprite(number);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
