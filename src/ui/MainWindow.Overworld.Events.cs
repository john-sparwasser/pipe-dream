using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace PipeDream.Ui;

// The Events tab: the events on the map, picked by clicking one of their pieces, and the picked
// event's steps in a drawer on the right, where they are added, reordered, replaced and deleted —
// Add and Replace choosing their piece in EventPieceWindow. The left drawer's list of every event
// is MainWindow.Overworld.EventList.cs; the rest of the overworld mode is MainWindow.Overworld.cs
// and its other partials.

public partial class MainWindow
{
    private Border owEventPanel = null!;
    private TextBlock owEventTitle = null!;
    private StackPanel owEventStepsList = null!;
    private ToggleButton owEventAdd = null!;
    private Button owEventClear = null!;
    private Button owNewEvent = null!;

    /// <summary>The event picked on the map or in the left drawer's list, or null. Its pieces wear
    /// the selection ring, its row in the list is framed, and its steps fill the right drawer.</summary>
    private int? owEvent;

    /// <summary>The step picked in the right drawer, by its place in the event's order, or null.
    /// Its piece wears the armed halo on the map, so a row can be found on the land.</summary>
    private int? owEventStep;

    /// <summary>Add is armed: the next click on the map lays the armed piece as a step.</summary>
    private bool owEventPlacing;

    private void WireOwEvents()
    {
        owEventPanel = this.GetControl<Border>("OwEventPanel");
        owEventTitle = this.GetControl<TextBlock>("OwEventTitle");
        owEventStepsList = this.GetControl<StackPanel>("OwEventSteps");
        owEventAdd = this.GetControl<ToggleButton>("OwEventAdd");
        owEventClear = this.GetControl<Button>("OwEventClear");
        owNewEvent = this.GetControl<Button>("OwNewEvent");
        WireOwEventList();
    }

    /// <summary>A click on the Events tab's map: with Add armed, it lays the piece there; else it
    /// picks the event whose piece is there, or none on bare land. The map has no reticle and no
    /// lasso on this tab (PickOnLeft): a piece is a thing to point at, not a cell to paint.</summary>
    private async void OwEventClicked((int Col, int Row) cell)
    {
        if (owEventPlacing && owEvent is { } ev && owEventPiece is { } piece)
        {
            string? why = session.OwAddEventStep(ev, piece.Src, piece.Size, cell.Col, cell.Row);
            SetOwEventPlacing(false);
            if (why is not null) { await ConfirmWindow.Notice("Add event step", why).ShowDialog(this); return; }
            AfterOwEventEdit();
            return;
        }
        OwSelectEvent(session.OwEventAt(cell.Col, cell.Row));
    }

    /// <summary>New event: the events that lay nothing yet, one picked opens in the right drawer
    /// ready for Add. Nothing is written until a step is.</summary>
    private async void OnOwNewEvent(object? sender, RoutedEventArgs e)
    {
        var free = session.OwEmptyEvents().ToList();
        if (free.Count == 0) { await ConfirmWindow.Notice("New event", "Every event already lays something.").ShowDialog(this); return; }
        var pick = new SlotNumberWindow("New event", "Start editing which event? These lay nothing yet.", free);
        await pick.ShowDialog(this);
        if (pick.Result is not { } ev) return;
        SetOwEventPlacing(false);
        OwSelectEvent(ev);
    }

    /// <summary>Add: pick the piece in EventPieceWindow, then the next click on the map lays it.
    /// Pressed again while armed, it calls the placement off.</summary>
    private async void OnOwEventAdd(object? sender, RoutedEventArgs e)
    {
        if (owEventAdd.IsChecked != true || owEvent is not { } ev) { SetOwEventPlacing(false); return; }
        owEventAdd.IsChecked = false;               // not armed until a piece is chosen
        var piece = await OwPickEventPiece("Add event step", $"Pick the piece event {ev:X2} lays next, then click where on the map it lands.", null);
        if (piece is null) return;
        owEventPiece = piece;
        SetOwEventPlacing(true);
    }

    /// <summary>The event piece picker, as a dialog over this window: the piece chosen, or null.</summary>
    private async Task<(int Src, int Size)?> OwPickEventPiece(string title, string prompt, int? current)
    {
        var pick = new EventPieceWindow(session, title, prompt, current);
        await pick.ShowDialog(this);
        return pick.Result;
    }

    /// <summary>Arm or disarm a placement. The button shows the state, the note says what to do,
    /// and the map previews the piece's footprint under the pointer while it is armed.</summary>
    private void SetOwEventPlacing(bool on)
    {
        owEventPlacing = on && owEvent is not null && owEventPiece is not null;
        owEventAdd.IsChecked = owEventPlacing;
        owView.RepaintOnHover = owEventPlacing;                   // the footprint preview follows the pointer
        owView.InvalidateVisual();
        RefreshOwNote();
    }

    /// <summary>Clear takes every step off the event. No question first: Ctrl+Z brings them back,
    /// which is what a question was standing in for.</summary>
    private void OnOwEventClear(object? sender, RoutedEventArgs e)
    {
        if (owEvent is not { } ev) return;
        session.OwClearEvent(ev);
        SetOwEventPlacing(false);
        AfterOwEventEdit();
    }

    /// <summary>Ctrl+Z / Ctrl+Y while the Events tab is up rewinds the step table.</summary>
    private void OwEventUndoRedo(bool redo)
    {
        if (redo ? session.OwEventRedo() : session.OwEventUndo()) AfterOwEventEdit();
    }

    private void AfterOwEventEdit()
    {
        RefreshOwEventPanel();
        RefreshOwEventList();
        owView.InvalidateVisual();
        UpdateTitle();
    }

