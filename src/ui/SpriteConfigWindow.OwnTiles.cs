using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PipeDream.Ui;

// SpriteConfigWindow — the Own tiles tab: the sprite's own Map16 tiles, numbered from 0x300 on
// the Look tab's sheet. Each is four 8x8 tiles (top left, bottom left, top right, bottom right),
// a tile word each: YXPCCCTT TTTTTTTT, as FG Map16's. The window and the other tabs are
// SpriteConfigWindow.axaml.cs.
public partial class SpriteConfigWindow
{
    private static readonly string[] QuarterNames = ["top left", "bottom left", "top right", "bottom right"];

    /// <summary>The sprite's own Map16: 8 bytes a tile.</summary>
    private byte[] Own => config.Map16;
    private int OwnCount => Own.Length / 8;

    private ListBox OwnList => this.GetControl<ListBox>("OwnTiles");
    private ListBox QuarterList => this.GetControl<ListBox>("Quarters");
    private int OwnIndex => OwnList.SelectedIndex is var i && i < OwnCount ? i : -1;
    private int Quarter => QuarterList.SelectedIndex;

    private TileSheetView GfxView => this.GetControl<TileSheetView>("GfxSheet");

    private void WireOwnTiles()
    {
        OwnList.SelectionChanged += (_, _) => { if (!loading) ShowQuarters(Math.Max(Quarter, 0)); };
        QuarterList.SelectionChanged += (_, _) => { if (!loading) { ShowQuarterFields(); RedrawGfxSheet(); } };
        this.GetControl<TextBox>("QuarterTile").TextChanged += (_, _) => QuarterFieldsChanged();
        this.GetControl<NumericUpDown>("QuarterPal").ValueChanged += (_, _) => QuarterFieldsChanged();
        foreach (var n in new[] { "QuarterXFlip", "QuarterYFlip" })
            this.GetControl<CheckBox>(n).IsCheckedChanged += (_, _) => QuarterFieldsChanged();
        // A click gives the quarter its tile and steps on to the next, so four clicks build a tile.
        GfxView.Picked += t =>
        {
            if (OwnIndex < 0 || Quarter < 0) return;
            var (_, pal, xf, yf, rest) = Word(OwnIndex, Quarter);
            SetWord(OwnIndex, Quarter, t, pal, xf, yf, rest);
            OwnChanged(OwnIndex, Math.Min(Quarter + 1, 3));
        };
        GfxView.Hovered += t => this.GetControl<TextBlock>("GfxReadout").Text = t is not { } n ? "8x8 tiles: SP1-SP4, then GFX33"
            : n < 0x200 ? $"tile {n:X3}  (SP{n / 0x80 + 1})" : $"tile {n:X3}  (GFX33)";
    }

    private void ShowOwnTiles(int select = 0)
    {
        loading = true;
        OwnList.ItemsSource = Enumerable.Range(0, OwnCount).Select(OwnRow).ToList();
        OwnList.SelectedIndex = Math.Min(select, OwnCount - 1);
        loading = false;
        ShowQuarters(Math.Max(Quarter, 0));
    }

