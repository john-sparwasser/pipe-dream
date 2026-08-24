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

    /// <summary>True for a custom ExGFX file the project owns (renameable); false for one of the
    /// ROM's own base files, fork or not.</summary>
    public required bool Custom { get; init; }

    public string? Name { get; init; }
    public required string Description { get; init; }
    public (uint[] Px, int W, int H) Sheet { get; init; }

    /// <summary>What a picker calls the file. ExGFX ids are primary keys, not labels: a named
    /// custom file goes by its name, and only stock files and unnamed imports show the id.</summary>
    public string Label => Custom
        ? Name is { Length: > 0 } n ? n : $"GFX{Id:X3}  (unnamed)"
        : $"GFX{Id:X3}";
}
