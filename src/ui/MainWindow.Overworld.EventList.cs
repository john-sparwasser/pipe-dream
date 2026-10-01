using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

// The Events tab's left drawer: every event that lays something, by number, each with the first
// pieces it lays, and a click on one opening it in the right drawer. The picked event, the right drawer and the map's
// side of the tab are MainWindow.Overworld.Events.cs.

public partial class MainWindow
{
    private StackPanel owEventList = null!;
    private ScrollViewer owEventListScroll = null!, owSheetScroll = null!;

    /// <summary>How many of an event's pieces its row previews.</summary>
    private const int OwPreviewSteps = 6;

    // Each event's row and the steps it was drawn from. A row is rebuilt only when its event's
    // steps change, so an edit — or an undo, which can reach any event — costs the rows it
    // changed rather than all 0x78 of them.
    private readonly Border?[] owEventRows = new Border?[Overworld.EventCount];
    private readonly List<Overworld.EventStep>?[] owEventRowSteps = new List<Overworld.EventStep>?[Overworld.EventCount];

    private void WireOwEventList()
    {
        owEventList = this.GetControl<StackPanel>("OwEventList");
        owEventListScroll = this.GetControl<ScrollViewer>("OwEventListScroll");
        owSheetScroll = this.GetControl<ScrollViewer>("OwSheetScroll");
    }

    /// <summary>The drawer on the Events tab is the list, not the sheet. Every row is redrawn on
    /// the way in: the pieces' pictures can have changed on the Tiles tab, or with the palette,
    /// without any event's steps changing.</summary>
    private void ShowOwEventList(bool events)
    {
        owSheetScroll.IsVisible = !events;
        owEventListScroll.IsVisible = events;
        if (events) RefreshOwEventList(redrawAll: true);
    }

    /// <summary>Bring every row up to its event's steps, rebuilding only those that differ.</summary>
    private void RefreshOwEventList(bool redrawAll = false)
    {
        for (int ev = 0; ev < Overworld.EventCount; ev++)
        {
            var steps = session.OwEventSteps(ev).ToList();
            if (!redrawAll && owEventRows[ev] is not null && owEventRowSteps[ev] is { } had && had.SequenceEqual(steps)) continue;
            var row = OwEventListRow(ev, steps);
            if (ev < owEventList.Children.Count) owEventList.Children[ev] = row;
            else owEventList.Children.Add(row);
            owEventRows[ev] = row;
            owEventRowSteps[ev] = steps;
        }
        OwEventListFrames();
    }

    /// <summary>One event: its number and how many steps it has, and its first pieces as they land.
    /// An event that lays nothing keeps its row, hidden — so a row is still found by its event's
    /// number, and the first Add (New event starts one) shows it.</summary>
    private Border OwEventListRow(int ev, List<Overworld.EventStep> steps)
    {
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 64, Spacing = 2 };
        label.Children.Add(new TextBlock { Text = $"Event {ev:X2}" });
        var count = new TextBlock { Text = $"{steps.Count} step{(steps.Count == 1 ? "" : "s")}" };
        count.Classes.Add("dim");
        label.Children.Add(count);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(label);
        // Half size: a 6x6 piece is 24 pixels, and six of them fit a drawer beside the number.
        if (session.OwEventPreview(ev, OwPreviewSteps) is { } strip)
            row.Children.Add(new PixelImage { Source = LevelBitmap.FromPixels(strip.Px, strip.W, strip.H), Zoom = 0.5,
                                              VerticalAlignment = VerticalAlignment.Center });
        var card = new Border { Child = row, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(4),
                                Background = (IBrush)this.FindResource("SurfaceBrush")!, BorderThickness = new Thickness(1),
                                IsVisible = steps.Count > 0, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        card.PointerPressed += (_, _) => { SetOwEventPlacing(false); OwSelectEvent(ev); };
        return card;
    }

    /// <summary>The picked event's row framed, as a step's row is in the right drawer, and scrolled
    /// to — a pick on the map may be of an event far down the list.</summary>
    private void OwEventListFrames()
    {
        for (int ev = 0; ev < Overworld.EventCount; ev++)
            if (owEventRows[ev] is { } row) row.BorderBrush = OwStepFrame(owEvent == ev);
        if (owEvent is { } picked && owEventListScroll.IsVisible) owEventRows[picked]?.BringIntoView();
    }
}
