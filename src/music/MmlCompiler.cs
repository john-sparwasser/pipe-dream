using System.Text;

namespace PipeDream.Music;

/// <summary>A song compiled to the bytes the engine plays, addressed as if loaded at <see cref="Base"/>.</summary>
/// <param name="InstrumentTable">Where the song's custom instruments (six bytes each: sample, ADSR1, ADSR2, GAIN, tuning) start: after the phrase list.</param>
public sealed record CompiledSong(byte[] Bytes, int Base, IReadOnlyList<MmlDiagnostic> Diagnostics, int InstrumentTable = 0)
{
    public bool Ok => Diagnostics.Count == 0;
}

/// <summary>
/// An <see cref="MmlSong"/> to N-SPC bytes in AddmusicK's layout (reference/AMK_PORT.md, phase 3). The layout and
/// every rule here were read off AddmusicK's output for small songs (docs and black-box probes, never its
/// source): a phase list ending in a jump back, one table of eight channel pointers per phrase, then each
/// channel's bytes ending in $00 with its loop subroutines after it.
/// </summary>
public static class MmlCompiler
{
    private const int Ticks = 192;                     // a whole note

    /// <summary>Per instrument, how far AMK moves a note's byte so the sample plays at pitch.</summary>
    private static int InstrumentOffset(int instrument) => instrument switch { 2 => -5, 9 => 5, 10 => -6, 12 => 5, 15 => -8, _ => 0 };

    /// <summary>Whether a channel has anything that plays: a note, rest or tie of some length. Commands alone are not data.</summary>
    private static bool HasNotes(IEnumerable<Cmd> cmds) => cmds.Any(c => c switch
    {
        NoteCmd { Length.Ticks: 0 } or RestCmd { Length.Ticks: 0 } or TieCmd { Length.Ticks: 0 } => false,
        NoteCmd or RestCmd or TieCmd => true,
        LoopCmd l => HasNotes(l.Body), LabelLoopCmd l => HasNotes(l.Body), SuperLoopCmd l => HasNotes(l.Body), TripletCmd t => HasNotes(t.Body),
        _ => false,
    });

    /// <summary>The sample index SMW's own instrument @n plays (its slot in the stock sample table).</summary>
    private static readonly int[] StockSample = [0, 1, 2, 3, 4, 7, 8, 9, 5, 10, 11, 1, 16, 12, 13, 18, 12, 17, 1, 0, 0, 15, 6, 6, 14, 14, 11, 11, 11, 14];

    /// <summary>AddmusicK's own sample groups: #default and #optimized hold 20 samples, #AMM 12.</summary>
    public static int StockGroupSize(string group) => group.ToLowerInvariant() switch { "default" or "optimized" => 20, "amm" => 12, _ => 0 };

