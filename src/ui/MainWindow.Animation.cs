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
/// Animations mode: the ExAnimation timeline — the list's slots down the left, the open
/// slot's editor and frame strip on the right, an animated preview in the drawer. Slots are
/// read and written through the session; the frame picker is <see cref="TilePickerWindow"/>.
/// </summary>
public partial class MainWindow
{
    private DockPanel animPane = null!, animToolPanel = null!;

    private StackPanel animGfx = null!, animBody = null!;
    private TextBlock animTitle = null!, animListTitle = null!;
    private Button animDelete = null!, animReassign = null!, animEmptyAdd = null!;
    private CheckBox animAdvanced = null!;
    private StackPanel animPreviewBody = null!;
    private ToggleButton animLevelBtn = null!, animGlobalBtn = null!;
    private ComboBox animFile = null!, animPalRow = null!;
    private Border animPaletteBar = null!;
    private PaletteGridView animColors = null!;

    /// <summary>Which list the timeline shows (the level's or the global one) and which of its
    /// slots is open on the right.</summary>
    private bool animGlobal = true;          // the global list opens first; Level is the toggle
    private int animSelected = -1;
    private DispatcherTimer? animPreview;
    private bool loadingAnimHeader;

    /// <summary>Animation mode: the ExAnimation timeline and its header.</summary>
    private void WireAnimation()
    {
        animPane = this.GetControl<DockPanel>("AnimPane");
        animToolPanel = this.GetControl<DockPanel>("AnimToolPanel");
        animGfx = this.GetControl<StackPanel>("AnimGfx");
        animBody = this.GetControl<StackPanel>("AnimBody");
        animTitle = this.GetControl<TextBlock>("AnimTitle");
        animDelete = this.GetControl<Button>("AnimDelete");
        animReassign = this.GetControl<Button>("AnimReassign");
        animEmptyAdd = this.GetControl<Button>("AnimEmptyAdd");
        animPreviewBody = this.GetControl<StackPanel>("AnimPreviewBody");
        this.GetControl<ScrollViewer>("AnimBodyScroll").Background = UiColors.DeskPattern;   // the timeline on the desk
        animListTitle = this.GetControl<TextBlock>("AnimListTitle");
        animLevelBtn = this.GetControl<ToggleButton>("AnimLevel");
        animGlobalBtn = this.GetControl<ToggleButton>("AnimGlobal");
        animFile = this.GetControl<ComboBox>("AnimFile");
        animAdvanced = this.GetControl<CheckBox>("AnimAdvanced");
        animAdvanced.IsCheckedChanged += (_, _) => RefreshAnim();
        animPalRow = this.GetControl<ComboBox>("AnimPalRow");
        for (int i = 0; i < 16; i++) animPalRow.Items.Add($"{i}");   // all sixteen: a destination can be sprite VRAM too
        animPalRow.SelectedIndex = 2;
        animPaletteBar = this.GetControl<Border>("AnimPaletteBar");
        animColors = this.GetControl<PaletteGridView>("AnimColors");
        animColors.Rows = 1;
        animColors.Cell = 20;
        animColors.Selectable = false;     // shows the row; the tiles over the destination choose it
        // The list's source file is part of its record: changing it rewrites the record with the
        // same slots. The palette row is display-only.
        animFile.SelectionChanged += (_, _) =>
        {
            if (loadingAnimHeader || animFile.SelectedIndex < 0 || !session.HasLevel) return;
            if (animFile.SelectedIndex != session.ExAnimAltFile(animGlobal))
            { session.SetExAnim(animGlobal, session.ExAnimSlots(animGlobal), animFile.SelectedIndex); RefreshAnim(); }
        };
        animPalRow.SelectionChanged += (_, _) => { if (modeAnim.IsChecked == true) RefreshAnim(); };
    }

    // ---- the header bar ----

    private void OnAnimList(object? sender, RoutedEventArgs e)
    {
        animGlobal = ReferenceEquals(sender, animGlobalBtn);
        animLevelBtn.IsChecked = !animGlobal;
        animGlobalBtn.IsChecked = animGlobal;
        animSelected = -1;
        RefreshAnim();
    }

