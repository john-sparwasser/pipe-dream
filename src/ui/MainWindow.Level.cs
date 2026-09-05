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
/// Level mode: the level canvas and its layer bar, the tile brush, the exits and entrances
/// overlays, and the dialogs that edit the level record (properties, GFX header, exits,
/// sprite data). The canvas itself is <see cref="LevelView"/>; the mode switch, zoom and
/// undo dispatch are in MainWindow.axaml.cs.
/// </summary>
public partial class MainWindow
{
    private ToggleButton layerOne = null!, layerTwo = null!, exitsMode = null!, entrancesMode = null!;
    private Button dropLayer2 = null!;
    private TextBlock layer2Note = null!;

    /// <summary>Multi-tile stamp brush from a Ctrl+drag grab; null = the drawer's single tile.</summary>
    private (ushort[] Tiles, int W, int H)? brush;

    /// <summary>Level mode: the layer toggles and everything the level canvas raises.</summary>
    private void WireLevel()
    {
        layerOne = this.GetControl<ToggleButton>("LayerOne");
        layerTwo = this.GetControl<ToggleButton>("LayerTwo");
        exitsMode = this.GetControl<ToggleButton>("ExitsMode");
        entrancesMode = this.GetControl<ToggleButton>("EntrancesMode");
        dropLayer2 = this.GetControl<Button>("DropLayer2");
        layer2Note = this.GetControl<TextBlock>("Layer2Note");

        canvas.Source = bitmap;
        canvas.PointerMoved += (_, _) => UpdateReadout();
        canvas.PointerExited += (_, _) => UpdateReadout();

        // RIGHT drag stamps the drawer's tile, one undo entry per stroke (ImGui parity: the
        // left button belongs to selection).
        canvas.CellPainted += (_, c) =>
        {
            if (edit is null) return;
            if (edit.TilePlacementBlocked is { } why) return;
            // A grabbed multi-tile brush wins over the drawer's single selected tile.
            bool changed = brush is { } b
                ? edit.PaintBrush(c.X, c.Y, b.Tiles, b.W, b.H)
                : edit.Paint(c.X, c.Y, palette.Selected);
            if (changed) PushDirty();
        };
        canvas.StrokeEnded += (_, _) =>
        {
            edit?.EndStroke();   // cells become DM16 objects here; the grid is re-rendered
            PushDirty();
        };
        canvas.DuplicateRequested += (_, c) =>
        {
            if (edit?.DuplicateSelected(c.X, c.Y) == true) PushDirty();
        };
        canvas.PlaceRequested += (_, c) =>
        {
            if (edit is null || canvas.CatalogObject < 0) return;
            edit.PlaceObject(canvas.CatalogObject, c.X, c.Y);
            PushDirty();
        };
        canvas.DeleteRequested += (_, _) =>
        {
            if (edit?.DeleteSelected() == true) PushDirty();
        };
        canvas.GrabRequested += (_, g) =>
        {
            if (edit is null) return;
            var (tiles, w, h) = edit.GrabTiles(g.X, g.Y, g.W, g.H);
            palette.ClearSelection();      // the level's block is the brush now, not the drawer's
            SetBrush(tiles, w, h);
        };
        // Moving and resizing raise this too, and they change PIXELS — without the push the
        // objects stayed where they were drawn and the edit looked like it had not happened.
        // RefreshPixels is a no-op when nothing is dirty, so a plain selection costs nothing.
        canvas.SelectionChanged += (_, _) => PushDirty();;
        canvas.ExitScreenClicked += async (_, screen) => await EditScreenExit(screen);
        canvas.ExitBadgeClicked += (_, screen) => FollowExit(screen);
        canvas.EntranceMoved += (_, m) =>
        {
            // The drop position is where the cursor was; the session snaps it to what the ROM
            // can store, so the markers are re-read rather than trusting the drag.
            session.MoveEntrance(m.Kind, m.Index, m.X, m.Y);
            RefreshEntranceMarkers();
            UpdateTitle();
        };
        canvas.EntranceEditRequested += async (_, en) =>
        {
            if (en.Kind == EntranceKind.Secondary) await ShowEntrance(en.Index);
            else if (session.MainEntrance is { } me && session.Rom is { } rom)
            {
                var dlg = new EntranceWindow(me, en.Kind, rom.HasFreeMidwayPosition);
                await dlg.ShowDialog(this);
                if (dlg.Applied is { } applied) session.ApplyEntry(applied);
            }
            RefreshEntranceMarkers();
            UpdateTitle();
        };
        canvas.SampleRequested += (_, p) =>
        {
            if (session.SampleCgramIndex(p.X, p.Y) is not { } idx)
            {
                return;
            }
            // Land the user where they can act on it: Palette mode, that swatch selected.
            if (modePalette.IsChecked != true) OnMode(modePalette, new RoutedEventArgs());
            paletteGrid.Select(idx);
            paletteBg.Select(idx == 0 ? 0 : -1);
            ShowPaletteColor(idx);
        };
        // A sprite edit changes what the overlay draws, so the level has to recompose. The
        // adopt comes from SceneRebuilt, below.
        canvas.SpritesChanged += (_, _) => { session.RefreshSprites(); PushSpritePixels(); };
        // A live drag step shifts cached overlay pixels in place instead of rebuilding the
        // scene, so only the bitmap upload is left to do here.
        canvas.SpritesMoved += (_, d) => { session.MoveSprites(d.Dx, d.Dy); PushSpritePixels(); };

        // Wheel scrolls the level sideways (Shift: vertically) — the canvas decides, the
        // scroll viewer applies, since it owns the offsets.
        canvas.ScrollRequested += (_, d) =>
        {
            var sv = this.GetControl<ScrollViewer>("CanvasScroll");
            sv.Offset = new Vector(Math.Max(0, sv.Offset.X + d.Dx), Math.Max(0, sv.Offset.Y + d.Dy));
        };
    }