    /// <param name="groupSize">How many samples a sample group holds: the samples a song names come after them in its sample table.</param>
    public static CompiledSong Compile(MmlSong song, int baseAddress, Func<string, int>? groupSize = null)
    {
        groupSize ??= StockGroupSize;
        var problems = new List<MmlDiagnostic>(song.Diagnostics);
        if (problems.Count > 0) return new CompiledSong([], baseAddress, problems);
        if (!song.Channels.Any(c => c is not null && HasNotes(c)))
            return new CompiledSong([], baseAddress, [new MmlDiagnostic(1, 1, "This song doesn't seem to have any data.")]);

        // Where the song's own samples start in its sample table: after the groups' samples.
        int groupBase = song.SampleGroups.Sum(groupSize);

        // A channel whose text compiles to nothing (an octave change and no notes) is not in the song at all.
        int first = -1;
        for (int i = 0; i < 8 && first < 0; i++)
            if (song.Channels[i] is { Count: > 0 } probe
                && (i == 0 || !new ChannelCompiler(song, groupBase, new List<MmlDiagnostic>(), false, i, new(), new LoopState()).Run(probe).IsEmpty)) first = i;
        var channels = new ChannelOut?[8];
        var labels = new Dictionary<int, (int Ch, int Sub, int Line, int Col)>();
        var loopState = new LoopState();
        for (int i = 0; i < 8; i++)
            if (song.Channels[i] is { Count: > 0 } cmds)
            {
                var c = new ChannelCompiler(song, groupBase, problems, i == first, i, labels, loopState).Run(cmds);
                if (!c.IsEmpty || i == first) channels[i] = c;     // channel 0 counts as soon as it has any text
            }
        if (first < 0) return new CompiledSong([], baseAddress, [new MmlDiagnostic(1, 1, "This song doesn't seem to have any data.")]);
        // The song's options are bytes at the start of channel 0, which exists for them when the song has no #0 of its own.
        if (first > 0 && song.OptionOrder.Count > 0)
        {
            var bytes0 = new List<byte>();
            ChannelCompiler.OptionBytes(song, bytes0);
            bytes0.Add(0x00);
            channels[0] = new ChannelOut(0, bytes0, [], [], [], null, false);
        }
        if (problems.Count > 0) return new CompiledSong([], baseAddress, problems);

        // The sample table the song's instruments name.
        var table = new List<byte>();
        foreach (var ins in song.Instruments)
        {
            int index;
            if (ins.Noise is { } nz) index = 0x80 | nz;
            else if (ins.FromInstrument is { } from) index = from is >= 0 and < 30 ? StockSample[from] : 0;
            else
            {
                int pos = song.Samples.IndexOf(ins.Sample!);
                if (pos < 0) return new CompiledSong([], baseAddress, [new MmlDiagnostic(1, 1, $"The sample \"{ins.Sample}\" was not included in this song.")]);
                index = groupBase + pos;
            }
            table.Add((byte)index); table.AddRange(ins.Bytes);
        }

        bool noLoop = song.NoLoop || channels.Any(c => c?.NoLoop == true);
        bool intro = channels.Any(c => c?.IntroAt is not null);

        // Layout: the phase list, a pointer table per phrase, then every channel's bytes in order.
        int header = noLoop ? (intro ? 6 : 4) : intro ? 8 : 6;
        int tables = intro ? 2 : 1;
        int at = baseAddress + header + table.Count + tables * 16;
        var starts = new int?[8];
        for (int i = 0; i < 8; i++)
            if (channels[i] is { } c) { starts[i] = at; at += c.MainLength; }
        // Every subroutine after every channel's own bytes, in the order the text has them, whichever channel (measured).
        var order = Enumerable.Range(0, 8).Where(i => channels[i] is not null)
            .SelectMany(i => channels[i]!.SubAt.Select((p, k) => (p, Ch: i, Sub: k))).OrderBy(x => x.p).ToList();
        var subAddr = new int[8][];
        for (int i = 0; i < 8; i++) subAddr[i] = new int[channels[i]?.Subs.Count ?? 0];
        foreach (var (_, ch, sub) in order) { subAddr[ch][sub] = at; at += channels[ch]!.Subs[sub].Count; }

        var bytes = new List<byte>();
        void W16(int v) { bytes.Add((byte)v); bytes.Add((byte)(v >> 8)); }
        int firstTable = baseAddress + header + table.Count;
        if (noLoop && intro) { W16(firstTable); W16(firstTable + 16); W16(0); }
        else if (noLoop) { W16(firstTable); W16(0); }
        else if (intro) { W16(firstTable); W16(firstTable + 16); W16(0x00FF); W16(baseAddress + 2); }
        else { W16(firstTable); W16(0x00FF); W16(baseAddress); }
        bytes.AddRange(table);
        for (int phrase = 0; phrase < tables; phrase++)
        {
            if (phrase == 1) firstTable += 16;
            for (int i = 0; i < 8; i++)
                W16(channels[i] is { } c && starts[i] is { } s ? s + (phrase == 1 ? c.IntroAt ?? 0 : 0) : 0);
        }
        // Calls (and label calls from other channels) filled in with where every subroutine ended up.
        for (int i = 0; i < 8; i++)
            if (channels[i] is { } c)
            {
                problems.AddRange(c.Resolve(subAddr, labels, out var main));
                bytes.AddRange(main);
            }
        foreach (var (_, ch, sub) in order) bytes.AddRange(channels[ch]!.Subs[sub]);
        if (problems.Count > 0) return new CompiledSong([], baseAddress, problems);
        return new CompiledSong(bytes.ToArray(), baseAddress, problems, baseAddress + header);
    }