    /// <summary>Add a slot NOW, decide later: it comes into being as one 8x8 with one frame, and
    /// every decision — type, trigger, destination, which tiles, how many frames — is made on the
    /// timeline it opens into.</summary>
    private void OnAnimAdd(object? sender, RoutedEventArgs e)
    {
        if (session.AddExAnimSlot(animGlobal) is not { } slot) return;
        animSelected = slot.Index;
        RefreshAnim();
    }

    /// <summary>Header button: drop the open slot from the list (the record is rewritten without it).</summary>
    private void OnAnimDelete(object? sender, RoutedEventArgs e)
    {
        if (animSelected < 0) return;
        session.SetExAnim(animGlobal, session.ExAnimSlots(animGlobal).Where(x => x.Index != animSelected).ToList(), session.ExAnimAltFile(animGlobal));
        animSelected = -1;
        RefreshAnim();
    }

    /// <summary>Header button: move the open slot to another slot number, picked in a modal
    /// from the numbers this list still has free.</summary>
    private async void OnAnimReassign(object? sender, RoutedEventArgs e)
    {
        var slots = session.ExAnimSlots(animGlobal);
        if (slots.All(s => s.Index != animSelected)) return;
        var free = Enumerable.Range(0, 0x20).Where(i => slots.All(s => s.Index != i)).ToList();
        if (free.Count == 0) return;                          // all 32 in use: nowhere to go
        var dlg = new SlotNumberWindow(animSelected, free);
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } to) return;
        if (session.ReassignExAnimSlot(animGlobal, animSelected, to))
        {
            animSelected = to;
            RefreshAnim();
        }
    }

    /// <summary>The gutter's sixteen swatches for the preview row — the Map16 bar's logic.</summary>
    private void RefreshAnimColors(int row)
    {
        var colors = new uint[16];
        if (row >= 0 && session.PaletteRgba is { } pal && pal.Length >= (row + 1) * 16)
            for (int i = 0; i < 16; i++)
                colors[i] = i == 0 ? 0xFF303030u : pal[row * 16 + i];
        animColors.Cols = 16;
        animColors.Colors = colors;
        animColors.InvalidateVisual();
    }

    // ---- the timeline ----

    /// <summary>Write one slot back and redraw.</summary>
    private void PutSlot(ExAnimation.Slot slot)
    {
        animSelected = slot.Index;
        if (session.SetExAnimSlot(animGlobal, slot)) RefreshAnim();
    }

    /// <summary>
    /// The timeline: the left lists the list's slots in slot order (click one to open it), the
    /// right is the open slot's editor — type, trigger and destination inline, an animated preview
    /// at the game's 7.5 fps, and the frame strip: click a frame to pick its tiles on the source
    /// sheet, × to drop it, + at the end to add one. The header picks the list, adds slots, and
    /// sets the list's source file and the preview palette row.
    /// </summary>
    private void RefreshAnim()
    {
        animPreview?.Stop(); animPreview = null;
        animBody.Children.Clear(); animPreviewBody.Children.Clear();
        animGfx.Children.Clear();
        animEmptyAdd.IsVisible = false;
        if (session.Rom is not { } rom) return;
        bool ready = rom.LmExAnimBase >= 0;
        animTitle.Text = !ready ? "no ExAnimation engine — File → Upgrade base (prep v11)" : "";
        animListTitle.Text = animGlobal ? "Global slots" : $"Level {session.LevelNum:X3} slots";
        if (!ready) return;

        var slots = session.ExAnimSlots(animGlobal).OrderBy(s => s.Index).ToList();
        int alt = session.ExAnimAltFile(animGlobal);
        loadingAnimHeader = true;
        animFile.SelectedIndex = alt;
        loadingAnimHeader = false;
        int palRow = Math.Max(0, animPalRow.SelectedIndex);
        RefreshAnimColors(palRow);
        if (slots.All(s => s.Index != animSelected)) animSelected = slots.Count > 0 ? slots[0].Index : -1;

        AnimSlotList(slots, palRow);

        // ---- right: the open slot's editor ----
        animDelete.IsEnabled = slots.Any(s => s.Index == animSelected);   // the header's Delete acts on the open slot
        animReassign.IsEnabled = animDelete.IsEnabled;                    // ...and so does Reassign
        if (slots.FirstOrDefault(s => s.Index == animSelected) is not { Frames: not null } sel) return;
        AnimSlotEditor(sel, palRow);
    }

    /// <summary>The left column: one card per slot in slot order; clicking one opens it.</summary>
    private void AnimSlotList(List<ExAnimation.Slot> slots, int palRow)
    {
        if (slots.Count == 0)
        {
            animGfx.Children.Add(Dim("No slots yet — Add slot in the bar above."));
            animEmptyAdd.IsVisible = true;                    // ...or right here, centred on the desk
        }
        foreach (var s in slots)
        {
            bool open = s.Index == animSelected;
            var head = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new TextBlock { Text = $"[{s.Index:X2}]", Width = 34, FontWeight = FontWeight.Bold, Foreground = (IBrush)this.FindResource("TextDimBrush")! });
            head.Children.Add(new TextBlock { Text = SlotTitle(s), TextTrimming = TextTrimming.CharacterEllipsis });
            var block = new StackPanel();
            block.Children.Add(new Border { Child = head, Padding = new Thickness(8, 6), Background = (IBrush)this.FindResource("SurfaceBrush")!, CornerRadius = new CornerRadius(4, 4, 0, 0) });
            var (px, w, h) = session.ExAnimFramePixels(s, 0, palRow);
            block.Children.Add(px.Length > 0
                ? new PixelImage { Source = LevelBitmap.FromPixels(px, w, h), Width = w * 4, Height = h * 4, Stretch = true, Margin = new Thickness(8, 6), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }
                : Mono(s.IsPalette ? $"palette {s.DestColor:X2} x{s.Colors}" : "(source not loaded)"));
            var card = new Border
            {
                Child = block, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(2),
                BorderBrush = open ? UiColors.Accent : this.FindResource("BorderBrush") as IBrush,
                Background = open ? UiColors.SelectionFill : Brushes.Transparent, Cursor = UiCursors.Hand,
            };
            int idx = s.Index;
            card.PointerPressed += (_, _) => { animSelected = idx; RefreshAnim(); };
            animGfx.Children.Add(card);
        }
    }

    /// <summary>The right column for the open slot: its decisions inline, then the frame strip
    /// and the animated preview built from it.</summary>
    private void AnimSlotEditor(ExAnimation.Slot sel, int palRow)
    {
        // The decisions, inline. Each change writes the slot straight back — there is no OK.
        var row = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        // Simple mode culls both dropdowns to the everyday choices; Advanced shows the engine's
        // full catalog. A slot already using a culled value keeps it in the list — filtering the
        // DISPLAY must never rewrite the slot.
        bool adv = animAdvanced.IsChecked == true;
        var type = AnimTypeBox(sel, adv);
        var trig = AnimTriggerBox(sel, adv);
        row.Children.Add(Labelled("type", type));
        row.Children.Add(Labelled("trigger", trig));
        if (sel.IsPalette)
        {
            var color = HexBox(sel.DestColor.ToString("X2"), 2, v => PutSlot(sel with { DestWord = (sel.DestWord & 0xFF00) | (v & 0xFF) }));
            var count = HexBox(sel.Colors.ToString(), 3, v => PutSlot(sel with { DestWord = (sel.DestWord & 0x80FF) | ((Math.Clamp(v, 1, 0x80) - 1) << 8) }), hex: false);
            row.Children.Add(Labelled("first colour", color));
            row.Children.Add(Labelled("colours", count));
        }
        else
        {
            var dest = AnimDestinationButton(sel, palRow);
            row.Children.Add(Labelled("destination", dest));
        }
        animBody.Children.Add(row);
        string note = sel.Doubled ? "Stateful trigger: the first half of the frames plays untriggered, the second half once triggered."
                    : sel.Trigger >= ExAnimation.TriggerOneShot0 ? "One shot: plays through once when triggered, then stops."
                    : sel.Trigger >= ExAnimation.TriggerManual0 ? "Manual: shows whichever frame a custom block writes to $7FC070+n." : "";
        if (note.Length > 0) animBody.Children.Add(Dim(note));
        if (sel.IsPalette)
        {
            animBody.Children.Add(Dim(ExAnimation.HasFrameWords(sel.Type)
                ? "Palette slot: each frame is an SNES colour word (BGR555). Click a frame to type one."
                : "Palette rotation: no frame data — the frame count is the delay between steps."));
        }

        var (strip, frames) = AnimFrameStrip(sel, palRow);
        AnimPreview(sel, frames);
        animBody.Children.Add(strip);
    }

    /// <summary>The frame strip: a card per frame (× to drop it) and the + at the end. Also
    /// hands back the frame bitmaps, which the preview animates.</summary>
    private (WrapPanel Strip, List<Avalonia.Media.Imaging.Bitmap> Frames) AnimFrameStrip(ExAnimation.Slot sel, int palRow)
    {
        var frames = new List<Avalonia.Media.Imaging.Bitmap>();
        var strip = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        int total = ExAnimation.HasFrameWords(sel.Type) ? sel.Frames.Length : 0;
        for (int f = 0; f < total; f++) strip.Children.Add(AnimFrameCard(sel, f, palRow, frames));
        if (total > 0 && sel.FrameCount < 0x100)
        {
            // The + at the end of the timeline: a new frame, a copy of the last, in both halves
            // when the trigger keeps two.
            // The cta class carries the accent fill AND its lighter-blue hover — an inline
            // Background would win the base state but lose :pointerover to the template's brush,
            // which showed as a translucent grey on hover.
            var plus = new Button
            {
                Content = "Add Frame", Height = 48, Padding = new Thickness(14, 0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
            };
            plus.Classes.Add("cta");
            ToolTip.SetTip(plus, "add a frame");
            plus.Click += (_, _) => PutSlot(WithAddedFrame(sel));
            strip.Children.Add(plus);
        }
        return (strip, frames);
    }

    /// <summary>The type dropdown: the everyday types, or the engine's whole catalog when Advanced is on.</summary>
    private ComboBox AnimTypeBox(ExAnimation.Slot sel, bool adv)
    {
        var types = (adv ? ExAnimSlotWindow.Types
                         : ExAnimSlotWindow.Types.Where(t => t.Code is <= 0x08 or 0x0F or 0x11)).ToList();
        if (types.All(t => t.Code != sel.Type))
            types.Add(ExAnimSlotWindow.Types.First(t => t.Code == sel.Type));
        var type = new ComboBox { ItemsSource = types.Select(t => t.Name).ToList(), Width = 250,
                                  SelectedIndex = Math.Max(0, types.FindIndex(t => t.Code == sel.Type)) };
        type.SelectionChanged += (_, _) =>
        {
            int code = types[type.SelectedIndex].Code;
            if (code == sel.Type) return;
            // Tile ↔ palette keep nothing in common: a palette slot starts on colour 00 with its
            // frame words as colours; a tile slot goes back to tile 600. Tile ↔ tile keeps the frames.
            var s2 = sel with { Type = code };
            bool wasPal = sel.IsPalette, isPal = code >= ExAnimation.TypePalette;
            if (wasPal != isPal) s2 = s2 with { DestWord = 0, Frames = [.. Enumerable.Repeat((ushort)(isPal ? 0x7FFF : 0x7D00), sel.Frames.Length)] };
            PutSlot(s2);
        };
        return type;
    }

    /// <summary>The trigger dropdown, culled the same way as the types.</summary>
    private ComboBox AnimTriggerBox(ExAnimation.Slot sel, bool adv)
    {
        var trigs = (adv ? ExAnimSlotWindow.Triggers
                         : ExAnimSlotWindow.Triggers.Where(t => t.Code <= 0x04)).ToList();   // None..Have Star
        if (trigs.All(t => t.Code != sel.Trigger))
            trigs.Add(ExAnimSlotWindow.Triggers.First(t => t.Code == sel.Trigger));
        var trig = new ComboBox { ItemsSource = trigs.Select(t => t.Name).ToList(), Width = 210,
                                  SelectedIndex = Math.Max(0, trigs.FindIndex(t => t.Code == sel.Trigger)) };
        trig.SelectionChanged += (_, _) =>
        {
            int code = trigs[trig.SelectedIndex].Code;
            if (code == sel.Trigger) return;
            // Going stateful doubles the list (the triggered half starts as a copy); going back keeps the first half.
            bool was = ExAnimation.TriggerDoubles(sel.Trigger), now = ExAnimation.TriggerDoubles(code);
            ushort[] frames = sel.Frames;
            if (!was && now) frames = [.. frames, .. frames];
            else if (was && !now) frames = frames[..Math.Min(sel.FrameCount, frames.Length)];
            PutSlot(sel with { Trigger = code, Frames = frames });
        };
        return trig;
    }

    /// <summary>The destination button: what sits at the destination now, and a click to re-pick it.</summary>
    private Button AnimDestinationButton(ExAnimation.Slot sel, int palRow)
    {
        // The destination is picked on the level's VRAM sheet; the button shows what sits there
        // now — the tiles the animation will overwrite — in the slot's own footprint.
        var (dpx, dw, dh) = session.ExAnimDestPixels(sel, palRow);
        var destFace = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        if (dpx.Length > 0) destFace.Children.Add(new PixelImage { Source = LevelBitmap.FromPixels(dpx, dw, dh), Width = dw * 3, Height = dh * 3, Stretch = true, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        destFace.Children.Add(Mono($"{sel.DestTile:X3}"));
        var dest = new Button { Content = destFace, Padding = new Thickness(8, 4) };
        ToolTip.SetTip(dest, "click to pick the destination on the level's VRAM sheet");
        dest.Click += async (_, _) =>
        {
            var pick = new TilePickerWindow(session, sel, palRow);
            await pick.ShowDialog(this);
            if (pick.Picked is { } t) PutSlot(sel with { DestWord = (sel.DestWord & 0x8000) | ExAnimation.LmTileToWord(t) });
        };
        return dest;
    }

    /// <summary>One frame's card: its label and × in a head band, the frame face on a mat, and the
    /// tile or colour under it. A tile frame's bitmap is also added to <paramref name="frames"/> for the preview.</summary>
    private Border AnimFrameCard(ExAnimation.Slot sel, int f, int palRow, List<Avalonia.Media.Imaging.Bitmap> frames)
    {
        int fi = f;
        bool triggered = sel.Doubled && f >= sel.FrameCount;
        var col = new StackPanel { Spacing = 6, Margin = new Thickness(10, 8, 10, 10) };
        // Label on the left, × pinned to the right — a full-width header band across the
        // card's top, the same treatment as the slot listing's card headers.
        var top = new DockPanel();
        if (sel.FrameCount > 1 && !triggered)
        {
            var x = new Button { Content = "×", Padding = new Thickness(5, 0), FontSize = 11,
                                 HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            ToolTip.SetTip(x, "remove this frame");
            x.Click += (_, _) => PutSlot(WithoutFrame(sel, fi));
            DockPanel.SetDock(x, Avalonia.Controls.Dock.Right);
            top.Children.Add(x);
        }
        var label = Mono((triggered ? "Triggered " : "Frame ") + $"{(f % sel.FrameCount) + 1}");
        label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 10, 0);           // room before the ×, two digits included
        label.Foreground = Brushes.White;                    // a header, not a dim annotation
        top.Children.Add(label);

        Control face;
        if (sel.IsPalette)
        {
            int bgr = sel.Frames[f];
            var sw = new Border { Width = 48, Height = 32, CornerRadius = new CornerRadius(3),
                                  Background = new SolidColorBrush(Color.FromRgb((byte)((bgr & 31) * 8), (byte)(((bgr >> 5) & 31) * 8), (byte)(((bgr >> 10) & 31) * 8))) };
            face = sw;
            col.Children.Add(FrameMat(face));
            col.Children.Add(Mono($"{bgr:X4}"));
        }
        else
        {
            var (px, w, h) = session.ExAnimFramePixels(sel, f, palRow);
            Avalonia.Media.Imaging.Bitmap? bmp = px.Length > 0 ? LevelBitmap.FromPixels(px, w, h) : null;
            if (bmp is not null && !triggered) frames.Add(bmp);
            face = bmp is not null
                ? new PixelImage { Source = bmp, Width = w * 4, Height = h * 4, Stretch = true }
                : new Border { Width = 64, Height = 32, Background = (IBrush)this.FindResource("SurfaceBrush")!, Child = Mono("pick…") };
            col.Children.Add(FrameMat(face));
            col.Children.Add(Mono($"tile {sel.SrcTile(f):X3}"));
        }
        // Lighter than the card body: the Surface tone sank into the desk pattern behind it.
        var head = new Border { Child = top, Padding = new Thickness(10, 5),
                                CornerRadius = new CornerRadius(5, 5, 0, 0),
                                Background = (IBrush)this.FindResource("BorderBrush")! };
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(col);
        var cardF = new Border { Child = stack, Margin = new Thickness(0, 0, 8, 8), MinWidth = 104,
                                 CornerRadius = new CornerRadius(5),
                                 Cursor = UiCursors.Hand,
                                 Background = this.FindResource("RaisedBrush") as IBrush };
        ToolTip.SetTip(cardF, sel.IsPalette ? "click to set this frame's colour" : "click to pick this frame's tiles on the source sheet");
        cardF.PointerPressed += async (_, _) => await PickFrame(sel, fi, palRow);
        // The whole card — band included — lightens under the pointer, so it reads as one button.
        var headBg = head.Background;
        cardF.PointerEntered += (_, _) => { cardF.Background = FrameCardHover; head.Background = FrameHeadHover; };
        cardF.PointerExited += (_, _) => { cardF.Background = this.FindResource("RaisedBrush") as IBrush; head.Background = headBg; };
        return cardF;
    }

    /// <summary>The animated preview in the drawer, ticking at the game's 7.5 fps.</summary>
    private void AnimPreview(ExAnimation.Slot sel, List<Avalonia.Media.Imaging.Bitmap> frames)
    {
        if (frames.Count > 0)
        {
            // The animated preview lives in the right drawer, scaled to fit its width.
            int scale = Math.Clamp(200 / frames[0].PixelSize.Width, 2, 8);
            var preview = new PixelImage { Source = frames[0], Width = frames[0].PixelSize.Width * scale, Height = frames[0].PixelSize.Height * scale, Stretch = true,
                                           HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            animPreviewBody.Children.Add(new Border { Child = preview, Padding = new Thickness(8), Background = (IBrush)this.FindResource("SurfaceBrush")!, CornerRadius = new CornerRadius(5),
                                                      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
            animPreviewBody.Children.Add(Dim($"{frames.Count} frame(s) at the game's rate (7.5 fps) → destination tile {sel.DestTile:X3}."));
            int at = 0;
            animPreview = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 7.5) };
            animPreview.Tick += (_, _) => { at = (at + 1) % frames.Count; preview.Source = frames[at]; };
            animPreview.Start();
        }
    }

    // The timeline's building blocks: dim and mono text, a labelled control, a hex box.
    private static TextBlock Dim(string t) { var b = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap }; b.Classes.Add("dim"); return b; }
    private static TextBlock Mono(string t) { var b = new TextBlock { Text = t }; b.Classes.Add("mono"); return b; }
    private static Control Labelled(string label, Control c)
    {
        var l = new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        l.Classes.Add("dim");
        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 14, 6), Children = { l, c } };
    }
    // A small hex (or decimal) box that commits on Enter or focus loss, so typing does not rewrite the ROM per keystroke.
    private static TextBox HexBox(string text, int width, Action<int> commit, bool hex = true)
    {
        var box = new TextBox { Text = text, Width = 26 + width * 12 };
        box.Classes.Add("mono");
        void Commit()
        {
            try { int v = hex ? Convert.ToInt32(box.Text?.Trim(), 16) : int.Parse(box.Text?.Trim() ?? ""); if ((box.Text ?? "").Trim() != text) commit(v); }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { box.Text = text; }
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostFocus += (_, _) => Commit();
        return box;
    }
    private static string SlotTitle(ExAnimation.Slot s)
    {
        string type = ExAnimSlotWindow.Types.FirstOrDefault(t => t.Code == s.Type).Name ?? $"type {s.Type:X2}";
        string trig = s.Trigger == 0 ? "" : " · " + (ExAnimSlotWindow.Triggers.FirstOrDefault(t => t.Code == s.Trigger).Name ?? $"trigger {s.Trigger:X2}");
        return s.IsPalette ? $"{type} → colour {s.DestColor:X2}{trig}" : $"{type} → {s.DestTile:X3}{trig}";
    }

    /// <summary>A frame's tiles are chosen on the source sheet; a palette frame's colour is typed.
    /// Picking from the alternate file flips the slot to alt-file sourcing (and back), since that is
    /// a slot-wide switch in the record — the other frames keep their words and get re-picked.</summary>
    private async Task PickFrame(ExAnimation.Slot sel, int f, int palRow)
    {
        if (sel.IsPalette)
        {
            var dlg = new TextPromptWindow("Frame colour — SNES colour word (BGR555, hex; 7FFF is white)", sel.Frames[f].ToString("X4"));
            await dlg.ShowDialog(this);
            if (dlg.Result is not { } txt) return;
            try { var fr = (ushort[])sel.Frames.Clone(); fr[f] = (ushort)Convert.ToInt32(txt.Trim(), 16); PutSlot(sel with { Frames = fr }); }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { animTitle.Text = "not a hex colour"; }
            return;
        }
        // The footprint on the SHEET is always a consecutive run of the slot's tiles: the engine
        // DMAs a frame as one line from the source, so that is where the tiles live — a 16x16 is
        // drawn as four tiles in a row (TL TR BL BR), exactly as Lunar Magic asks. Nothing is
        // copied or packed; the frame word names the run directly.
        int alt = session.ExAnimAltFile(animGlobal);
        int[] footprint = Enumerable.Range(0, Math.Max(1, sel.TileCount)).ToArray();
        var pick = new TilePickerWindow(session, footprint, alt, palRow, sel.AltFile, animGlobal)
        {
            // "Edit…" on the alternate file: straight to the Graphics editor on that file, the
            // way clicking its E6x card there would.
            EditRequested = file => { gfxSlot = file; EditGfxFile(file, palRow); },
        };
        await pick.ShowDialog(this);
        if (pick.Picked is not { } tile) return;

        int word = ExAnimSlotWindow.TileToWord(tile, pick.PickedAlt, alt);
        if (word < 0) return;
        bool useAlt = pick.PickedAlt;
        var frames = (ushort[])sel.Frames.Clone();
        frames[f] = (ushort)word;
        int destWord = useAlt ? sel.DestWord | 0x8000 : sel.DestWord & 0x7FFF;
        PutSlot(sel with { Frames = frames, DestWord = destWord });
    }

    /// <summary>Hover tones for the frame cards: one step lighter than RaisedColor/BorderColor.</summary>
    private static readonly IBrush FrameCardHover = new SolidColorBrush(Color.Parse("#323947"));

    private static readonly IBrush FrameHeadHover = new SolidColorBrush(Color.Parse("#404757"));

    /// <summary>The 4px mat around a frame card's preview, so the pixels read as a framed
    /// thumbnail rather than art floating on the card.</summary>
    private Border FrameMat(Control face) => new()
    {
        Child = face, BorderThickness = new Thickness(4),
        BorderBrush = this.FindResource("BorderBrush") as IBrush,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    };

    /// <summary>One more frame, a copy of the last — added to both halves of a doubled list.</summary>
    private static ExAnimation.Slot WithAddedFrame(ExAnimation.Slot s)
    {
        int n = s.FrameCount;
        var a = s.Frames.Take(n).ToList();
        a.Add(a.Count > 0 ? a[^1] : (ushort)0x7D00);
        if (s.Doubled)
        {
            var b = s.Frames.Skip(n).Take(n).ToList();
            b.Add(b.Count > 0 ? b[^1] : a[^1]);
            a.AddRange(b);
        }
        return s with { FrameCount = n + 1, Frames = [.. a] };
    }

    private static ExAnimation.Slot WithoutFrame(ExAnimation.Slot s, int f)
    {
        int n = s.FrameCount;
        if (n <= 1) return s;
        var a = s.Frames.Take(n).ToList(); a.RemoveAt(f);
        if (s.Doubled)
        {
            var b = s.Frames.Skip(n).Take(n).ToList();
            if (f < b.Count) b.RemoveAt(f);
            a.AddRange(b);
        }
        return s with { FrameCount = n - 1, Frames = [.. a] };
    }
}