    private void SetBrush(ushort[]? tiles, int w, int h)
    {
        // Arming the brush disarms the object catalog, as the ImGui editor does — right-click
        // means one thing at a time. Both halves are set: clearing the list is what the user
        // sees, and clearing the canvas is what actually disarms — the list's own handler does
        // not fire when nothing was selected in it.
        objectList.SelectedIndex = -1;
        canvas.CatalogObject = -1;
        brush = tiles is null ? null : (tiles, w, h);
        if (tiles is null) palette.ClearSelection();   // no brush, no block to show as one
        canvas.InvalidateVisual();
    }

    private string LevelReadout()
    {
        if (canvas.HoverCell is not { } c) return "";
        if (session.TileAt(c.X, c.Y) is not { } tile) return $"({c.X,3},{c.Y,2})  empty";
        string acts = map16?.ActsAs(tile) is { } a ? $"  acts 0x{a:X3}" : "";
        return $"({c.X,3},{c.Y,2})  tile 0x{tile:X3}{acts}";
    }

    /// <summary>Push what an edit changed into the bitmap. The composition already happened in
    /// the session's phase images, so this is only the copy — and because the bitmap takes whole
    /// images, a repaint is one 13MB push rather than per-cell blits. If that ever shows up in a
    /// profile, LevelBitmap grows a dirty-rect upload.</summary>
    private void PushDirty()
    {
        if (!session.RefreshPixels()) return;
        bitmap.SetImages(session.Phases, session.PxW, session.PxH, 0);
        canvas.InvalidateVisual();
    }

    /// <summary>Upload after a sprite edit: the session repainted the phases in place, so only
    /// the bitmap needs pushing — no sheet or drawer is affected by a sprite list change.</summary>
    private void PushSpritePixels()
    {
        bitmap.SetImages(session.Phases, session.PxW, session.PxH, 0);
        canvas.InvalidateVisual();
    }