    /// <summary>A call to a subroutine still to be filled in: where it is, and the subroutine it names (a loop of this
    /// channel, or a label that may be another channel's).</summary>
    private sealed record Call(bool InSub, int Owner, int Offset, int Sub, int Label, Cmd At);

    /// <summary>What AddmusicK keeps for loop bodies across the whole song: the transposition (h) they use, whether one was set, and
    /// the loop defined last, which <c>*</c> recalls from any channel (measured).</summary>
    private sealed class LoopState { public int Tune; public bool TuneSet; public (int Ch, int Sub) Last = (-1, -1); public int Recalls; }

    /// <summary>One channel's bytes: the main stream, then its subroutines, with the places that call them still to be filled in.</summary>
    private sealed class ChannelOut(int index, List<byte> main, List<List<byte>> subs, List<(int Line, int Col)> subAt, List<Call> calls, int? introAt, bool noLoop)
    {
        public IReadOnlyList<List<byte>> Subs => subs;
        public IReadOnlyList<(int Line, int Col)> SubAt => subAt;
        public int? IntroAt => introAt;
        public bool NoLoop => noLoop;
        /// <summary>Nothing but the terminator: the channel played nothing.</summary>
        public bool IsEmpty => main.Count <= 1 && subs.Count == 0 && main.All(b => b == 0);
        public int MainLength => main.Count;

        public List<MmlDiagnostic> Resolve(int[][] subAddr, Dictionary<int, (int Ch, int Sub, int Line, int Col)> labels, out byte[] mainBytes)
        {
            var errors = new List<MmlDiagnostic>();
            foreach (var c in calls)
            {
                int target;
                if (c.Label >= 0)
                {
                    if (!labels.TryGetValue(c.Label, out var l) || (l.Line, l.Col).CompareTo((c.At.Line, c.At.Column)) > 0)
                    { errors.Add(new MmlDiagnostic(c.At.Line, c.At.Column, "Label not yet defined.")); continue; }
                    target = subAddr[l.Ch][l.Sub];
                }
                else target = subAddr[index][c.Sub];
                var list = c.InSub ? subs[c.Owner] : main;
                list[c.Offset] = (byte)target;
                list[c.Offset + 1] = (byte)(target >> 8);
            }
            mainBytes = main.ToArray();
            return errors;
        }
    }

    /// <summary>One run of bytes being written (the main stream or a subroutine) and what the engine will have been told by then.</summary>
    private sealed class Buf(int sub)
    {
        public readonly List<byte> Bytes = new();
        public readonly int Sub = sub;                // -1 for the main stream
        public int? LastDuration, LastQv;
        public bool Slide;                // a pending "&", carried to the next note written to THIS stream

        public void Forget() { LastDuration = null; LastQv = null; }
    }

    private sealed class ChannelCompiler(MmlSong song, int groupBase, List<MmlDiagnostic> problems, bool isFirst, int channelIndex, Dictionary<int, (int Ch, int Sub, int Line, int Col)> labels, LoopState loopState)
    {
        // What the channel is set to as the text is read.
        private int octave = 4, defaultLength = 8, tune, instrument = -1, qv = 0x7F;
        private bool tuneSet;                // h has been used here, so it stands in for the instrument's own offset
        private int pendingPercussion = -1;

        private readonly List<List<byte>> subs = new();
        private readonly List<(int Line, int Col)> subAt = new();     // where each was written: subroutines are laid out in that order
        private readonly List<Call> calls = new();
        private int? introAt;
        private bool noLoop;
        private Buf main = new(-1);

