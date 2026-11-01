using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

// MainWindow — the Overworld's Sprites tab: Lunar Magic's custom overworld sprites, as its Sprite
// Editor mode places them. Right-click places the drawer's number, a left drag moves a sprite in
// 8px steps, Delete removes the picked one, and the right drawer edits its fields. Dispatched from
// MainWindow.Overworld.cs; the list is EditorSession.Overworld.Sprites.cs.

public partial class MainWindow
{
    private Border owSpritePanel = null!;
    private TextBlock owSpriteTitle = null!, owSpriteNote = null!, owSpriteCodeList = null!;
    private TextBox owSpriteNumber = null!, owSpriteX = null!, owSpriteY = null!, owSpriteZ = null!, owSpriteExtra = null!;
    private StackPanel owSpriteFields = null!;

    /// <summary>The picked sprite's index in the list, or null.</summary>
    private int? owSprite;

    /// <summary>The number a right-click places — the last one typed or picked.</summary>
    private int owSpriteArmed = 1;

    /// <summary>Whether the press the map is answering was the right button. The map reports a
    /// pick without the button, so the press is looked at on its way down.</summary>
    private bool owRightPress;

    /// <summary>A left drag under way: where in the sprite the pointer took hold, and whether it
    /// has moved yet — a click without a move saves nothing.</summary>
    private (int Dc, int Dr, bool Moved)? owSpriteDrag;

