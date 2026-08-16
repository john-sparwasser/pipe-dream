namespace PipeDream;

/// <summary>Everything needed to compose one frame of the level canvas. The overlay
/// delegate draws sprites for a given animation phase (img, W, H, phase).</summary>
public readonly record struct CanvasScene(
    uint[][][] TileCaches, uint Backdrop, Map16Grid Grid,
    ushort[]? BgImage, uint[][][]? BgCaches, Map16Grid? Layer2,
    int VisibleRows, Action<uint[], int, int, int>? DrawOverlay);