        private void Error(Cmd c, string message) { if (problems.Count < 200) problems.Add(new MmlDiagnostic(c.Line, c.Column, message)); }

        /// <summary>What the song's #option lines put at the start of channel 0, in the order they were written.</summary>
        public static void OptionBytes(MmlSong song, List<byte> into)
        {
            foreach (string option in song.OptionOrder)
            {
                if (option == "tempoimmunity") into.AddRange([0xF4, 0x07]);
                else if (option == "smwvtable") into.AddRange([0xFA, 0x06, 0x00]);
            }
        }

        public ChannelOut Run(List<Cmd> cmds)
        {
            if (isFirst)                                     // remote code lives with the first channel's subroutines
                foreach (var d in song.RemoteDefs)
                {
                    int sub = Subroutine(d.Body, false, d);
                    if (!labels.TryAdd(RemoteKey(d.Id), (channelIndex, sub, d.Line, d.Column))) Error(d, "Remote code definitions cannot repeat.");
                }
            if (isFirst)
            {
                loopState.Last = (-1, -1);                   // remote code is not a loop for * to recall
                // The N-SPC velocity table ($F4 $08) from parser version 2 on; before that only #louder asks for it, after the reserve.
                if (song.AmkVersion >= 2) main.Bytes.AddRange([0xF4, 0x08]);
                main.Bytes.AddRange([0xFA, 0x04, 0x00]);
                if (song.Louder) main.Bytes.AddRange([0xF4, 0x08]);
                if (channelIndex == 0) OptionBytes(song, main.Bytes);
            }
            Sequence(cmds, main, false);
            main.Bytes.Add(0x00);
            return new ChannelOut(channelIndex, main.Bytes, subs, subAt, calls, introAt, noLoop);
        }

        /// <summary>What #halvetempo or #option dividetempo divides tempo and every time by.</summary>
        private int Divisor => song.HalveTempo ? 2 : song.DivideTempo ?? 1;

        /// <summary>A length in ticks before anything is rounded, so a note and its ties are added exactly.</summary>
        private double NoteTicks(MmlLength len, bool triplet)
        {
            double t = len.Ticks is { } k ? k : (double)Ticks / (len.Denominator ?? defaultLength);
            if (len.Dots > 0) t *= 2.0 - Math.Pow(0.5, len.Dots);
            if (triplet) t *= 2.0 / 3.0;
            return t;
        }

        private int Round(double ticks) => (int)Math.Floor(ticks / Divisor + 0.5);

        /// <summary>The way a note's length is cut when a slide (&) comes straight after it: what is left over first, then 127s.</summary>
        private static List<int> SplitBeforeSlide(int total)
        {
            var parts = new List<int>();
            if (total % 127 != 0) parts.Add(total % 127);
            for (int i = 0; i < total / 127; i++) parts.Add(127);
            return parts;
        }

        private static List<int> Split(int total)
        {
            if (total <= 127) return [total];
            if (total % 2 == 0 && total / 2 <= 127) return [total / 2, total / 2];
            var parts = new List<int>();
            while (total > 127) { parts.Add(127); total -= 127; }
            if (total > 0) parts.Add(total);
            return parts;
        }

        private int NoteByte(NoteCmd n)
        {
            int b = 0x80 + (octave - 1) * 12 + n.Letter + n.Accidental + (tuneSet ? tune : InstrumentOffset(instrument));
            if (b is < 0x80 or > 0xC5) { Error(n, "Note's pitch is out of range."); return 0x80; }
            return b;
        }

