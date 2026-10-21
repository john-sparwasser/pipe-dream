namespace PipeDream;

/// <summary>
/// The project's music (reference/AMK_PORT.md, phase 1): AddmusicK's whole input as data in the .pdp,
/// so sharing the file shares the songs. Names and numbers are AMK's own: a song is a path under its
/// <c>music/</c> folder, a sample a path under <c>samples/</c>, an effect a file in <c>1DF9/</c> or
/// <c>1DFC/</c>, so an AMK folder reads in and writes out one to one. Nothing here compiles anything.
/// </summary>
public sealed class MusicState
{
    /// <summary>The songs: path under music/ ("click.mml", "Dir/Song.txt") → the MML as written.
    /// A song in no <see cref="SongList"/> entry stays here and is not inserted.</summary>
    public Dictionary<string, string> Songs { get; set; } = new();

    /// <summary>Which song plays under which number: Addmusic_list.txt's Globals and Locals.
    /// Global songs never transfer and cost ARAM permanently; local ones upload on a song change.</summary>
    public List<SongSlot> SongList { get; set; } = new();

    /// <summary>BRR samples and sample banks: path under samples/ ("default/00 SMW @0.brr", "x.bnk") → bytes.</summary>
    public Dictionary<string, byte[]> Samples { get; set; } = new();

    /// <summary>Sample groups (Addmusic_sample groups.txt), in file order: <c>#default</c> is every stock
    /// sample, <c>#optimized</c> only what sound effects and global songs need.</summary>
    public List<SampleGroup> Groups { get; set; } = new();

    /// <summary>The sound effects on SMW's two ports (Addmusic_sound effects.txt): $1DF9 plays on SPC
    /// channel 6, $1DFC on 7. In file order.</summary>
    public List<SfxSlot> Sfx1DF9 { get; set; } = new();
    public List<SfxSlot> Sfx1DFC { get; set; } = new();

    /// <summary>The effects' sources: "1DF9/01 Hit head.txt" → the text.</summary>
    public Dictionary<string, string> SfxSources { get; set; } = new();

    /// <summary>AMK's command-line options, one per entry, as Addmusic_options.txt holds them ("-v").</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>Nothing in it: a project that has not used music.</summary>
    public bool IsEmpty => Songs.Count == 0 && SongList.Count == 0 && Samples.Count == 0 && Groups.Count == 0
                           && Sfx1DF9.Count == 0 && Sfx1DFC.Count == 0 && SfxSources.Count == 0 && Options.Count == 0;

    public sealed class SongSlot
    {
        /// <summary>The number Lunar Magic and the game use for it, 01-FF.</summary>
        public int Number { get; set; }
        /// <summary>A key of <see cref="MusicState.Songs"/>.</summary>
        public string Path { get; set; } = "";
        public bool Global { get; set; }
    }

    public sealed class SampleGroup
    {
        public string Name { get; set; } = "";
        public List<SampleEntry> Samples { get; set; } = new();
    }

    public sealed class SampleEntry
    {
        /// <summary>A key of <see cref="MusicState.Samples"/>.</summary>
        public string Path { get; set; } = "";
        /// <summary>The trailing "!": always in ARAM, because a sound effect or a global song uses it.</summary>
        public bool Important { get; set; }
    }

    public sealed class SfxSlot
    {
        /// <summary>The value written to the port, hex 01-FF.</summary>
        public int Slot { get; set; }
        /// <summary>"" a normal effect, "*" a pointer to the effect the file names (saves ARAM),
        /// "?" no terminating $00 is added (for effects that repeat with $FF or $FE).</summary>
        public string Flag { get; set; } = "";
        /// <summary>The file name inside the port's folder; a key of <see cref="MusicState.SfxSources"/> with "1DF9/" or "1DFC/" before it.</summary>
        public string File { get; set; } = "";
    }
}
