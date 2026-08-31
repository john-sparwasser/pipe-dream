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
/// Pick tiles by CLICKING them on a sheet instead of typing numbers. Two uses, one window:
/// the SOURCE of an ExAnimation frame (the sheets the engine can read — AN1, the level's AN2
/// file, Mario's sheet, the list's alternate file 60-63; the frame's run of N consecutive tiles
/// is outlined) and its DESTINATION (the level's VRAM as the 8x8 sheet the Map16 editor shows;
/// the slot's footprint — a line, or the stacked/16x16/32x16 block — is outlined). A click
/// returns the first tile in <see cref="Picked"/>; <see cref="PickedAlt"/> says whether a source
/// came from the alternate file.
/// </summary>
public partial class TilePickerWindow : Window
{
    public int? Picked { get; private set; }
    public bool PickedAlt { get; private set; }

    /// <summary>One selectable sheet: what to show, what its first tile is numbered, and how many
    /// of its tiles may be chosen (a destination sheet has 0x400 tiles but only 0x300 are valid).</summary>
    private readonly record struct SheetSource(string Name, Func<(uint[] Px, int W, int H)> Sheet, int Base, int Limit, bool Alt, int File = -1);

    /// <summary>Raised with the file id when the user asks to edit the alternate file; the caller
    /// takes them to the Graphics editor. The picker closes itself.</summary>
    public Action<int>? EditRequested { get; set; }
    private int editFileId = -1;

    private readonly List<SheetSource> sources = [];
    private readonly int[] offsets;
    private int sheetTiles, sheetCols = 16, limit;
    private const int Scale = 3;
    private ComboBox source = null!;
    private PixelImage sheet = null!;
    private Canvas overlay = null!;

    public TilePickerWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Frame SOURCE: the footprint is a consecutive run of the slot's N tiles on the sheet —
    /// the line the engine DMAs, so a 16x16 frame is drawn and picked as four tiles in a row.
    /// The dropdown opens on where custom animations actually live — the global list's own file
    /// 60-63, a level list's AN2 bypass file — not on AN1, which is the remapped vanilla sheet.</summary>
    public TilePickerWindow(EditorSession session, int[] footprint, int altIndex, int palRow, bool preferAlt, bool global)
        : this(footprint, "Pick source tiles")
    {
        sources.Add(new("AN1 — animated tiles (GFX33)", () => session.GfxFileSheet(0x33, palRow), 0x600, int.MaxValue, false, 0x33));
        int an2 = session.GfxBins.FirstOrDefault(b => b.Name == "AN2").File;
        if (an2 is not (0 or 0x7F)) sources.Add(new($"AN2 — GFX{an2:X3}", () => session.GfxFileSheet(an2, palRow), 0x780, int.MaxValue, false, an2));
        // Mario's sheet is a legal source too (0x900-0xBE7) but not one anyone animates from
        // on purpose; it stays out of the list until someone asks.
        sources.Add(new($"file {0x60 + altIndex:X2} — the list's alternate file", () => session.GfxFileSheet(0x60 + altIndex, palRow),
                        0xC00 + altIndex * 0x400, int.MaxValue, true, 0x60 + altIndex));
        editFileId = 0x60 + altIndex;
        int def = preferAlt || global ? sources.Count - 1        // the slot already reads it, or global: its 60-63 file
                : an2 is not (0 or 0x7F) ? 1                     // level list: the level's AN2 bypass file
                : sources.Count - 1;                             // no AN2 set: the 60-63 file is still the custom home
        Start(def);
    }

    /// <summary>DESTINATION: the level's VRAM sheet, footprint = the slot's tiles relative to its
    /// first (<see cref="ExAnimation.Slot.DestTileAt"/> minus the first).</summary>
    public TilePickerWindow(EditorSession session, ExAnimation.Slot slot, int palRow)
        : this(Enumerable.Range(0, Math.Max(1, slot.TileCount)).Select(k => slot.DestTileAt(k) - slot.DestTile).ToArray(),
               "Pick the destination tile")
    {
        sources.Add(new("VRAM — layer 1/2 graphics (tiles 000-2FF)", () =>
        {
            var (px, w, h) = session.ChrPhases(palRow);
            return px[0] is { } p ? (p, w, h) : ([], 0, 0);
        }, 0x000, 0x300, false));
        Start(0);
    }