        private void Sequence(List<Cmd> cmds, Buf buf, bool triplet)
        {
            for (int i = 0; i < cmds.Count; i++)
            {
                var c = cmds[i];
                switch (c)
                {
                    case NoteCmd or RestCmd or TieCmd:
                        {
                            double sum = 0, lastTicks = 0;
                            int end = i;
                            sum += TicksOf(cmds[i], triplet);
                            // A note takes the ties after it; a rest takes the ties and rests after it.
                            while (end + 1 < cmds.Count && (cmds[end + 1] is TieCmd || (c is RestCmd && cmds[end + 1] is RestCmd)))
                            { lastTicks = TicksOf(cmds[end + 1], triplet); sum += lastTicks; end++; }
                            bool slideNext = end + 1 < cmds.Count && cmds[end + 1] is PitchSlideCmd || cmds[end] is TieCmd { BeforeBend: true };
                            if (slideNext)
                            {
                                // The last thing before a slide is written on its own.
                                if (end > i)
                                {
                                    int head = Round(sum - lastTicks), tail = Round(lastTicks);
                                    if (head > 0) Group(c, head, buf);
                                    if (tail > 0) Group(c is RestCmd ? c : cmds[end], tail, buf, true);   // a rest's own is a rest
                                }
                                else if (Round(sum) > 0) Group(c, Round(sum), buf, true);
                                i = end;
                                break;
                            }
                            int total = Round(sum);
                            if (total == 0) { i = end; break; }                // too short to write: AddmusicK leaves it out
                            Group(c, total, buf);
                            i = end;
                            break;
                        }
                    case OctaveCmd o: octave = o.Octave; break;
                    case OctaveShiftCmd s:
                        octave += s.By;
                        if (octave is < 1 or > 6) { Error(s, "Octave out of range."); octave = Math.Clamp(octave, 1, 6); }
                        break;
                    case DefaultLengthCmd l: defaultLength = l.Denominator; break;
                    case TuneCmd h: tune = h.Semitones; tuneSet = true; break;
                    case InstrumentCmd n: Instrument(n, buf); break;
                    case VolumeCmd v: buf.Bytes.AddRange([0xE7, (byte)v.Value]); break;
                    case GlobalVolumeCmd w: buf.Bytes.AddRange([0xE0, (byte)w.Value]); break;
                    case PanCmd y: buf.Bytes.AddRange([0xDB, (byte)(y.Value | (y.SurroundLeft != 0 ? 0x80 : 0) | (y.SurroundRight != 0 ? 0x40 : 0))]); break;
                    case QuantCmd q: qv = q.Value; buf.LastQv = null; break;   // written again with the next note, even unchanged (measured)
                    case TempoCmd t:
                        {
                            int v = t.Value / Divisor;
                            if (v == 0) { Error(t, "Tempo has been zeroed out by #halvetempo."); break; }
                            buf.Bytes.AddRange([0xE2, (byte)v]);
                            break;
                        }
                    case VibratoCmd p: buf.Bytes.AddRange(Scaled([0xDE, (byte)p.Delay, (byte)p.Rate, (byte)p.Extent])); break;
                    case NoiseCmd n: buf.Bytes.AddRange([0xF8, (byte)n.Value]); break;
                    case NoLoopCmd: noLoop = true; break;
                    case ResumeCmd: buf.LastDuration = null; break;
                    case IntroCmd intro:
                        if (buf != main) { Error(intro, "The intro marker (\"/\") cannot be inside a loop."); break; }
                        introAt = buf.Bytes.Count;
                        buf.LastDuration = null;
                        break;
                    case SampleLoadCmd s:
                        {
                            int index = song.Samples.IndexOf(s.Sample);
                            if (index < 0) { Error(s, "The specified sample was not included in this song."); break; }
                            buf.Bytes.AddRange([0xF3, (byte)(groupBase + index), (byte)s.Tuning]);
                            break;
                        }
                    case HexCmd h:
                        // $EF's parameters are written channels, left, right and stored left, right, channels.
                        if (h.Bytes[0] == 0xEF) buf.Bytes.AddRange([0xEF, h.Bytes[2], h.Bytes[3], h.Bytes[1]]);
                        else
                        {
                            var scaled = Scaled(h.Bytes);
                            if ((scaled[0] == 0xE2 && scaled[1] == 0) || (scaled[0] == 0xE3 && scaled[2] == 0)) { Error(h, "Tempo has been zeroed out."); break; }
                            buf.Bytes.AddRange(scaled);
                        }
                        if (h.Bytes[0] == 0xDA) { instrument = h.Bytes[1]; pendingPercussion = -1; }
                        if (h.Bytes[0] is 0xE6 or 0xE9) buf.Forget();           // a subloop edge or loop call, written by hand
                        break;
                    case TripletCmd t: Sequence(t.Body, buf, true); break;
                    case LoopCmd l: EmitCall(buf, Subroutine(l.Body, triplet, l), -1, l.Count, l); break;
                    case LabelLoopCmd l:
                        {
                            int sub = Subroutine(l.Body, triplet, l);
                            if (!labels.TryAdd(l.Id, (channelIndex, sub, l.Line, l.Column))) Error(l, "Label redefinition.");
                            EmitCall(buf, sub, -1, l.Count, l);
                            break;
                        }
                    case LabelCallCmd l:
                        EmitCall(buf, -1, l.Id, l.Count, l);
                        break;
                    case RecallCmd r:
                        if (loopState.Last.Sub < 0) { Error(r, "There is no loop to recall."); break; }
                        if (loopState.Last.Ch == channelIndex) { EmitCall(buf, loopState.Last.Sub, -1, r.Count, r); break; }
                        int key = RecallBase + loopState.Recalls++;  // another channel's: called the way a label is
                        labels[key] = (loopState.Last.Ch, loopState.Last.Sub, r.Line, r.Column);
                        EmitCall(buf, -1, key, r.Count, r);
                        break;
                    case SuperLoopCmd s:
                        buf.Bytes.AddRange([0xE6, 0x00]);
                        buf.Forget();
                        Sequence(s.Body, buf, triplet);
                        buf.Bytes.AddRange([0xE6, (byte)(s.Count - 1)]);
                        buf.Forget();
                        break;
                    case PitchSlideCmd: buf.Slide = true; break;
                    case RemoteDefCmd d: Error(d, "Remote code definitions must come before any channel's text."); break;
                    case RemoteCallCmd r: RemoteCall(r, buf, triplet); break;
                }
            }
        }

