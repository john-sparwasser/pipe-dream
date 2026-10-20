using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PipeDream.Ui;

// SpriteConfigWindow — the Look tab: the displays Lunar Magic picks between for a placement, and
// each display's Map16 tiles, arranged on the preview and picked off the sprite Map16 sheet. The
// window and the other tabs are SpriteConfigWindow.axaml.cs.
public partial class SpriteConfigWindow
{
    /// <summary>The sprite's own Map16 tiles number from here.</summary>
    private const int OwnFrom = 0x300;

    private void WireLook()
    {
        WireDisplays();
        WireTileFields();
        WirePreview();
        WireMap16Sheet();
    }

    private void ShowLook()
    {
        loading = true;
        this.GetControl<ComboBox>("DisplayBy").SelectedIndex = config.DisplayByExtraByte ? 1 : 0;
        loading = false;
        ShowDisplayList(0);
        RedrawMap16Sheet();
    }

    // ---- displays ----

    private ListBox DisplayList => this.GetControl<ListBox>("Displays");
    private PixiConfig.Display? Display
        => DisplayList.SelectedIndex is var i && i >= 0 && i < config.Displays.Count ? config.Displays[i] : null;

    private void WireDisplays()
    {
        this.GetControl<ComboBox>("DisplayBy").SelectionChanged += (_, _) =>
        {
            bool byByte = this.GetControl<ComboBox>("DisplayBy").SelectedIndex == 1;
            if (loading || byByte == config.DisplayByExtraByte) return;
            config.DisplayByExtraByte = byByte;
            Touched();
            ShowDisplayList(DisplayList.SelectedIndex);
        };
        DisplayList.SelectionChanged += (_, _) => { if (!loading) { ShowDisplayFields(); RefreshTileList(0); } };
        this.GetControl<TextBox>("DisplayDescription").TextChanged += (_, _) => DisplayChanged();
        this.GetControl<TextBox>("DisplayText").TextChanged += (_, _) => DisplayChanged();
        foreach (var n in new[] { "DisplayExtraBit", "DisplayUseText" })
            this.GetControl<CheckBox>(n).IsCheckedChanged += (_, _) => DisplayChanged();
        foreach (var n in new[] { "DisplayX", "DisplayY" })
            this.GetControl<NumericUpDown>(n).ValueChanged += (_, _) => DisplayChanged();
    }

    /// <summary>A display's line: its description, then what picks it.</summary>
    private string DisplayRow(PixiConfig.Display d)
        => (d.Description.Length > 0 ? d.Description : "(no description)")
         + (config.DisplayByExtraByte ? $"  · byte {d.X + 1} = {d.Y:X2}" : $"  · at {d.X:X},{d.Y:X}")
         + (d.ExtraBit ? "  · extra bit" : "")
         + (d.UseText ? "  · text" : "");

    private void ShowDisplayList(int select)
    {
        loading = true;
        DisplayList.ItemsSource = config.Displays.Select(DisplayRow).ToList();
        DisplayList.SelectedIndex = Math.Min(Math.Max(select, 0), config.Displays.Count - 1);
        loading = false;
        ShowDisplayFields();
        RefreshTileList(0);
    }

    /// <summary>Fill the display's fields. By position, X and Y are the placement's nibbles (0-F);
    /// by extension byte, X is which byte (0 = the first) and Y the value it holds.</summary>
    private void ShowDisplayFields()
    {
        var d = Display;
        bool byByte = config.DisplayByExtraByte;
        loading = true;
        this.GetControl<TextBlock>("DisplayXLabel").Text = byByte ? "byte" : "x";
        this.GetControl<TextBlock>("DisplayYLabel").Text = byByte ? "value" : "y";
        this.GetControl<NumericUpDown>("DisplayX").Maximum = byByte ? 11 : 15;
        this.GetControl<NumericUpDown>("DisplayY").Maximum = byByte ? 255 : 15;
        foreach (var n in new[] { "DisplayDescription", "DisplayText" }) this.GetControl<TextBox>(n).IsEnabled = d is not null;
        foreach (var n in new[] { "DisplayExtraBit", "DisplayUseText" }) this.GetControl<CheckBox>(n).IsEnabled = d is not null;
        foreach (var n in new[] { "DisplayX", "DisplayY" }) this.GetControl<NumericUpDown>(n).IsEnabled = d is not null;
        this.GetControl<TextBox>("DisplayDescription").Text = d?.Description ?? "";
        this.GetControl<CheckBox>("DisplayExtraBit").IsChecked = d?.ExtraBit == true;
        this.GetControl<NumericUpDown>("DisplayX").Value = d?.X ?? 0;
        this.GetControl<NumericUpDown>("DisplayY").Value = d?.Y ?? 0;
        this.GetControl<CheckBox>("DisplayUseText").IsChecked = d?.UseText == true;
        this.GetControl<TextBox>("DisplayText").Text = d?.Text ?? "";
        loading = false;
    }

