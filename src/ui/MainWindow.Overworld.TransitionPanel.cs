using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

// The Transitions tab's right drawer: the star, pipe or exit tile picked on the map — where it
// leads, what leads to it, and Lunar Magic's link settings for it (one-way or two-way, and the way
// the player walks through an exit tile), with its index read off for a level's secondary exit. The map side of the tab, and the link pick
// the drawer arms, are MainWindow.Overworld.Transitions.cs; the tables are
// EditorSession.Overworld.Transitions.cs.

public partial class MainWindow
{
    private Border owTransitionPanel = null!;
    private TextBlock owTransitionTitle = null!, owTransitionDest = null!, owTransitionFrom = null!, owTransitionNote = null!, owTransitionIndex = null!;
    private ToggleButton owTransitionPick = null!;
    private Button owTransitionGo = null!, owTransitionClear = null!, owTransitionUnlink = null!;
    private CheckBox owTransitionTwoWay = null!;
    private ComboBox owTransitionDir = null!;
    private DockPanel owTransitionDirRow = null!;

    /// <summary>The star, pipe or exit tile picked on the map, in layer 1's grid (the submap map's
    /// rows below the main map's), or null. It wears the selection ring, where it leads the armed
    /// halo, and the drawer is about it.</summary>
    private (int X, int Y)? owTransition;

    /// <summary>Set while the drawer fills its own lists, so the fill is not taken for a choice.</summary>
    private bool owTransitionFilling;

    private void WireOwTransitionPanel()
    {
        owTransitionPanel = this.GetControl<Border>("OwTransitionPanel");
        owTransitionTitle = this.GetControl<TextBlock>("OwTransitionTitle");
        owTransitionDest = this.GetControl<TextBlock>("OwTransitionDest");
        owTransitionFrom = this.GetControl<TextBlock>("OwTransitionFrom");
        owTransitionNote = this.GetControl<TextBlock>("OwTransitionNote");
        owTransitionPick = this.GetControl<ToggleButton>("OwTransitionPick");
        owTransitionGo = this.GetControl<Button>("OwTransitionGo");
        owTransitionClear = this.GetControl<Button>("OwTransitionClear");
        owTransitionUnlink = this.GetControl<Button>("OwTransitionUnlink");
        owTransitionIndex = this.GetControl<TextBlock>("OwTransitionIndex");
        owTransitionTwoWay = this.GetControl<CheckBox>("OwTransitionTwoWay");
        owTransitionDir = this.GetControl<ComboBox>("OwTransitionDir");
        owTransitionDirRow = this.GetControl<DockPanel>("OwTransitionDirRow");
        owTransitionDir.ItemsSource = new[] { "As the path says" }.Concat(Overworld.DirectionNames).ToList();
    }

    /// <summary>A click on the Transitions tab's map with no link armed: a star, pipe or exit tile
    /// is picked and the drawer opens on it; anything else lets go.</summary>
    private void OwTransitionClicked((int Col, int Row) cell)
        => OwPickTransition(EditorSession.OwLayer1Cell(cell.Col, cell.Row, out int x, out int y) && session.OwLinkableAt(x, y) is not null
                            ? (x, y) : null);

    /// <summary>Pick a tile for the drawer, or none. A new tile starts with the way through it as
    /// its path says — a choice made for the last one is not this one's.</summary>
    private void OwPickTransition((int X, int Y)? tile)
    {
        if (tile != owTransition)
        {
            owTransitionFilling = true;
            owTransitionDir.SelectedIndex = 0;
            owTransitionFilling = false;
        }
        owTransition = tile;
        RefreshOwTransitionPanel();
        owView.InvalidateVisual();
    }