    // ---- layer 2 ----

    private void OnEditLayer(object? sender, RoutedEventArgs e)
    {
        session.SetEditLayer(ReferenceEquals(sender, layerTwo) ? 1 : 0);
        AdoptSession();
    }

    private void OnDropLayer2(object? sender, RoutedEventArgs e)
    {
        AdoptSession();
    }

    /// <summary>Show which layer is live and which of the layer-2 conversions is available. The
    /// loudest case gets its own note: objects that exist on a level whose MODE never loads them
    /// would silently do nothing in-game.</summary>
    private void RefreshLayerBar()
    {
        layerOne.IsChecked = session.EditLayer == 0;
        // Deliberately NOT disabled when layer 2 is a background image. Most levels are one, so
        // the button spent most of its life greyed out and clicking it did nothing at all —
        // whereas SetEditLayer already has the answer and can only say it if the click gets
        // through.
        layerTwo.IsChecked = session.EditLayer == 1;
        dropLayer2.IsVisible = session.Layer2FromProject;
        layer2Note.Text = session.Layer2Editable && !session.LevelModeReadsLayer2 && session.Header is { } h
            ? $"(mode {h.LevelMode:X2} ignores L2)" : "";
    }

    private void OnReloadLevel(object? sender, RoutedEventArgs e)
    {
        session.ReloadLevel();
        AdoptSession();
    }

    // ---- View menu toggles that redraw the level ----

    private void OnToggleSprites(object? sender, RoutedEventArgs e)
    {
        session.ShowSprites = !session.ShowSprites;
        AdoptSession();
    }

    /// <summary>Draw the level's layer 3 on the level canvas. A recompose, not an overlay: it
    /// belongs BEHIND layer 2 unless the header gives it priority, and nothing painted over a
    /// finished canvas can go behind anything.</summary>
    private void OnToggleLayer3Preview(object? sender, RoutedEventArgs e)
    {
        if (!session.SetPreviewLayer3(!session.PreviewLayer3)) return;
        layer3PreviewItem.Icon = session.PreviewLayer3 ? new TextBlock { Text = "✓" } : null;
        AdoptSession();
    }

    private void OnToggleGrid(object? sender, RoutedEventArgs e)
    {
        canvas.ShowGrid = !canvas.ShowGrid;
        canvas.InvalidateVisual();
    }

    // ---- exits and entrances ----

    /// <summary>
    /// Arm or disarm the canvas's exits mode. It TAKES OVER from the layer being edited rather
    /// than sitting beside it: while it is on, the layer toggles are dead, the canvas paints no
    /// selection, and a click means "this screen", not "this object".
    /// </summary>
    private void OnExitsMode(object? sender, RoutedEventArgs e) => ApplyOverlayMode(exitsMode);

    private void OnEntrancesMode(object? sender, RoutedEventArgs e) => ApplyOverlayMode(entrancesMode);

    /// <summary>
    /// Arm or disarm one of the level's overlay modes. They are the two halves of a connection —
    /// where a level leads and where it is entered — and each TAKES OVER from the layer being
    /// edited, so they are exclusive with each other as well: arming one disarms the other,
    /// rather than leaving two modes both claiming the canvas.
    /// </summary>
    private void ApplyOverlayMode(ToggleButton clicked)
    {
        if (clicked.IsChecked == true)
            foreach (var other in new[] { exitsMode, entrancesMode })
                if (!ReferenceEquals(other, clicked)) other.IsChecked = false;

        bool exits = exitsMode.IsChecked == true, entrances = entrancesMode.IsChecked == true;
        canvas.Mode = exits ? LevelView.EditMode.Exits
                    : entrances ? LevelView.EditMode.Entrances
                    : paletteTabs.SelectedIndex == 1 ? LevelView.EditMode.Sprites
                                                     : LevelView.EditMode.Objects;
        layerOne.IsEnabled = layerTwo.IsEnabled = !exits && !entrances;
        edit?.Selection.Clear();
        canvas.Sprites?.Selection.Clear();
        RefreshExitBadges();
        RefreshEntranceMarkers();
    }

