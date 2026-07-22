using System.Runtime.InteropServices;
using Foster.Framework;

namespace PipeDream;

/// <summary>Everything needed to compose one frame of the level canvas. The overlay
/// delegate draws sprites for a given animation phase (img, W, H, phase).</summary>
public readonly record struct CanvasScene(
    uint[][][] TileCaches, uint Backdrop, Map16Grid Grid,
    ushort[]? BgImage, uint[][][]? BgCaches, Map16Grid? Layer2,
    int VisibleRows, Action<uint[], int, int, int>? DrawOverlay);

/// <summary>
/// Incremental level compositor: holds one composed image + texture per animation phase
/// (CONTRACT §12), composes the Map16 grid (+ layer 2 / BG image + sprite overlay) into
/// them, and repaints edits by recomposing only dirty cells. The non-visible phases upload
/// lazily when the animation reaches them, so a repaint costs one texture upload, not four.
/// </summary>
public sealed class LevelCanvas : IDisposable
{
    private readonly GraphicsDevice gd;
    private readonly Texture?[] texs = new Texture?[4];
    private readonly uint[]?[] imgs = new uint[4][];
    private readonly bool[] stale = new bool[4];
    private readonly HashSet<(int x, int y)> dirty = new();

    public int PxW { get; private set; }
    public int PxH { get; private set; }
    public bool HasImages => imgs[0] is not null;
    public int DirtyCount => dirty.Count;

    public LevelCanvas(GraphicsDevice gd) => this.gd = gd;

    public void MarkDirty(int x, int y) => dirty.Add((x, y));
    public Texture? TexFor(int phase) => texs[phase] ?? texs[0];

    /// <summary>Full compose of all four phases (creates/reuses textures).</summary>
    public void Rebuild(in CanvasScene s)
    {
        dirty.Clear();
        try
        {
            for (int p = 0; p < 4; p++)
            {
                var (img, W, H) = Map16.ComposeLevel(s.TileCaches[p], s.Backdrop, s.Grid,
                                                     s.BgImage, s.BgCaches?[p], s.Layer2, s.VisibleRows);
                s.DrawOverlay?.Invoke(img, W, H, p);
                imgs[p] = img;
                // Reuse the texture when the size matches — recreating 4 large textures per
                // edit is a big part of repaint latency.
                if (texs[p] is { } t && t.Width == W && t.Height == H) t.SetData<uint>(img);
                else { texs[p]?.Dispose(); texs[p] = new Texture(gd, W, H, MemoryMarshal.AsBytes(img.AsSpan())); }
                stale[p] = false;
                PxW = W; PxH = H;
            }
        }
        catch { Drop(); }
    }

    /// <summary>Recompose only the dirty cells into the persistent phase images and re-blit
    /// the overlay; upload the visible phase now, leave the rest stale. Falls back to a full
    /// Rebuild if there are no images yet.</summary>
    public void ApplyDirty(in CanvasScene s, int visiblePhase)
    {
        if (!HasImages) { Rebuild(s); return; }
        int W = PxW, H = PxH;
        for (int p = 0; p < 4; p++)
        {
            var img = imgs[p]!;
            foreach (var (cx, cy) in dirty) ComposeCellInto(img, W, H, s, p, cx, cy);
            s.DrawOverlay?.Invoke(img, W, H, p);
            stale[p] = true;
        }
        dirty.Clear();
        RefreshPhase(visiblePhase);
    }

    /// <summary>Upload a phase's image if it was recomposed since its last upload.</summary>
    public void RefreshPhase(int p)
    {
        if (stale[p] && texs[p] is { } t && imgs[p] is { } img) { t.SetData<uint>(img); stale[p] = false; }
    }

    public void Drop()
    {
        for (int p = 0; p < 4; p++) { texs[p]?.Dispose(); texs[p] = null; imgs[p] = null; }
    }

    public void Dispose() => Drop();

    // One cell of Map16.ComposeLevel's layering: backdrop → BG image (or layer 2) → layer 1.
    private static void ComposeCellInto(uint[] img, int W, int H, in CanvasScene s, int phase, int cx, int cy)
    {
        int px = cx * 16, py = cy * 16;
        if (px < 0 || py < 0 || px + 16 > W || py + 16 > H) return;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++) img[(py + y) * W + (px + x)] = s.Backdrop;
        if (s.BgImage is not null && s.BgCaches is not null)
        {
            int within = cx & 0x1F;                          // 2-screen horizontal repeat
            int idx = s.BgImage[(within / 16) * 0x1B0 + (cy % 27) * 16 + (within & 0x0F)];
            var t = s.BgCaches[phase][idx & 0x1FF];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                { uint c = t[y * 16 + x]; if (c != 0) img[(py + y) * W + (px + x)] = c; }
        }
        else if (s.Layer2 is not null) DrawCellTile(img, W, s.Layer2.Get(cx, cy), s.TileCaches[phase], px, py);
        DrawCellTile(img, W, s.Grid.Get(cx, cy), s.TileCaches[phase], px, py);
    }

    private static void DrawCellTile(uint[] img, int W, int t, uint[][] cache, int px, int py)
    {
        if (t == Map16Grid.Empty) return;
        uint[]? tile = (t & ObjectEngine.Marker) != 0 || t >= cache.Length ? null : cache[t];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                uint c = tile is null ? 0xFFFF00FFu : tile[y * 16 + x];
                if (c != 0) img[(py + y) * W + (px + x)] = c;
            }
    }
}