    /// <summary>
    /// The drawer, for the tile picked: what it is and where, where it leads — a tile on some map
    /// and whether that one leads back, its own tile for a location teleport, or nowhere yet — the
    /// tiles that lead here, and its settings. Hidden on the other tabs and with nothing picked.
    /// </summary>
    private void RefreshOwTransitionPanel()
    {
        if (owTransition is { } p && session.OwLinkableAt(p.X, p.Y) is null) owTransition = null;   // a reload took it
        bool show = OwModeNow == OwMode.Transitions && !OwColorsOnly && owTransition is not null;
        owTransitionPanel.IsVisible = show;
        owTransitionPick.IsChecked = owLinkFrom is not null && owLinkFrom == owTransition;
        if (!show) return;
        var (x, y) = owTransition!.Value;
        var t = session.OwTransitionAt(x, y);
        bool exit = session.OwLinkableAt(x, y) == true;
        owTransitionTitle.Text = OwTransitionName(x, y);

        owTransitionDest.Text = t switch
        {
            null => "Nowhere yet. Pick a destination to link it.",
            { Location: true } => $"Its own tile: a location teleport, index {t.Value.Slot:X2}, the spot a level's secondary exit can name.",
            { } d => $"{OwTransitionName(d.DestX, OwRow(d.DestY, d.DestSubmapMap))} — index {d.Slot:X2}, " + OwWayBack(x, y, d),
        };
        owTransitionGo.IsEnabled = t is { Location: false };
        var from = session.OwTransitionsInto(x, y);
        owTransitionFrom.Text = from.Count == 0 ? "Nothing leads here."
            : string.Join("\n", from.Select(f => $"{OwTransitionName(f.X, OwRow(f.Y, f.SubmapMap))} — index {f.Slot:X2}"));

        // The index is only where the entry sits in its table — the game finds it by its tile — so
        // it is read off, not chosen. A level's secondary exit naming a warp is the one use of it.
        owTransitionIndex.Text = t is not { } u ? "A slot in its table is taken when it is linked."
            : u.Exit ? $"Exit path index {u.Slot:X2}."
            : $"Warp index {u.Slot:X2} — the number a level's secondary exit uses to come out here.";
        owTransitionFilling = true;
        // The way through an exit tile: the stored one once linked; before that, the choice the
        // link will use, which starts as the map's own reading.
        owTransitionDirRow.IsVisible = exit;
        if (exit) owTransitionDir.SelectedIndex = t is { } e ? e.Dir + 1 : Math.Max(0, owTransitionDir.SelectedIndex);
        owTransitionFilling = false;

        owTransitionClear.IsEnabled = owTransitionUnlink.IsEnabled = t is not null;
        owTransitionNote.Text = OwTransitionWarning(x, y, exit, t);
    }

    /// <summary>"Pipe (0A,05) on Yoshi's Island": the tile's kind, where it is in its map's own
    /// coordinates, and the map.</summary>
    private string OwTransitionName(int x, int y)
    {
        bool sub = y >= Overworld.Rows;
        int my = y % Overworld.Rows;
        string kind = session.Overworld?.Layer1At(x, my, sub) switch
        {
            0x5F => "Star",
            0x5A or 0x5B or 0x82 => "Pipe",
            _ => session.OwLinkableAt(x, y) == true ? "Exit tile" : "Tile",
        };
        return $"{kind} ({x:X2},{my:X2}) on {EditorSession.OwSubmapNames[Overworld.SubmapAt(x, my, sub)]}";
    }

    private static int OwRow(int y, bool submapMap) => y + (submapMap ? Overworld.Rows : 0);

    /// <summary>Whether the trip comes back: the tile it lands on leads here, leads somewhere else,
    /// or leads nowhere — the last two being a one-way trip.</summary>
    private string OwWayBack(int x, int y, EditorSession.OwTransition t)
        => session.OwTransitionAt(t.DestX, OwRow(t.DestY, t.DestSubmapMap)) switch
        {
            { } back when (back.DestX, OwRow(back.DestY, back.DestSubmapMap)) == (x, y) => "and back.",
            { } other => $"one-way: that tile leads to {OwTransitionName(other.DestX, OwRow(other.DestY, other.DestSubmapMap))}.",
            null => "one-way: that tile leads nowhere.",
        };

    /// <summary>What Lunar Magic warns about, for this tile: a star or pipe where the game cannot
    /// read one, and an exit tile whose path does not say which way the player walks through it.</summary>
    private string OwTransitionWarning(int x, int y, bool exit, EditorSession.OwTransition? t)
    {
        if (!exit && EditorSession.OwWarpCellBroken(x, y))
            return "A star or pipe in the first column of the submaps does not work: one of the game's tables cannot say X=0 there.";
        if (exit && t is null && owTransitionDir.SelectedIndex == 0
            && session.Overworld?.TravelDirOf(x, y % Overworld.Rows, y >= Overworld.Rows) < 0)
            return "The path does not say which way the player walks through this tile — a path on exactly one side does. Set Player walks before linking it.";
        return "";
    }

