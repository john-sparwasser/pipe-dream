using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace PipeDream.Ui;

/// <summary>
/// Red squiggles under what <see cref="AsmLint"/> finds in a <see cref="TextEditor"/>, re-checked
/// on every change (the check is a line scan, cheap enough to run per keystroke), with the message
/// as a tooltip over the squiggle.
/// </summary>
internal sealed class LintSquiggles : IBackgroundRenderer
{
    private static readonly IPen Pen = new Pen(new SolidColorBrush(Color.FromRgb(0xF1, 0x4C, 0x4C)), 1);
    private readonly TextEditor editor;
    public IReadOnlyList<AsmIssue> Issues { get; private set; } = [];
    /// <summary>Raised after each re-check, for a problem count.</summary>
    public event Action? Checked;

    public LintSquiggles(TextEditor editor)
    {
        this.editor = editor;
        editor.TextArea.TextView.BackgroundRenderers.Add(this);
        editor.TextChanged += (_, _) => Recheck();
        editor.TextArea.TextView.PointerMoved += OnPointerMoved;
        Recheck();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    private void Recheck()
    {
        Issues = AsmLint.Check(editor.Text ?? "");
        editor.TextArea.TextView.InvalidateLayer(Layer);
        Checked?.Invoke();
    }

    public void Draw(TextView view, DrawingContext dc)
    {
        if (!view.VisualLinesValid) return;
        int end = view.Document.TextLength;
        foreach (var i in Issues)
        {
            if (i.Offset + i.Length > end) continue;
            var seg = new TextSegment { StartOffset = i.Offset, Length = Math.Max(1, i.Length) };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(view, seg))
            {
                // A zigzag along the bottom of the run: 2px up and down every 2px across.
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    double y = r.Bottom - 1;
                    c.BeginFigure(new Point(r.Left, y), false);
                    bool up = true;
                    for (double x = r.Left + 2; x <= r.Right + 0.5; x += 2, up = !up) c.LineTo(new Point(x, up ? y - 2 : y));
                    c.EndFigure(false);
                }
                dc.DrawGeometry(null, Pen, g);
            }
        }
    }

    /// <summary>The message of the squiggle under the pointer, as the view's tooltip.</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var view = editor.TextArea.TextView;
        string? tip = null;
        if (view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset) is { } pos)
        {
            int at = view.Document.GetOffset(pos.Location);
            tip = Issues.FirstOrDefault(i => at >= i.Offset && at < i.Offset + Math.Max(1, i.Length))?.Message;
        }
        if (tip == ToolTip.GetTip(view) as string) return;
        ToolTip.SetTip(view, tip);
        ToolTip.SetIsOpen(view, tip is not null);
    }
}
