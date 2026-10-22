namespace PipeDream.Music;

/// <summary>A problem in a song's text, with where it is (1-based). Compilation stops at the first
/// error of a song, as AddmusicK's does, but a parse reports everything it can.</summary>
public sealed record MmlDiagnostic(int Line, int Column, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

/// <summary>How long a note, rest or tie is written: <c>4</c> (a quarter), <c>4.</c> (dotted),
/// <c>=24</c> (ticks), or nothing (the channel's default). Resolved to ticks by the compiler, which
/// knows the default and whether a triplet is open.</summary>
public readonly record struct MmlLength(int? Denominator, int Dots, int? Ticks)
{
    public static readonly MmlLength None = new(null, 0, null);
    public bool IsDefault => Denominator is null && Ticks is null;
}

/// <summary>One thing in a channel's text. Positions are where it starts.</summary>
public abstract record Cmd(int Line, int Column);

public sealed record NoteCmd(int Line, int Column, int Letter, int Accidental, MmlLength Length) : Cmd(Line, Column);
public sealed record RestCmd(int Line, int Column, MmlLength Length) : Cmd(Line, Column);
public sealed record TieCmd(int Line, int Column, MmlLength Length) : Cmd(Line, Column);
public sealed record OctaveCmd(int Line, int Column, int Octave) : Cmd(Line, Column);
public sealed record OctaveShiftCmd(int Line, int Column, int By) : Cmd(Line, Column);
public sealed record DefaultLengthCmd(int Line, int Column, int Denominator) : Cmd(Line, Column);
public sealed record TuneCmd(int Line, int Column, int Semitones) : Cmd(Line, Column);
public sealed record InstrumentCmd(int Line, int Column, int Number) : Cmd(Line, Column);
public sealed record VolumeCmd(int Line, int Column, int Value) : Cmd(Line, Column);
public sealed record GlobalVolumeCmd(int Line, int Column, int Value) : Cmd(Line, Column);
public sealed record PanCmd(int Line, int Column, int Value, int SurroundLeft, int SurroundRight) : Cmd(Line, Column);
public sealed record QuantCmd(int Line, int Column, int Value) : Cmd(Line, Column);
public sealed record TempoCmd(int Line, int Column, int Value) : Cmd(Line, Column);
public sealed record VibratoCmd(int Line, int Column, int Delay, int Rate, int Extent) : Cmd(Line, Column);
public sealed record NoiseCmd(int Line, int Column, int Value) : Cmd(Line, Column);
public sealed record PitchSlideCmd(int Line, int Column) : Cmd(Line, Column);
public sealed record NoLoopCmd(int Line, int Column) : Cmd(Line, Column);
public sealed record IntroCmd(int Line, int Column) : Cmd(Line, Column);
/// <summary>The channel was named again (<c>#0</c> twice): its text carries on, but AMK forgets the length it last wrote.</summary>
public sealed record ResumeCmd(int Line, int Column) : Cmd(Line, Column);
public sealed record SampleLoadCmd(int Line, int Column, string Sample, int Tuning) : Cmd(Line, Column);
public sealed record HexCmd(int Line, int Column, byte[] Bytes) : Cmd(Line, Column);

/// <summary><c>[ body ]n</c>: compiled as a subroutine called n times. A missing count is 1 as AMK reads it.</summary>
public sealed record LoopCmd(int Line, int Column, List<Cmd> Body, int Count) : Cmd(Line, Column);
/// <summary><c>(id)[ body ]n</c>: a loop that later <c>(id)k</c> calls can reuse.</summary>
public sealed record LabelLoopCmd(int Line, int Column, int Id, List<Cmd> Body, int Count) : Cmd(Line, Column);
public sealed record LabelCallCmd(int Line, int Column, int Id, int Count) : Cmd(Line, Column);
/// <summary><c>*n</c>: call the most recent loop again.</summary>
public sealed record RecallCmd(int Line, int Column, int Count) : Cmd(Line, Column);
/// <summary><c>[[ body ]]n</c>: an inline loop; loops may sit inside it.</summary>
public sealed record SuperLoopCmd(int Line, int Column, List<Cmd> Body, int Count) : Cmd(Line, Column);
/// <summary><c>{ body }</c>: every length inside is two thirds as long.</summary>
public sealed record TripletCmd(int Line, int Column, List<Cmd> Body) : Cmd(Line, Column);
/// <summary><c>(!id)[ hex commands ]</c>.</summary>
public sealed record RemoteDefCmd(int Line, int Column, int Id, List<Cmd> Body) : Cmd(Line, Column);
/// <summary><c>(!id, type, arg)</c>: <paramref name="Arg"/> is a hex value or a note length in ticks, null when the type takes none.</summary>
public sealed record RemoteCallCmd(int Line, int Column, int Id, int Type, MmlLength? Arg, int? ArgByte) : Cmd(Line, Column);

/// <summary>An <c>#instruments</c> entry: a sample (by name), an existing instrument (<c>@n</c>) or noise
/// (<c>nXX</c>), then the five bytes: ADSR1, ADSR2, GAIN, tuning multiplier, tuning fraction.</summary>
public sealed record InstrumentDecl(string? Sample, int? FromInstrument, int? Noise, byte[] Bytes);

public sealed class MmlSong
{
    /// <summary>The parser version asked for: "#amk 2" → 2; 0 when the song names no target, which AMK refuses.</summary>
    public int AmkVersion;
    public bool Am4, Amm;
    public bool Louder;

    /// <summary>The groups named in <c>#samples { #group ... }</c> and the samples listed beside them, in order.</summary>
    public List<string> SampleGroups { get; } = new();
    public List<string> Samples { get; } = new();
    public bool SamplesDeclared;
    public List<InstrumentDecl> Instruments { get; } = new();
    public string? Path;
    public int? Pad;
    public bool HalveTempo;
    public int? DivideTempo;
    public bool TempoImmunity, NoLoop, SmwVTable;
    /// <summary>The #option lines in the order written, which is the order their bytes are inserted.</summary>
    public List<string> OptionOrder { get; } = new();
    public Dictionary<string, string> Spc { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Length;

    /// <summary>Each channel's commands, <c>#0</c> to <c>#7</c>; null for one the song never names.</summary>
    public List<Cmd>?[] Channels { get; } = new List<Cmd>?[8];

    /// <summary>Remote code definitions, which come before any channel's text.</summary>
    public List<RemoteDefCmd> RemoteDefs { get; } = new();

    public List<MmlDiagnostic> Diagnostics { get; } = new();
    public bool HasErrors => Diagnostics.Count > 0;
}