    /// <summary>Re-read where this level's entrances put Mario. Cheap — a main record, a midway
    /// screen and a scan of the entrance table — so it runs after every move rather than being
    /// kept in step by hand.</summary>
    private void RefreshEntranceMarkers()
    {
        canvas.Entrances = canvas.Mode == LevelView.EditMode.Entrances ? session.Entrances() : [];
        // ponytail: built once per window from the first level's palette; Mario's own 10 colours
        // come from the ROM, only row 8's shared colours 1-5 could differ between levels.
        if (canvas.MarioIcon is null && session.Rom is { } rom && session.Scene?.Palettes[0] is { } pal
            && PlayerGfx.BigMarioStanding(rom, pal) is { } px)
            canvas.MarioIcon = LevelBitmap.FromPixels(px, 16, 32);
        canvas.InvalidateVisual();
    }

    /// <summary>
    /// Walk the connection: the badge names where a screen leads, so clicking it goes there.
    /// A secondary exit's destination is an INDEX into the entrance table rather than a level,
    /// so that one is resolved through the record — which is the whole reason the view hands
    /// back a screen number and lets this side work out what it means.
    /// </summary>
    private void FollowExit(int screen)
    {
        if (edit?.ReadExits().FirstOrDefault(x => x.Screen == screen) is not { } exit) return;
        int level = exit.Secondary && session.ReadEntrance(exit.Destination) is { } entrance
            ? entrance.DestinationLevel
            : exit.Destination;
        if (level < 0 || level >= EditorSession.LevelCount) return;
        levelBox.SelectedIndex = level;         // the picker IS the load path
    }

    /// <summary>Re-read the exit table the canvas draws its badges from. Cheap enough to run on
    /// every write — it is a handful of objects out of the layer-1 stream.</summary>
    private void RefreshExitBadges()
    {
        canvas.Exits = edit is null || canvas.Mode != LevelView.EditMode.Exits
            ? []
            : [.. edit.ReadExits().Select(x => (x.Screen, x.Destination, x.LmForm))];
        canvas.InvalidateVisual();
    }