    /// <summary>Pick destination: the map lights every tile this one can link to, and the next
    /// click on one links them as the settings say (OwLinkPicked). Pressed again, it calls it off.</summary>
    private void OnOwTransitionPick(object? sender, RoutedEventArgs e)
    {
        if (owTransition is not { } p || owTransitionPick.IsChecked != true) { CancelOwLink(); return; }
        ArmOwLink(p.X, p.Y);
    }

    /// <summary>The link settings the drawer holds for the tile a link starts from: two-way or
    /// one-way, and for an exit tile the way through it (-1: as the path says). The tile's hover
    /// button links with the defaults when it is not the one the drawer is about.</summary>
    private (bool TwoWay, int Dir) OwLinkSettings((int X, int Y) from)
        => from == owTransition ? (owTransitionTwoWay.IsChecked != false, owTransitionDir.SelectedIndex - 1) : (true, -1);

    /// <summary>Go there: the map scrolls to where this tile leads, and that tile is picked.</summary>
    private void OnOwTransitionGo(object? sender, RoutedEventArgs e)
    {
        if (owTransition is not { } p || session.OwTransitionAt(p.X, p.Y) is not { Location: false } t) return;
        int dy = OwRow(t.DestY, t.DestSubmapMap);
        double step = owView.CellPx * owView.Zoom;
        owView.BringIntoView(OwLinkRect(step, t.DestX, dy).Inflate(4 * step));
        OwPickTransition(session.OwLinkableAt(t.DestX, dy) is not null ? (t.DestX, dy) : owTransition);
    }

    /// <summary>Clear this end: Lunar Magic's No Setting — this tile's entry goes, and the tile it
    /// led to keeps whatever it does. No question: the far end is untouched and a link puts it back.</summary>
    private async void OnOwTransitionClear(object? sender, RoutedEventArgs e)
    {
        if (owTransition is not { } p) return;
        if (session.ClearOwTransition(p.X, p.Y) is { } why) await ConfirmWindow.Notice("Clear transition", why).ShowDialog(this);
        AfterOwTransition();
    }

    /// <summary>The way through an exit tile. Linked, it rewrites the entry at once; not yet
    /// linked, it is what the link will use.</summary>
    private async void OnOwTransitionDir(object? sender, SelectionChangedEventArgs e)
    {
        if (owTransitionFilling || owTransition is not { } p) return;
        if (session.OwTransitionAt(p.X, p.Y) is not { Exit: true }) { owTransitionNote.Text = OwTransitionWarning(p.X, p.Y, true, null); return; }
        int dir = owTransitionDir.SelectedIndex - 1;
        if (dir < 0) dir = session.Overworld?.TravelDirOf(p.X, p.Y % Overworld.Rows, p.Y >= Overworld.Rows) ?? -1;
        if (dir < 0) { await ConfirmWindow.Notice("Exit path", "The path does not say which way the player walks through this tile; pick one.").ShowDialog(this); RefreshOwTransitionPanel(); return; }
        if (session.SetOwExitDir(p.X, p.Y, dir) is { } why) await ConfirmWindow.Notice("Exit path", why).ShowDialog(this);
        AfterOwTransition();
    }

    private void OnOwTransitionUnlink(object? sender, RoutedEventArgs e)
    {
        if (owTransition is { } p) OwUnlinkTransition(p.X, p.Y);
    }

    /// <summary>On the map: the picked tile ringed, where it leads haloed, and a line between the
    /// two — the destination is often on another map, a screen away.</summary>
    private void DrawOwTransitionPick(DrawingContext ctx, double step)
    {
        if (OwModeNow != OwMode.Transitions || owTransition is not { } p) return;
        var at = OwLinkRect(step, p.X, p.Y);
        if (session.OwTransitionAt(p.X, p.Y) is { Location: false } t)
        {
            var to = OwLinkRect(step, t.DestX, OwRow(t.DestY, t.DestSubmapMap));
            Overlay.LinkLine(ctx, at.Center, to.Center);
            Overlay.Armed(ctx, to);
        }
        Overlay.Selection(ctx, at);
    }
}