        private const int RemoteBase = 0x10000, RecallBase = 0x20000;
        private static int RemoteKey(int id) => RemoteBase + id;

        /// <summary><c>(!id, type, arg)</c>: $FC, the address of the code, the event type, and its argument (a hex byte or a note length in ticks).
        /// Written from the documentation: AddmusicK 1.0.5 does not compile it, so there is nothing to check it against.</summary>
        private void RemoteCall(RemoteCallCmd r, Buf buf, bool triplet)
        {
            int arg = r.ArgByte ?? (r.Arg is { } len ? Round(NoteTicks(len, triplet)) : 0);
            buf.Bytes.Add(0xFC);
            if (r.Type == 0) buf.Bytes.AddRange([0x00, 0x00]);
            else
            {
                calls.Add(new Call(buf.Sub >= 0, buf.Sub, buf.Bytes.Count, -1, RemoteKey(r.Id), r));
                buf.Bytes.AddRange([0x00, 0x00]);
            }
            buf.Bytes.Add((byte)r.Type);
            buf.Bytes.Add((byte)arg);
        }

        private double TicksOf(Cmd c, bool triplet) => c switch
        {
            NoteCmd n => NoteTicks(n.Length, triplet),
            RestCmd r => NoteTicks(r.Length, triplet),
            TieCmd t => NoteTicks(t.Length, triplet),
            _ => 0,
        };

        private void Instrument(InstrumentCmd n, Buf buf)
        {
            int v = n.Number;
            if (v is >= 21 and <= 29) { pendingPercussion = v; instrument = v; return; }
            if (v >= 30 && v - 30 >= song.Instruments.Count) { Error(n, "This custom instrument has not been defined yet."); return; }
            pendingPercussion = -1;
            instrument = v;
            buf.Bytes.AddRange([0xDA, (byte)v]);
        }