    private void DisplayChanged()
    {
        if (loading || Display is not { } d) return;
        d.Description = this.GetControl<TextBox>("DisplayDescription").Text ?? "";
        d.Text = this.GetControl<TextBox>("DisplayText").Text ?? "";
        d.ExtraBit = Check("DisplayExtraBit");
        d.UseText = Check("DisplayUseText");
        d.X = Num("DisplayX");
        d.Y = Num("DisplayY");
        Touched();
        int i = DisplayList.SelectedIndex;
        loading = true;
        DisplayList.ItemsSource = config.Displays.Select(DisplayRow).ToList();
        DisplayList.SelectedIndex = i;
        loading = false;
    }

    private void OnAddDisplay(object? sender, RoutedEventArgs e)
    {
        config.Displays.Add(Display is { } from
            ? new PixiConfig.Display
            {
                Description = from.Description, ExtraBit = from.ExtraBit, X = from.X, Y = from.Y,
                UseText = from.UseText, Text = from.Text, Tiles = [.. from.Tiles], Gfx = [.. from.Gfx],
            }
            : new PixiConfig.Display { Description = name });
        Touched();
        ShowDisplayList(config.Displays.Count - 1);
    }

    private void OnRemoveDisplay(object? sender, RoutedEventArgs e)
    {
        int i = DisplayList.SelectedIndex;
        if (i < 0 || i >= config.Displays.Count) return;
        config.Displays.RemoveAt(i);
        Touched();
        ShowDisplayList(i);
    }

    // ---- the display's tiles ----

    private List<PixiConfig.Tile> Tiles => Display?.Tiles ?? [];
    private ListBox TileList => this.GetControl<ListBox>("DisplayTiles");
    /// <summary>The primary tile — the one the fields and a sheet click act on — or -1.</summary>
    private int Selected => TileList.SelectedIndex is var i && i < Tiles.Count ? i : -1;
    /// <summary>Every picked tile; a drag or nudge moves them all.</summary>
    private List<int> Picked => TileList.Selection.SelectedIndexes.Where(i => i >= 0 && i < Tiles.Count).ToList();

    private void WireTileFields()
    {
        TileList.SelectionChanged += (_, _) => { if (!loading) { ShowTileFields(); Preview.Redraw(); MarkMap16Sheet(); } };
        foreach (var n in new[] { "TileX", "TileY" })
            this.GetControl<NumericUpDown>(n).ValueChanged += (_, _) => TileFieldsChanged();
        this.GetControl<TextBox>("TileNumber").TextChanged += (_, _) => TileFieldsChanged();
        this.GetControl<CheckBox>("TileTranslucent").IsCheckedChanged += (_, _) => TileFieldsChanged();
    }

    private void ShowTileFields()
    {
        if (Selected < 0) return;
        var t = Tiles[Selected];
        loading = true;
        this.GetControl<NumericUpDown>("TileX").Value = t.X;
        this.GetControl<NumericUpDown>("TileY").Value = t.Y;
        var number = this.GetControl<TextBox>("TileNumber");
        if (Hex(number.Text) != t.Number) number.Text = $"{t.Number:X3}";   // "2" being typed stays "2", not "002"
        this.GetControl<CheckBox>("TileTranslucent").IsChecked = t.Translucent;
        loading = false;
    }

    private void TileFieldsChanged()
    {
        if (loading || Selected < 0) return;
        int number = Hex(this.GetControl<TextBox>("TileNumber").Text) is { } n and < 0x400 ? n : Tiles[Selected].Number;
        EditTile(Selected, new PixiConfig.Tile(Num("TileX"), Num("TileY"), number, Check("TileTranslucent")));
    }

    private void EditTile(int i, PixiConfig.Tile t)
    {
        Tiles[i] = t;
        Touched();
        RefreshTileList(i);
    }

    /// <summary>Rebuild the tile list and pick <paramref name="select"/> again, the first as primary.</summary>
    private void RefreshTileList(params int[] select)
    {
        loading = true;
        TileList.ItemsSource = Tiles.Select(TileRow).ToList();
        TileList.Selection.Clear();
        foreach (int i in select.Select(i => Math.Min(i, Tiles.Count - 1)).Where(i => i >= 0).Distinct())
            TileList.Selection.Select(i);
        loading = false;
        ShowTileFields();
        Preview.Redraw();
        MarkMap16Sheet();
    }

