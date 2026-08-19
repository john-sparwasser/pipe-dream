using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// A <see cref="CatalogItem"/> dressed for the drawer list: the same facts, plus the thumbnail
/// turned into a bitmap the templates can bind to.
///
/// The service layer deals in RGBA buffers because it must run without a window. Turning one
/// into an Avalonia image is this layer's job, and it is the only thing this class adds.
/// </summary>
public sealed class CatalogRow(CatalogItem item)
{
    public int Number => item.Number;
    public string Label => item.Label;
    public bool Loaded => item.Loaded;
    public int W => item.W;
    public int H => item.H;

    public Bitmap? Thumb { get; } = item.Thumb is { } px && item.Size > 0
        ? LevelBitmap.FromPixels(px, item.Size, item.Size) : null;

    public static List<CatalogRow> Wrap(IReadOnlyList<CatalogItem> items)
        => [.. items.Select(i => new CatalogRow(i))];
}