    private Control OwnRow(int i)
    {
        var icon = new PixelImage
        {
            Zoom = 2,
            Source = LevelBitmap.FromPixels(session.DrawSpriteDisplay([new PixiConfig.Tile(0, 0, OwnFrom + i)], Own, 16, 16, 0, 0), 16, 16),
        };
        var text = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Text = $"{OwnFrom + i:X3}" };
        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { icon, text } };
    }

    /// <summary>Quarter q of own tile i: its 8x8 tile, palette, flips, and the bit it keeps as is
    /// (priority).</summary>
    private (int Tile, int Pal, bool XFlip, bool YFlip, int Keep) Word(int i, int q)
    {
        int lo = Own[i * 8 + q * 2], hi = Own[i * 8 + q * 2 + 1];
        return (lo | (hi & 3) << 8, (hi >> 2) & 7, (hi & 0x40) != 0, (hi & 0x80) != 0, hi & 0x20);
    }

    private void SetWord(int i, int q, int tile, int pal, bool xf, bool yf, int rest)
    {
        Own[i * 8 + q * 2] = (byte)tile;
        Own[i * 8 + q * 2 + 1] = (byte)((tile >> 8) & 3 | (pal & 7) << 2 | rest | (xf ? 0x40 : 0) | (yf ? 0x80 : 0));
    }

    private void ShowQuarters(int select)
    {
        int i = OwnIndex;
        loading = true;
        if (i >= 0) ShowQuarterRows(i, select);
        else QuarterList.ItemsSource = new List<string>();
        loading = false;
        ShowQuarterFields();
        RedrawGfxSheet();
    }

    private void ShowQuarterFields()
    {
        bool on = OwnIndex >= 0 && Quarter >= 0;
        var (t, pal, xf, yf, _) = on ? Word(OwnIndex, Quarter) : default;
        loading = true;
        this.GetControl<TextBox>("QuarterTile").IsEnabled = on;
        this.GetControl<NumericUpDown>("QuarterPal").IsEnabled = on;
        foreach (var n in new[] { "QuarterXFlip", "QuarterYFlip" }) this.GetControl<CheckBox>(n).IsEnabled = on;
        this.GetControl<TextBox>("QuarterTile").Text = on ? $"{t:X3}" : "";
        this.GetControl<NumericUpDown>("QuarterPal").Value = pal;
        this.GetControl<CheckBox>("QuarterXFlip").IsChecked = xf;
        this.GetControl<CheckBox>("QuarterYFlip").IsChecked = yf;
        loading = false;
    }

    private void QuarterFieldsChanged()
    {
        if (loading || OwnIndex < 0 || Quarter < 0) return;
        var (tile, pal, _, _, rest) = Word(OwnIndex, Quarter);
        if (Hex(this.GetControl<TextBox>("QuarterTile").Text) is { } t and < 0x400) tile = t;
        bool palChanged = Num("QuarterPal") != pal;
        SetWord(OwnIndex, Quarter, tile, Num("QuarterPal"), Check("QuarterXFlip"), Check("QuarterYFlip"), rest);
        OwnChanged(OwnIndex, Quarter, fields: false);
        if (palChanged) RedrawGfxSheet();
    }

    /// <summary>Own tile i changed: redraw it everywhere it shows — its row, the Map16 sheet, the
    /// display — and land on quarter q. A field edit keeps the fields as typed.</summary>
    private void OwnChanged(int i, int q, bool fields = true)
    {
        Touched();
        loading = true;
        OwnList.ItemsSource = Enumerable.Range(0, OwnCount).Select(OwnRow).ToList();
        OwnList.SelectedIndex = i;
        loading = false;
        if (fields) ShowQuarters(q);
        else
        {
            loading = true;
            ShowQuarterRows(i, q);
            loading = false;
            MarkGfxSheet();
        }
        RedrawMap16Sheet();
        RefreshTileList([Selected, .. Picked]);
    }

    private void ShowQuarterRows(int i, int q)
    {
        QuarterList.ItemsSource = Enumerable.Range(0, 4).Select(n =>
        {
            var (t, pal, xf, yf, _) = Word(i, n);
            return $"{QuarterNames[n],-12} {t:X3}  pal {pal}{(xf ? "  xf" : "")}{(yf ? "  yf" : "")}";
        }).ToList();
        QuarterList.SelectedIndex = q;
    }

    /// <summary>The 8x8 sheet in the picked quarter's palette, its tile outlined.</summary>
    private void RedrawGfxSheet()
    {
        int pal = OwnIndex >= 0 && Quarter >= 0 ? Word(OwnIndex, Quarter).Pal : 0;
        var (px, w, h) = session.SpriteMap16Gfx(pal);
        GfxView.Show(px, w, h);
        MarkGfxSheet();
    }

    private void MarkGfxSheet()
    {
        GfxView.Marks.Children.Clear();
        if (OwnIndex >= 0 && Quarter >= 0) GfxView.Mark(Word(OwnIndex, Quarter).Tile, 1, UiColors.Accent);
    }

    private void OnAddOwn(object? sender, RoutedEventArgs e)
    {
        if (OwnCount >= 0x100) { this.GetControl<TextBlock>("Status").Text = "the sprite's own tiles end at 3FF"; return; }
        byte[] tile = OwnIndex >= 0 ? Own[(OwnIndex * 8)..(OwnIndex * 8 + 8)] : new byte[8];
        config.Map16 = [.. Own, .. tile];
        Touched();
        ShowOwnTiles(OwnCount - 1);
        RedrawMap16Sheet();
        RefreshTileList([Selected, .. Picked]);
    }

    /// <summary>Only the last goes: displays name own tiles by number, so removing one between
    /// would renumber the rest under them.</summary>
    private void OnRemoveOwn(object? sender, RoutedEventArgs e)
    {
        if (OwnCount == 0) return;
        config.Map16 = Own[..^8];
        Touched();
        ShowOwnTiles(OwnCount - 1);
        RedrawMap16Sheet();
        RefreshTileList([Selected, .. Picked]);
    }
}