    private void OwSelectEvent(int ev)
    {
        owEvent = ev < 0 ? null : ev;
        owEventStep = null;
        RefreshOwEventPanel();
        OwEventListFrames();
        owView.InvalidateVisual();                  // the ring moves; the cells do not
        RefreshOwNote();
    }

    /// <summary>The right drawer: the selected event's steps in the order the game lays them —
    /// each piece as it lands, in the colours of the submap it lands on, with where. Hidden when
    /// no event is selected, so the map has the width when nothing is being read off it.</summary>
    private void RefreshOwEventPanel()
    {
        bool show = OwModeNow == OwMode.Events && owEvent is { } ev;
        owEventPanel.IsVisible = show;
        owEventStepsList.Children.Clear();
        if (!show) return;
        var steps = session.OwEventSteps(owEvent!.Value).ToList();
        owEventTitle.Text = $"Event {owEvent:X2} — {steps.Count} step{(steps.Count == 1 ? "" : "s")}";
        owEventClear.IsEnabled = steps.Count > 0;
        if (owEventStep >= steps.Count) owEventStep = null;       // a delete or undo took it
        for (int i = 0; i < steps.Count; i++) owEventStepsList.Children.Add(OwEventStepRow(i, steps[i], steps.Count));
    }

    /// <summary>One step: its number, the piece as it lands, where it lands, and its edits.</summary>
    private Control OwEventStepRow(int i, Overworld.EventStep s, int steps)
    {
        var img = session.OwEventStepImage(s);
        int side = s.Size * 8;
        var picture = new PixelImage { Source = LevelBitmap.FromPixels(img, side, side), Zoom = 2, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = $"{i + 1}.  {s.Size}x{s.Size} piece 0x{s.Piece:X3}" });
        var place = new TextBlock { Text = $"{EditorSession.OwEventStepPlace(s)}  ({s.Cx >> 1}, {(s.Cy >> 1) % Overworld.Rows})" };
        place.Classes.Add("dim");
        text.Children.Add(place);
        text.Children.Add(OwEventStepButtons(i, steps));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(picture);
        row.Children.Add(text);
        var card = new Border { Child = row, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(4),
                                Background = (IBrush)this.FindResource("SurfaceBrush")!, BorderThickness = new Thickness(1),
                                BorderBrush = OwStepFrame(owEventStep == i) };
        // A press on the row picks the step; its buttons take their own presses, so they do not.
        card.PointerPressed += (_, _) => OwSelectEventStep(owEventStep == i ? null : i);
        return card;
    }

    private IBrush OwStepFrame(bool picked) => picked ? (IBrush)this.FindResource("AccentBrush")! : Brushes.Transparent;

    /// <summary>Pick a step in the drawer, or let go of it: the row is framed and its piece lit on the map.</summary>
    private void OwSelectEventStep(int? i)
    {
        owEventStep = i;
        // Only the frames change: rebuilding the rows, pictures and buttons and all, cost a
        // noticeable pause per click on a long event.
        for (int k = 0; k < owEventStepsList.Children.Count; k++)
            if (owEventStepsList.Children[k] is Border card) card.BorderBrush = OwStepFrame(owEventStep == k);
        owView.InvalidateVisual();
    }

    /// <summary>A step's edits: move it up or down its event's order, lay another piece in its
    /// place (same spot, same turn), or take it off. Each is one undo step.</summary>
    private StackPanel OwEventStepButtons(int i, int steps)
    {
        Button Small(string label, string tip, bool enabled, Action act)
        {
            var b = new Button { Content = label, Padding = new Thickness(6, 0), FontSize = 11, IsEnabled = enabled };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bar.Children.Add(Small("↑", "Lay this step one earlier", i > 0, () => OwMoveEventStep(i, i - 1)));
        bar.Children.Add(Small("↓", "Lay this step one later", i < steps - 1, () => OwMoveEventStep(i, i + 1)));
        bar.Children.Add(Small("Replace", "Pick another piece to lay here instead, in the same spot", true, () => OwReplaceEventStep(i)));
        bar.Children.Add(Small("Delete", "Take this step off the event. Undo brings it back", true, () => OwEventStepEdit(ev => { if (!session.OwDeleteEventStep(ev, i)) return false; owEventStep = null; return true; })));
        return bar;
    }

    /// <summary>A moved step stays picked, so a run of ↑ or ↓ follows the one being moved.</summary>
    private void OwMoveEventStep(int i, int j)
        => OwEventStepEdit(ev => { if (!session.OwMoveEventStep(ev, i, j)) return false; owEventStep = j; return true; });

    private void OwEventStepEdit(Func<int, bool> edit)
    {
        if (owEvent is not { } ev || !edit(ev)) return;
        SetOwEventPlacing(false);
        AfterOwEventEdit();
    }

    /// <summary>Replace: pick the new piece in EventPieceWindow, the step's own piece lit there.</summary>
    private async void OwReplaceEventStep(int i)
    {
        if (owEvent is not { } ev || session.OwEventSteps(ev).ElementAtOrDefault(i) is not { Size: > 0 } step) return;
        if (await OwPickEventPiece("Replace event step", $"Pick the piece step {i + 1} of event {ev:X2} lays instead, in the same spot.", step.Piece) is not { } piece) return;
        if (session.OwReplaceEventStep(ev, i, piece.Src, piece.Size) is { } why)
        {
            await ConfirmWindow.Notice("Replace event step", why).ShowDialog(this);
            return;
        }
        SetOwEventPlacing(false);
        AfterOwEventEdit();
    }

    /// <summary>The piece Add lays, as an event step names it: its source offset and its side.
    /// Chosen in EventPieceWindow; null until one is.</summary>
    private (int Src, int Size)? owEventPiece;
}
