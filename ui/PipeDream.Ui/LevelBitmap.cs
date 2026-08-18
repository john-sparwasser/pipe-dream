using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PipeDream.Ui;

/// <summary>
/// The Avalonia counterpart of <c>LevelCanvas</c>'s upload step: one WriteableBitmap per
/// animation phase, fed by the SAME CPU composition the ImGui editor uses.
///
/// This is the whole bet of the migration in one file. Composition already produces a
/// <c>uint[]</c> of RGBA pixels and knows nothing about any UI framework, so porting the
/// canvas is a matter of swapping a Foster <c>Texture</c> for a WriteableBitmap and leaving
/// the expensive part alone. Everything else — the object engine, Map16 composition, the
/// dirty-cell repaint — carries over untouched.
///
/// Pixel format: composition packs 0xAABBGGRR (little-endian RGBA), which matches
/// <see cref="PixelFormat.Rgba8888"/>; no per-pixel swizzle is needed on the way in.
/// </summary>
public sealed class LevelBitmap : IDisposable
{
    private readonly WriteableBitmap?[] bmps = new WriteableBitmap?[4];
    private readonly bool[] stale = new bool[4];
    private uint[]?[] imgs = new uint[4][];

    public int PxW { get; private set; }
    public int PxH { get; private set; }
    public bool HasImages => imgs[0] is not null;

    /// <summary>Adopt freshly composed phase images; only the visible phase is pushed to a
    /// bitmap now, the rest when the animation reaches them.</summary>
    public void SetImages(uint[]?[] phaseImages, int w, int h, int visiblePhase)
    {
        imgs = phaseImages;
        PxW = w; PxH = h;
        for (int p = 0; p < 4; p++) stale[p] = true;
        Refresh(visiblePhase & 3);
    }

    public WriteableBitmap? For(int phase)
    {
        Refresh(phase & 3);
        return bmps[phase & 3] ?? bmps[0];
    }

    /// <summary>Copy a phase's pixels into its bitmap if they changed since the last push.
    /// Allocates a new bitmap only when the size changes — a resize per repaint would make
    /// the canvas allocate megabytes per frame.</summary>
    public void Refresh(int p)
    {
        if (!stale[p] || imgs[p] is not { } img || PxW <= 0 || PxH <= 0) return;
        var bmp = bmps[p];
        if (bmp is null || bmp.PixelSize.Width != PxW || bmp.PixelSize.Height != PxH)
        {
            bmp?.Dispose();
            bmp = bmps[p] = new WriteableBitmap(new PixelSize(PxW, PxH), new Vector(96, 96),
                                                PixelFormat.Rgba8888, AlphaFormat.Premul);
        }
        using (var fb = bmp.Lock())
        {
            int rowBytes = PxW * 4;
            unsafe
            {
                fixed (uint* src = img)
                {
                    var dst = (byte*)fb.Address;
                    // The framebuffer stride is not guaranteed to be width*4, so only take
                    // the single-copy path when it is; otherwise copy row by row. Either way
                    // nothing is allocated here — this runs on every repaint.
                    if (fb.RowBytes == rowBytes)
                        Buffer.MemoryCopy(src, dst, (long)rowBytes * PxH, (long)rowBytes * PxH);
                    else
                        for (int y = 0; y < PxH; y++)
                            Buffer.MemoryCopy(src + (long)y * PxW, dst + (long)y * fb.RowBytes, rowBytes, rowBytes);
                }
            }
        }
        stale[p] = false;
    }

    public void Dispose()
    {
        for (int p = 0; p < 4; p++) { bmps[p]?.Dispose(); bmps[p] = null; imgs[p] = null; }
    }

    /// <summary>One-shot: composed RGBA pixels to a bitmap, for the static sheets (Map16
    /// picker, GFX sheets) that do not animate per phase.</summary>
    public static WriteableBitmap FromPixels(uint[] px, int w, int h)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                      PixelFormat.Rgba8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        int rowBytes = w * 4;
        unsafe
        {
            fixed (uint* src = px)
            {
                var dst = (byte*)fb.Address;
                if (fb.RowBytes == rowBytes)
                    Buffer.MemoryCopy(src, dst, (long)rowBytes * h, (long)rowBytes * h);
                else
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + (long)y * w, dst + (long)y * fb.RowBytes, rowBytes, rowBytes);
            }
        }
        return bmp;
    }
}
