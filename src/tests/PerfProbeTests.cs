using System.Diagnostics;
using PipeDream.Services;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

/// <summary>
/// Budget pins for the interactive hot paths, in the CanvasCostTests spirit: a sprite drag
/// step and a sprite edit must stay incremental. Before these paths existed each one paid a
/// full scene rebuild (~75ms/step here), which made dragging a slideshow. Budgets are several
/// times the measured cost (1.7ms / 5ms) to absorb slow CI machines.
/// </summary>
public class PerfProbeTests(ITestOutputHelper log)
{
    private static string? Prepped => PreppedRom.Path;

    [Fact]
    public void time_the_hot_paths()
    {
        if (Prepped is not { } path) { log.WriteLine("SKIP: no ROM"); return; }
        var session = new EditorSession();
        session.OpenRom(path);
        session.ShowLevel(0x105);
        var edit = session.Edit!;

        double Time(string what, Action a, int reps = 5)
        {
            a();                                             // warm
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++) a();
            double ms = sw.Elapsed.TotalMilliseconds / reps;
            log.WriteLine($"{what}: {ms:F1} ms");
            return ms;
        }

        edit.Selection.Clear();
        edit.Selection.Add(0);
        Time("object MoveSelected+Reconcile", () => edit.MoveSelected(1, 0, coalesce: true));
        edit.MoveSelected(0, 1);                             // one real entry to undo
        Time("object Undo/Redo pair", () => { edit.Undo(); edit.Redo(); });

        Time("palette undo/redo pair", () =>
        {
            session.BeginPaletteStroke();
            session.SetPaletteColor(4, 0x1234);
            session.EndPaletteStroke();
            session.PaletteUndo();
        });

        double refresh = Time("RefreshSprites (incremental)", () => session.RefreshSprites(), reps: 3);
        Assert.True(refresh < 40, $"RefreshSprites took {refresh:F1}ms — a sprite edit is recomposing the scene again");
        if (session.Map16 is { } m16)
            Time("map16 stamp+undo (targeted recompose)", () =>
            {
                m16.StampQuad(0x100, 0, 0x2345);
                m16.EndStroke();
                m16.Undo();
                session.RecomposeAfterMap16();
            }, reps: 3);
        if (session.Sprites is { } sp && sp.Sprites.Sprites.Count > 0)
        {
            sp.Selection.Clear();
            sp.Selection.Add(0);
            double step = Time("MoveSprites (drag step)", () =>
            {
                sp.MoveSelected(1, 0, coalesce: true);
                session.MoveSprites(1, 0);
            });
            Assert.True(step < 20, $"a sprite drag step took {step:F1}ms — dragging will feel sluggish");
        }

        Time("Recolour-class full repalette (Map16 undo fallback)", () =>
        {
            session.BeginPaletteStroke();
            session.SetPaletteColor(4, 0x7FFF);
            session.EndPaletteStroke();
            session.PaletteUndo();
        }, reps: 3);
    }
}
