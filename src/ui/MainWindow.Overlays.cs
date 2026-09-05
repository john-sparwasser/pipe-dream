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
/// The hitbox and spawn overlays: one setting each, drawn on the level canvas and the Map16
/// sheet alike (spawns on the Tiles drawer too). The overlays themselves are
/// <see cref="HitboxOverlay"/> and <see cref="SpawnOverlay"/>.
/// </summary>
public partial class MainWindow
{
    private ToggleButton spawnsToggle = null!, m16SpawnsToggle = null!;

    /// <summary>Hitbox and spawn overlays: one setting each, a button in every mode that draws them.</summary>
    private void WireOverlays()
    {
        // Hitboxes on both canvases, from the same rule: the tile's acts-as through the ROM's
        // collision tables, in this level's tileset. The level can also resolve a steep slope's
        // upper tile from the tile under it; the sheet has no neighbours and shows it as unknown.
        // One setting with two buttons: turning it on in either mode turns it on in both, so
        // switching modes never loses it.
        var m16Hitboxes = this.GetControl<ToggleButton>("M16Hitboxes");
        var hitboxes = this.GetControl<ToggleButton>("HitboxesToggle");
        void ShowHitboxes(bool on)
        {
            hitboxes.IsChecked = on;
            m16Hitboxes.IsChecked = on;
            canvas.Hitboxes = on ? LevelHitbox : null;
            map16Canvas.Hitboxes = on && session.Rom is { } r
                ? tile => Hitboxes.Of(r, r.ActsAs(tile), session.Tileset) : null;
            canvas.InvalidateVisual();
            map16Canvas.InvalidateVisual();
        }
        m16Hitboxes.IsCheckedChanged += (_, _) => ShowHitboxes(m16Hitboxes.IsChecked == true);
        hitboxes.IsCheckedChanged += (_, _) => ShowHitboxes(hitboxes.IsChecked == true);
        // Spawns likewise: one setting, two buttons, three canvases (the Tiles drawer follows).
        spawnsToggle = this.GetControl<ToggleButton>("SpawnsToggle");
        m16SpawnsToggle = this.GetControl<ToggleButton>("M16Spawns");
        spawnsToggle.IsCheckedChanged += (_, _) => ShowSpawns(spawnsToggle.IsChecked == true);
        m16SpawnsToggle.IsCheckedChanged += (_, _) => ShowSpawns(m16SpawnsToggle.IsChecked == true);
    }

    /// <summary>Turn the Spawns overlay on or off everywhere it draws. Built per level: the
    /// thumbnails use the level's sprite graphics, so a level change rebuilds it.</summary>
    private void ShowSpawns(bool on)
    {
        spawnsToggle.IsChecked = on;
        m16SpawnsToggle.IsChecked = on;
        SpawnOverlay? overlay = null;
        if (on && session.Rom is { } r && session.HasLevel)
        {
            var thumbs = session.SpriteCatalog().Items.ToDictionary(i => i.Number, i => i.Thumb);
            overlay = new SpawnOverlay(
                tile => Map16Tiles.SpawnOf(tile) ?? (r.ActsAs(tile) is var a && a != tile ? Map16Tiles.SpawnOf(a) : null),
                n => thumbs.GetValueOrDefault(n));
        }
        canvas.Spawns = overlay;
        canvas.TileAt = overlay is null ? null : (cx, cy) => session.Scene?.Grid.Get(cx, cy) ?? Map16Grid.Empty;
        map16Canvas.Spawns = overlay;
        palette.Spawns = overlay;
        canvas.InvalidateVisual();
        map16Canvas.InvalidateVisual();
        palette.InvalidateVisual();
    }

    /// <summary>The hitbox of the level cell at (cx, cy): the tile's acts-as through the ROM's
    /// tables, and for the tile over a steep slope, the slope under it.</summary>
    private Hitbox LevelHitbox(int cx, int cy)
    {
        if (session.Rom is not { } r || session.Scene is not { } sc) return Hitbox.Nothing;
        int tile = sc.Grid.Get(cx, cy);
        if (tile == Map16Grid.Empty) return Hitbox.Nothing;
        var hb = Hitboxes.Of(r, r.ActsAs(tile), session.Tileset);
        if (hb.Kind != HitKind.SlopeTop) return hb;
        int below = sc.Grid.Get(cx, cy + 1);
        return below == Map16Grid.Empty ? hb : Hitboxes.Above(r, r.ActsAs(below), session.Tileset);
    }
}