    /// <summary>A tile list row: the tile as drawn, then its fields.</summary>
    private Control TileRow(PixiConfig.Tile t)
    {
        var icon = new PixelImage
        {
            Zoom = 1.5,
            Source = LevelBitmap.FromPixels(session.DrawSpriteDisplay([t with { X = 0, Y = 0 }], config.Map16, 16, 16, 0, 0), 16, 16),
        };
        var text = new TextBlock
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Text = $"{t.Number:X3}  x{t.X,4} y{t.Y,4}{(t.Number >= OwnFrom ? "  own" : "")}{(t.Translucent ? "  translucent" : "")}",
        };
        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { icon, text } };
    }

    private void OnAddTile(object? sender, RoutedEventArgs e) => AddTile(Selected >= 0 ? Tiles[Selected] : new PixiConfig.Tile(0, 0, 0));

    /// <summary>Add a tile to the display, making the first display if there is none.</summary>
    private void AddTile(PixiConfig.Tile t)
    {
        if (Display is null)
        {
            config.Displays.Add(new PixiConfig.Display { Description = name });
            ShowDisplayList(config.Displays.Count - 1);
        }
        Tiles.Add(t);
        Touched();
        RefreshTileList(Tiles.Count - 1);
    }

    private void OnDeleteTile(object? sender, RoutedEventArgs e) => DeletePicked();

    private void DeletePicked()
    {
        var picked = Picked;
        if (picked.Count == 0) return;
        foreach (int i in picked.OrderByDescending(i => i)) Tiles.RemoveAt(i);
        Touched();
        RefreshTileList(picked.Min());
    }

    private void OnTileUp(object? sender, RoutedEventArgs e) => MoveTile(-1);
    private void OnTileDown(object? sender, RoutedEventArgs e) => MoveTile(1);

    private void MoveTile(int by)
    {
        int i = Selected, j = i + by;
        if (i < 0 || j < 0 || j >= Tiles.Count) return;
        (Tiles[i], Tiles[j]) = (Tiles[j], Tiles[i]);
        Touched();
        RefreshTileList(j);
    }

    // ---- preview: pick, lasso, move ----

    private TileArrangeView Preview => this.GetControl<TileArrangeView>("DisplayPreview");

    private void WirePreview()
    {
        var view = Preview;
        view.Count = () => Tiles.Count;
        view.BoxOf = i => (Tiles[i].X, Tiles[i].Y, 16);
        view.Picked = () => Picked;
        view.Primary = () => Selected;
        view.Pick = picked => RefreshTileList(picked);
        view.MoveTo = moves =>
        {
            foreach (var (i, x, y) in moves) Tiles[i] = Tiles[i] with { X = x, Y = y };
            Touched();
            RefreshTileList([.. moves.Select(m => m.I)]);
        };
        view.Delete = DeletePicked;
        view.Render = (w, h, ox, oy) => session.DrawSpriteDisplay(Tiles, config.Map16, w, h, ox, oy);
    }

    // ---- the sprite Map16 sheet: pick a tile ----


    private TileSheetView Map16View => this.GetControl<TileSheetView>("Map16Sheet");

    private void WireMap16Sheet()
    {
        Map16View.Picked += t =>
        {
            if (Selected >= 0) EditTile(Selected, Tiles[Selected] with { Number = t });
            else AddTile(new PixiConfig.Tile(0, 0, t));
        };
        Map16View.Hovered += t => this.GetControl<TextBlock>("Map16Readout").Text = t is not { } n ? ""
            : n < OwnFrom ? $"tile {n:X3}"
            : n < OwnFrom + OwnCount ? $"tile {n:X3}  (the sprite's own)"
            : $"tile {n:X3}  (Lunar Magic's — the sprite has no tile here)";
    }

    /// <summary>Draw the sheet again: on opening, and when the sprite's own tiles change.</summary>
    private void RedrawMap16Sheet()
    {
        var (px, w, h) = session.SpriteMap16Sheet(config.Map16);
        Map16View.Show(px, w, h);
        MarkMap16Sheet();
    }

    /// <summary>Outline the display's tiles on the sheet, the primary one brightest.</summary>
    private void MarkMap16Sheet()
    {
        var sheet = Map16View;
        sheet.Marks.Children.Clear();
        foreach (var t in Tiles.Select(t => t.Number).Distinct()) sheet.Mark(t, 1, UiColors.Sprite);
        if (Selected >= 0) sheet.Mark(Tiles[Selected].Number, 1, UiColors.Accent);
    }
}