        /// <summary>A note, rest or tie, with the ties after it already added to the length: the duration (and
        /// quantisation) bytes when they changed, then the note, then a tie or rest byte for each further part.</summary>
        private void Group(Cmd head, int total, Buf buf, bool beforeSlide = false)
        {
            var parts = beforeSlide ? SplitBeforeSlide(total) : Split(total);
            int noteByte = head switch
            {
                NoteCmd n => pendingPercussion >= 0 ? 0xBB + pendingPercussion : NoteByte(n),
                RestCmd => 0xC7,
                _ => 0xC6,
            };
            if (head is NoteCmd) pendingPercussion = -1;
            if (buf.Slide)
            {
                // The note before bent into this one: over the length last written (nothing, after a loop or an intro), to where this one is.
                buf.Bytes.AddRange([0xDD, 0x00, (byte)(buf.LastDuration ?? 0), (byte)noteByte]);
                buf.Slide = false;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                int d = parts[i];
                if (i == 0)
                {
                    if (buf.LastDuration != d || buf.LastQv != qv) buf.Bytes.Add((byte)d);
                    if (buf.LastQv != qv) buf.Bytes.Add((byte)qv);
                    buf.LastDuration = d; buf.LastQv = qv;
                    buf.Bytes.Add((byte)noteByte);
                }
                else
                {
                    if (buf.LastDuration != d) { buf.Bytes.Add((byte)d); buf.LastDuration = d; }
                    buf.Bytes.Add((byte)(head is RestCmd ? 0xC7 : 0xC6));
                }
            }
        }

        /// <summary>A command's bytes with the times in it divided as the song's tempo is: durations and delays round down, and the
        /// rate of a vibrato or tremolo is multiplied (to at most 255).</summary>
        private byte[] Scaled(byte[] b)
        {
            int d = Divisor;
            if (d == 1) return b;
            var r = (byte[])b.Clone();
            byte Div(byte v) => (byte)(v / d);
            switch (b[0])
            {
                case 0xDC or 0xE1 or 0xE8 or 0xEA or 0xF2 or 0xE2: r[1] = Div(b[1]); break;
                case 0xE3: r[1] = Div(b[1]); r[2] = Div(b[2]); break;
                case 0xDD or 0xEB or 0xEC: r[1] = Div(b[1]); r[2] = Div(b[2]); break;
                case 0xDE or 0xE5: r[1] = Div(b[1]); r[2] = (byte)Math.Min(255, b[2] * d); break;
                case 0xFB when b.Length >= 3 && b[1] >= 0x80: r[2] = Div(b[2]); break;
                case 0xFB when b.Length >= 3: r[2] = Div(b[2]); break;
            }
            return r;
        }

        /// <summary>A loop's body, compiled into a subroutine. Its notes use the loops' own transposition (h, kept from loop to loop and apart from the
        /// text's), and what the body does to the instrument and a pending percussion hit is undone once it ends; octave, default length and quantisation
        /// carry in and out, as they do through the text.</summary>
        private int Subroutine(List<Cmd> body, bool triplet, Cmd at)
        {
            subAt.Add((at.Line, at.Column));
            var (savedTune, savedSet, savedInstrument, savedPercussion) = (tune, tuneSet, instrument, pendingPercussion);
            (tune, tuneSet) = (loopState.Tune, loopState.TuneSet);   // the loops' own transposition, which carries from one loop to the next
            var sub = new Buf(subs.Count);
            subs.Add(sub.Bytes);
            Sequence(body, sub, triplet);
            sub.Bytes.Add(0x00);
            loopState.Last = (channelIndex, sub.Sub);
            (loopState.Tune, loopState.TuneSet) = (tune, tuneSet);
            (tune, tuneSet, instrument, pendingPercussion) = (savedTune, savedSet, savedInstrument, savedPercussion);
            return sub.Sub;
        }

        private void EmitCall(Buf buf, int sub, int label, int count, Cmd at)
        {
            buf.Bytes.Add(0xE9);
            calls.Add(new Call(buf.Sub >= 0, buf.Sub, buf.Bytes.Count, sub, label, at));
            buf.Bytes.Add(0); buf.Bytes.Add(0);
            buf.Bytes.Add((byte)count);
            buf.Forget();
        }
    }
}