    private TilePickerWindow(int[] offsets, string title) : this()
    {
        this.offsets = offsets;
        Title = title;
        source = this.GetControl<ComboBox>("Source");
        sheet = this.GetControl<PixelImage>("Sheet");
        overlay = this.GetControl<Canvas>("Overlay");
        // The pointer is taken on the HOST panel, hit-testable over its whole transparent area: a
        // custom-drawn image only hit-tests where it has painted, which is unreliable across the
        // sheet (and nowhere at all in a headless run).
        var host = this.GetControl<Panel>("SheetHost");
        host.PointerMoved += (_, e) => Hover(e.GetPosition(host));
        host.PointerExited += (_, _) => overlay.Children.Clear();
        host.PointerPressed += (_, e) =>
        {
            if (TileAt(e.GetPosition(host)) is not { } t) return;
            var s = sources[source.SelectedIndex];
            Picked = s.Base + t;
            PickedAlt = s.Alt;
            Close();
        };
    }

    private void Start(int index)
    {
        source.ItemsSource = sources.Select(s => s.Name).ToList();
        source.SelectedIndex = index;
        source.SelectionChanged += (_, _) => ShowSheet();
        ShowSheet();
    }

    private void ShowSheet()
    {
        overlay.Children.Clear();
        var s = sources[Math.Max(0, source.SelectedIndex)];
        var (px, w, h) = s.Sheet();
        var hint = this.GetControl<TextBlock>("Hint");
        this.GetControl<Button>("EditFile").IsVisible = s.Alt && editFileId >= 0;   // the file is ours to paint
        if (px.Length == 0)
        {
            sheet.Source = null; sheet.Width = sheet.Height = 0; sheetTiles = 0;
            hint.Text = s.Alt ? "empty — Edit… to paint it" : "not loaded";
            return;
        }
        sheetTiles = w / 8 * (h / 8); sheetCols = w / 8;
        limit = Math.Min(s.Limit, sheetTiles);
        sheet.Source = LevelBitmap.FromPixels(px, w, h);
        sheet.Width = w * Scale; sheet.Height = h * Scale;
        this.GetControl<Panel>("SheetHost").Width = w * Scale;
        this.GetControl<Panel>("SheetHost").Height = h * Scale;
        hint.Text = offsets.Length == 1 ? $"click a tile — {s.Base:X3}-{s.Base + limit - 1:X3}"
                  : $"click where the first of {offsets.Length} tiles goes — {s.Base:X3}-{s.Base + limit - 1:X3}";
    }

    /// <summary>The tile under the pointer, when the whole footprint from it fits the sheet.</summary>
    private int? TileAt(Point p)
    {
        int cx = (int)(p.X / (8 * Scale)), cy = (int)(p.Y / (8 * Scale));
        if (cx < 0 || cy < 0 || cx >= sheetCols) return null;
        int t = cy * sheetCols + cx;
        foreach (int off in offsets)
            if (t + off >= limit || t + off < 0) return null;
        return t;
    }

    private void Hover(Point p)
    {
        overlay.Children.Clear();
        var s = sources[Math.Max(0, source.SelectedIndex)];
        int? at = TileAt(p);
        this.GetControl<TextBlock>("Readout").Text = at is { } t0 ? $"tile {s.Base + t0:X3}" : "";
        if (at is not { } t) return;
        foreach (int off in offsets)
        {
            int idx = t + off, x = idx % sheetCols * 8 * Scale, y = idx / sheetCols * 8 * Scale;
            var r = new Rectangle { Width = 8 * Scale, Height = 8 * Scale, Stroke = UiColors.Selection, StrokeThickness = 2, Fill = UiColors.SelectionFill };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            overlay.Children.Add(r);
        }
    }

    private void OnEditFile(object? sender, RoutedEventArgs e)
    {
        if (editFileId < 0) return;
        Close();
        EditRequested?.Invoke(editFileId);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
