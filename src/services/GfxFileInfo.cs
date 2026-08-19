namespace PipeDream.Services;

/// <summary>
/// One GFX file as a picker sees it: what it is, what it holds, and what it looks like.
///
/// The thumbnail is raw RGBA for the same reason the catalogs' is — the layer that draws decides
/// what to wrap it in.
/// </summary>
public sealed class GfxFileInfo
{
    public required int Id { get; init; }

    /// <summary>True for a project import (renameable, and it shadows any stock file of the same
    /// id); false for a file the ROM itself resolves.</summary>
    public required bool Imported { get; init; }

    public string? Name { get; init; }
    public required string Description { get; init; }
    public (uint[] Px, int W, int H) Sheet { get; init; }

    public string Label => $"GFX{Id:X3}" + (Imported ? "  custom" : "  stock");
}