    private void WireOwSprites()
    {
        owSpritePanel = this.GetControl<Border>("OwSpritePanel");
        owSpriteTitle = this.GetControl<TextBlock>("OwSpriteTitle");
        owSpriteNote = this.GetControl<TextBlock>("OwSpriteNote");
        owSpriteFields = this.GetControl<StackPanel>("OwSpriteFields");
        owSpriteNumber = this.GetControl<TextBox>("OwSpriteNumber");
        owSpriteX = this.GetControl<TextBox>("OwSpriteX");
        owSpriteY = this.GetControl<TextBox>("OwSpriteY");
        owSpriteZ = this.GetControl<TextBox>("OwSpriteZ");
        owSpriteExtra = this.GetControl<TextBox>("OwSpriteExtra");
        foreach (var box in new[] { owSpriteNumber, owSpriteX, owSpriteY, owSpriteZ, owSpriteExtra })
        {
            box.LostFocus += (_, _) => OwSpriteFieldsCommitted();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OwSpriteFieldsCommitted(); e.Handled = true; } };
        }
        this.GetControl<Button>("OwSpriteDelete").Click += (_, _) => OwDeleteSprite();
        owSpriteCodeList = this.GetControl<TextBlock>("OwSpriteCodeList");
        this.GetControl<Button>("OwSpriteCodeOpen").Click += async (_, _) =>
        {
            await new CustomSpritesWindow(session, PixiImport.Overworld).ShowDialog(this);
            AfterOwSprites();
        };

        owView.AddHandler(PointerPressedEvent, (_, e) => owRightPress = e.GetCurrentPoint(owView).Properties.IsRightButtonPressed,
                          RoutingStrategies.Tunnel);
        owView.PointerMoved += (_, e) => OwSpriteDragMoved(e);
        owView.PointerReleased += (_, _) => OwSpriteDragEnded();
    }

    /// <summary>A press on the map on the Sprites tab: right places a sprite of the armed number,
    /// left on a sprite picks it and takes hold of it, left on bare map lets go.</summary>
    private async void OwSpritePressed((int Col, int Row) c)
    {
        if (owRightPress)
        {
            var (index, why) = session.AddOwSprite(c.Col, c.Row, owSpriteArmed);
            if (why is not null) { await ConfirmWindow.Notice("Place sprite", why).ShowDialog(this); return; }
            owSprite = index;
            AfterOwSprites();
            return;
        }
        int at = session.OwSpriteAt(c.Col, c.Row);
        owSprite = at < 0 ? null : at;
        if (at >= 0)
        {
            var (col, row) = EditorSession.OwSpriteCell(session.OwSprites[at]);
            owSpriteDrag = (c.Col - col, c.Row - row, false);
        }
        RefreshOwSpritePanel();
        owView.InvalidateVisual();
    }

    private void OwSpriteDragMoved(PointerEventArgs e)
    {
        if (owSpriteDrag is not { } d || owSprite is not { } i || owView.Hover is not { } h) return;
        if (!e.GetCurrentPoint(owView).Properties.IsLeftButtonPressed) { OwSpriteDragEnded(); return; }
        var (col, row) = EditorSession.OwSpriteCell(session.OwSprites[i]);
        if ((h.Col - d.Dc, h.Row - d.Dr) == (col, row)) return;
        if (!session.MoveOwSprite(i, h.Col - d.Dc, h.Row - d.Dr, save: false)) return;   // off its own map: stays put
        owSpriteDrag = d with { Moved = true };
        owView.InvalidateVisual();
        RefreshOwSpritePanel();
    }

    /// <summary>The drag let go: one save for the whole move.</summary>
    private void OwSpriteDragEnded()
    {
        if (owSpriteDrag is not { } d) return;
        owSpriteDrag = null;
        if (!d.Moved) return;
        session.SaveOwSprites();
        UpdateTitle();
    }

    private void OwDeleteSprite()
    {
        if (owSprite is not { } i) return;
        session.RemoveOwSprite(i);
        owSprite = null;
        AfterOwSprites();
    }

    /// <summary>The drawer's fields, typed: the number arms the next placement too; with a sprite
    /// picked they replace its fields. Anything that does not read as hex puts the old value back.</summary>
    private void OwSpriteFieldsCommitted()
    {
        static int? Hex(string? s, int max)
            => int.TryParse(s?.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int v) && v >= 0 && v <= max ? v : null;
        if (Hex(owSpriteNumber.Text, 0x7F) is { } n and > 0) owSpriteArmed = n;
        if (owSprite is { } i && i < session.OwSprites.Count)
        {
            var s = session.OwSprites[i];
            byte[] extra;
            try { extra = Convert.FromHexString(new string((owSpriteExtra.Text ?? "").Where(Uri.IsHexDigit).ToArray())); }
            catch (FormatException) { extra = s.Extra; }                // an odd digit count
            var edited = s with
            {
                Number = owSpriteArmed,
                X = Hex(owSpriteX.Text, 0x3F) ?? s.X,
                Y = Hex(owSpriteY.Text, 0x3F) ?? s.Y,
                Z = Hex(owSpriteZ.Text, 0x1F) ?? s.Z,
                Extra = extra,
            };
            if (edited.Number != s.Number || (edited.X, edited.Y, edited.Z) != (s.X, s.Y, s.Z) || !edited.Extra.AsSpan().SequenceEqual(s.Extra))
            {
                session.SetOwSprite(i, edited);
                AfterOwSprites();
                return;
            }
        }
        RefreshOwSpritePanel();
    }

    /// <summary>The list changed: the map, the drawer and the title's dirty mark follow.</summary>
    private void AfterOwSprites()
    {
        RefreshOwSpritePanel();
        owView.InvalidateVisual();
        UpdateTitle();
    }

    /// <summary>The drawer: shown on the Sprites tab, about the picked sprite or, with none, the
    /// number a right-click places.</summary>
    private void RefreshOwSpritePanel()
    {
        owSpritePanel.IsVisible = OwModeNow == OwMode.Sprites && !OwColorsOnly && session.HasRom;
        if (!owSpritePanel.IsVisible) return;
        if (owSprite is { } i && i >= session.OwSprites.Count) owSprite = null;     // a reload took it
        var s = owSprite is { } k ? session.OwSprites[k] : null;
        owSpriteFields.IsVisible = s is not null;
        owSpriteTitle.Text = s is null ? "New sprites" : $"Sprite {s.Number:X2} — {EditorSession.OwSubmapNames[s.Submap]}";
        owSpriteNumber.Text = $"{s?.Number ?? owSpriteArmed:X2}";
        if (s is not null)
        {
            owSpriteX.Text = $"{s.X:X2}";
            owSpriteY.Text = $"{s.Y:X2}";
            owSpriteZ.Text = $"{s.Z:X2}";
            owSpriteExtra.Text = string.Join(' ', s.Extra.Select(b => $"{b:X2}"));
        }
        var code = session.OwSpriteCode;
        owSpriteNote.Text = $"{session.OwSprites.Count} custom sprites on the overworld, up to {Overworld.MaxSpritesPerSubmap} a submap. "
                          + (code.Count == 0 ? "They run in game once their code is in: add it in Sprite code… (kind Overworld) and give it a slot."
                                             : "The build inserts this code with BOWSIE's overworld sprite system:");
        owSpriteCodeList.Text = string.Join('\n', code.Select(e => $"{e.Key:X2}  {e.Value}"));
        owSpriteCodeList.IsVisible = code.Count > 0;
        RefreshOwNote();                            // it names the armed number
    }

    /// <summary>Every custom sprite as a box with its number, the picked one ringed.</summary>
    private void DrawOwSprites(DrawingContext ctx, double step, double size)
    {
        if (OwModeNow != OwMode.Sprites) return;
        for (int i = 0; i < session.OwSprites.Count; i++)
        {
            var s = session.OwSprites[i];
            var (col, row) = EditorSession.OwSpriteCell(s);
            var r = new Rect(col * step, row * step, 2 * step, 2 * step);
            Overlay.Sprite(ctx, r);
            Overlay.Badge(ctx, $"{s.Number:X2}", size, r.TopLeft + new Point(1, 1));
            if (i == owSprite) Overlay.Selection(ctx, r);
        }
    }
}
