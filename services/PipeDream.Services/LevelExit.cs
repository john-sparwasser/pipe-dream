namespace PipeDream.Services;

/// <summary>
/// One screen exit: which screen of the level leads where.
///
/// Exits are objects in the layer-1 stream that draw no tiles, so they are invisible on the
/// canvas and a list is the only way to reach them at all.
///
/// Mutable on purpose — the exits editor stages a whole table and commits it in one go, so a
/// session of retyping destinations costs one undo step rather than one per keystroke.
/// </summary>
public sealed class LevelExit
{
    /// <summary>The screen this exit governs.</summary>
    public int Screen { get; set; }

    /// <summary>A level number for a plain exit; an index into the secondary entrance table for
    /// a secondary one.</summary>
    public int Destination { get; set; }

    public bool Water { get; set; }
    public bool Secondary { get; set; }

    /// <summary>Lunar Magic's secondary-exit form: extended object 0x02 carrying a two-byte exit
    /// word, which packs its own flags into the high byte — so <see cref="Water"/> and
    /// <see cref="Secondary"/> do not apply to it.</summary>
    public bool LmForm { get; set; }

    /// <summary>The object this row came from, kept so an untouched exit keeps its original
    /// position in the stream instead of being rewritten onto the screen it governs.</summary>
    internal LevelObject? Source { get; set; }

    public bool IsNew => Source is null;

    public string Kind => LmForm ? "LM word" : "vanilla";
}