    /// <summary>
    /// One screen's destination, asked for over the level itself. Everything else about an exit
    /// — the water and secondary flags, the LM word form — is left exactly as it was found;
    /// clearing the box removes the exit, which is the only other thing this view can mean.
    /// </summary>
    private async Task EditScreenExit(int screen)
    {
        if (edit is null) return;
        var exits = edit.ReadExits();
        var here = exits.FirstOrDefault(x => x.Screen == screen);
        // How wide the destination can be depends on the BASE. A v7-prepped (or LM-saved) ROM
        // takes the level's ninth bit from the exit's own flags, so the whole level range is
        // reachable; on anything older the ninth bit comes from the submap the player entered
        // from, and only the low byte means anything.
        bool high = session.ExitsReachHighLevels;
        int mask = here?.LmForm == true ? 0xFFFF : high ? 0x1FF : 0xFF;
        string range = here?.LmForm == true ? "0000-FFFF" : high ? "000-1FF" : "00-FF, low byte only";

        var dlg = new TextPromptWindow(
            $"Screen {screen:X2} exits to (hex level {range} — blank for none)",
            here is null ? "" : here.Destination.ToString(here.LmForm ? "X4" : high ? "X3" : "X2"));
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } text) return;

        text = text.Trim();
        if (text.Length == 0)
        {
            if (here is null) return;
            exits.Remove(here);
        }
        else if (int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out int dest))
        {
            // MASKED, not clamped: the field is as wide as it is. Clamping turned $105 into
            // $FF — a level nobody asked for, written silently.
            dest &= mask;
            if (here is not null) here.Destination = dest;
            else exits.Add(new LevelExit { Screen = screen, Destination = dest });
        }
        else return;                       // not a number: the safe answer is to change nothing

        if (edit.WriteExits(exits)) PushDirty();
        RefreshExitBadges();
        UpdateTitle();
    }

    /// <summary>Screen exits, staged in a table and applied as one object edit. "Entrance…" hands
    /// off to the entrance record the exit points at, applying the table on the way so nothing
    /// typed is lost.</summary>
    private async void OnLevelExits(object? sender, RoutedEventArgs e)
    {
        if (edit is null) return;
        var dlg = new LevelExitsWindow(edit.ReadExits());
        await dlg.ShowDialog(this);

        if (dlg.Applied is { } exits && edit.WriteExits(exits))
        {
            PushDirty();
        }
        if (dlg.OpenEntrance is { } at) await ShowEntrance(at);
        UpdateTitle();
    }

    private async Task ShowEntrance(int index)
    {
        if (!session.HasRom) return;
        var dlg = new SecondaryEntranceWindow(index, session.ReadEntrance);
        await dlg.ShowDialog(this);
        if (dlg.Applied is not { } a) return;
        session.WriteEntrance(a.Index, a.Entrance);
        UpdateTitle();
    }

    // ---- level record dialogs ----

    /// <summary>Level header + main entrance, staged in a dialog and applied in one go: every
    /// header field forces a full reparse, so live-applying a slider would be unusable.</summary>
    private async void OnLevelProperties(object? sender, RoutedEventArgs e) => await EditLevelProperties();

    private async Task EditLevelProperties()
    {
        if (session.Header is not { } header || session.MainEntrance is not { } entrance) return;
        var dlg = new LevelPropertiesWindow(header, entrance, session.HasHeaderOverride);
        await dlg.ShowDialog(this);

        if (dlg.RevertRequested) { session.RevertHeader(); AdoptSession(); return; }
        // An entrance change repaints too. Most of its fields are spawn bookkeeping the canvas
        // never draws, but the Layer 3 option is in there — without this, giving a level a
        // layer 3 wrote the byte and left the Background tab still saying it has none.
        if (dlg.AppliedEntry is { } en && en != entrance) { session.ApplyEntry(en); AdoptSession(); }
        if (dlg.AppliedHeader is { } h && h != header)
        {
            session.ApplyHeader(h);
            AdoptSession();
        }
        UpdateTitle();
    }

    /// <summary>The graphics header off the GFX drawer: the level's tileset ("layer 1") and
    /// sprite set. Same staged-apply path as the properties dialog — a header change reparses.</summary>
    private async void OnGfxHeader(object? sender, RoutedEventArgs e)
    {
        if (session.Header is not { } h) return;
        var (layer1, sprites) = session.GfxHeaderChoices();
        if (layer1.Count == 0) return;
        var dlg = new GfxHeaderWindow(layer1, h.Tileset, sprites, h.SpriteSet);
        await dlg.ShowDialog(this);
        if (dlg.Result is { } r && (r.Tileset != h.Tileset || r.SpriteSet != h.SpriteSet))
        {
            session.ApplyHeader(h with { Tileset = r.Tileset, SpriteSet = r.SpriteSet });
            AdoptSession();
        }
    }

    private async void OnSpriteData(object? sender, RoutedEventArgs e)
    {
        if (session.Sprites is not { } sp || sp.Selection.Count != 1) return;   // menu does nothing without exactly one selected sprite
        int i = sp.Selection.First();
        var dlg = new SpriteDataWindow(sp.Sprites.Sprites[i]);
        await dlg.ShowDialog(this);
        if (dlg.Applied is { } d && sp.SetData(i, d.Number, d.Extra, d.ExtraBytes))
        {
            session.RefreshSprites();
            PushSpritePixels();
            PushDirty();
        }
        UpdateTitle();
    }
}
