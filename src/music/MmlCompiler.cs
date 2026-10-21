using System.Text;

namespace PipeDream.Music;

/// <summary>A song compiled to the bytes the engine plays, addressed as if loaded at <see cref="Base"/>.</summary>
public sealed record CompiledSong(byte[] Bytes, int Base, IReadOnlyList<MmlDiagnostic> Diagnostics)
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

        var channels = new ChannelOut?[8];
        var labels = new Dictionary<int, (int Ch, int Sub)>();
        int first = Array.FindIndex(song.Channels, c => c is { Count: > 0 });
        for (int i = 0; i < 8; i++)
            if (song.Channels[i] is { Count: > 0 } cmds)
            {
                var c = new ChannelCompiler(song, problems, i == first, i, labels);
                channels[i] = c.Run(cmds);
            }
        if (problems.Count > 0) return new CompiledSong([], baseAddress, problems);

        // The sample table the song's instruments name.
        int groupBase = song.SampleGroups.Sum(groupSize);
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
        if (first < 0) return new CompiledSong([], baseAddress, [new MmlDiagnostic(1, 1, "This song doesn't seem to have any data.")]);

        bool noLoop = song.NoLoop || channels.Any(c => c?.NoLoop == true);
        bool intro = channels.Any(c => c?.IntroAt is not null);

        // Layout: the phase list, a pointer table per phrase, then every channel's bytes in order.
        int header = noLoop ? 4 : intro ? 8 : 6;
        int tables = intro ? 2 : 1;
        int at = baseAddress + header + table.Count + tables * 16;
        var starts = new int?[8];
        for (int i = 0; i < 8; i++)
            if (channels[i] is { } c) { starts[i] = at; at += c.MainLength; }
        var subBase = new int[8];                          // every channel's subroutines come after every channel's own bytes
        for (int i = 0; i < 8; i++)
            if (channels[i] is { } c) { subBase[i] = at; at += c.SubLength; }

        var bytes = new List<byte>();
        void W16(int v) { bytes.Add((byte)v); bytes.Add((byte)(v >> 8)); }
        int firstTable = baseAddress + header + table.Count;
        if (noLoop) { W16(firstTable); W16(0); }
        else if (intro) { W16(firstTable); W16(firstTable + 16); W16(0x00FF); W16(baseAddress + 2); }
        else { W16(firstTable); W16(0x00FF); W16(baseAddress); }
        bytes.AddRange(table);
        for (int phrase = 0; phrase < tables; phrase++)
        {
            if (phrase == 1) firstTable += 16;
            for (int i = 0; i < 8; i++)
                W16(channels[i] is { } c && starts[i] is { } s ? s + (phrase == 1 ? c.IntroAt ?? 0 : 0) : 0);
        }
        // Where every subroutine ended up, so calls (and label calls from other channels) can be filled in.
        var subAddr = new int[8][];
        for (int i = 0; i < 8; i++)
            if (channels[i] is { } c) subAddr[i] = c.SubStarts(subBase[i]);
        var subData = new List<byte>();
        for (int i = 0; i < 8; i++)
            if (channels[i] is { } c)
            {
                problems.AddRange(c.Resolve(subAddr, labels, out var main, out var subBytes));
                bytes.AddRange(main);
                subData.AddRange(subBytes);
            }
        bytes.AddRange(subData);
        if (problems.Count > 0) return new CompiledSong([], baseAddress, problems);
        return new CompiledSong(bytes.ToArray(), baseAddress, problems);
    }

    /// <summary>A call to a subroutine still to be filled in: where it is, and the subroutine it names (a loop of this
    /// channel, or a label that may be another channel's).</summary>
    private sealed record Call(bool InSub, int Owner, int Offset, int Sub, int Label, Cmd At);

    /// <summary>One channel's bytes: the main stream, then its subroutines, with the places that call them still to be filled in.</summary>
    private sealed class ChannelOut(int index, List<byte> main, List<List<byte>> subs, List<Call> calls, int? introAt, bool noLoop)
    {
        public int? IntroAt => introAt;
        public bool NoLoop => noLoop;
        public int MainLength => main.Count;
        public int SubLength => subs.Sum(s => s.Count);

        public int[] SubStarts(int start)
        {
            var at = start;
            var result = new int[subs.Count];
            for (int i = 0; i < subs.Count; i++) { result[i] = at; at += subs[i].Count; }
            return result;
        }

        public List<MmlDiagnostic> Resolve(int[][] subAddr, Dictionary<int, (int Ch, int Sub)> labels, out byte[] mainBytes, out byte[] subBytes)
        {
            var errors = new List<MmlDiagnostic>();
            foreach (var c in calls)
            {
                int target;
                if (c.Label >= 0)
                {
                    if (!labels.TryGetValue(c.Label, out var l)) { errors.Add(new MmlDiagnostic(c.At.Line, c.At.Column, "A loop with that label has not been defined.")); continue; }
                    target = subAddr[l.Ch][l.Sub];
                }
                else target = subAddr[index][c.Sub];
                var list = c.InSub ? subs[c.Owner] : main;
                list[c.Offset] = (byte)target;
                list[c.Offset + 1] = (byte)(target >> 8);
            }
            mainBytes = main.ToArray();
            subBytes = subs.SelectMany(x => x).ToArray();
            return errors;
        }
    }

    /// <summary>One run of bytes being written (the main stream or a subroutine) and what the engine will have been told by then.</summary>
    private sealed class Buf(int sub)
    {
        public readonly List<byte> Bytes = new();
        public readonly int Sub = sub;                // -1 for the main stream
        public int? LastDuration, LastQv;
        public void Forget() { LastDuration = null; LastQv = null; }
    }

    private sealed class ChannelCompiler(MmlSong song, List<MmlDiagnostic> problems, bool isFirst, int channelIndex, Dictionary<int, (int Ch, int Sub)> labels)
    {
        // What the channel is set to as the text is read.
        private int octave = 4, defaultLength = 8, tune, instrument = -1, qv = 0x7F;
        private int pendingPercussion = -1;

        private readonly List<List<byte>> subs = new();
        private readonly List<Call> calls = new();
        private int lastLoop = -1;
        private int? introAt;
        private bool noLoop;
        private Buf main = new(-1);

        private void Error(Cmd c, string message) { if (problems.Count < 200) problems.Add(new MmlDiagnostic(c.Line, c.Column, message)); }

        public ChannelOut Run(List<Cmd> cmds)
        {
            if (isFirst)
            {
                main.Bytes.AddRange([0xF4, 0x08, 0xFA, 0x04, 0x00]);
                foreach (string option in song.OptionOrder)          // in the order the song wrote them
                {
                    if (option == "tempoimmunity") main.Bytes.AddRange([0xF4, 0x07]);
                    else if (option == "smwvtable") main.Bytes.AddRange([0xFA, 0x06, 0x00]);
                }
            }
            Sequence(cmds, main, false);
            main.Bytes.Add(0x00);
            return new ChannelOut(channelIndex, main.Bytes, subs, calls, introAt, noLoop);
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

        private int Round(double ticks) => Math.Max(1, (int)Math.Floor(ticks / Divisor + 0.5));

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
            int b = 0x80 + (octave - 1) * 12 + n.Letter + n.Accidental + tune + InstrumentOffset(instrument);
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
                            double sum = 0;
                            int end = i;
                            sum += TicksOf(cmds[i], triplet);
                            // A note takes the ties after it; a rest takes the ties and rests after it.
                            while (end + 1 < cmds.Count && (cmds[end + 1] is TieCmd || (c is RestCmd && cmds[end + 1] is RestCmd)))
                            { sum += TicksOf(cmds[end + 1], triplet); end++; }
                            int total = Round(sum);
                            Group(c, total, buf);
                            // A pitch slide: the note just played bends into the next one.
                            if (c is NoteCmd && end + 1 < cmds.Count && cmds[end + 1] is PitchSlideCmd ps)
                            {
                                if (end + 2 < cmds.Count && cmds[end + 2] is NoteCmd target)
                                {
                                    int nb = NoteByte(target);
                                    buf.Bytes.AddRange([0xDD, 0x00, (byte)Math.Min(total, 127), (byte)nb]);
                                    end++;
                                }
                                else { Error(ps, "A pitch slide (\"&\") must be followed by a note."); end++; }
                            }
                            i = end;
                            break;
                        }
                    case OctaveCmd o: octave = o.Octave; break;
                    case OctaveShiftCmd s:
                        octave += s.By;
                        if (octave is < 1 or > 6) { Error(s, "Octave out of range."); octave = Math.Clamp(octave, 1, 6); }
                        break;
                    case DefaultLengthCmd l: defaultLength = l.Denominator; break;
                    case TuneCmd h: tune = h.Semitones; break;
                    case InstrumentCmd n: Instrument(n, buf); break;
                    case VolumeCmd v: buf.Bytes.AddRange([0xE7, (byte)v.Value]); break;
                    case GlobalVolumeCmd w: buf.Bytes.AddRange([0xE0, (byte)w.Value]); break;
                    case PanCmd y: buf.Bytes.AddRange([0xDB, (byte)(y.Value | (y.SurroundLeft != 0 ? 0x80 : 0) | (y.SurroundRight != 0 ? 0x40 : 0))]); break;
                    case QuantCmd q: qv = q.Value; break;
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
                            buf.Bytes.AddRange([0xF3, (byte)index, (byte)s.Tuning]);
                            break;
                        }
                    case HexCmd h:
                        // $EF's parameters are written channels, left, right and stored left, right, channels.
                        if (h.Bytes[0] == 0xEF) buf.Bytes.AddRange([0xEF, h.Bytes[2], h.Bytes[3], h.Bytes[1]]);
                        else buf.Bytes.AddRange(Scaled(h.Bytes));
                        if (h.Bytes[0] == 0xDA) { instrument = h.Bytes[1]; pendingPercussion = -1; }
                        if (h.Bytes[0] is 0xE6 or 0xE9) buf.Forget();           // a subloop edge or loop call, written by hand
                        break;
                    case TripletCmd t: Sequence(t.Body, buf, true); break;
                    case LoopCmd l: EmitCall(buf, Subroutine(l.Body, triplet), -1, l.Count, l); break;
                    case LabelLoopCmd l:
                        {
                            int sub = Subroutine(l.Body, triplet);
                            labels[l.Id] = (channelIndex, sub);
                            EmitCall(buf, sub, -1, l.Count, l);
                            break;
                        }
                    case LabelCallCmd l:
                        EmitCall(buf, -1, l.Id, l.Count, l);
                        break;
                    case RecallCmd r:
                        if (lastLoop < 0) { Error(r, "There is no loop to recall."); break; }
                        EmitCall(buf, lastLoop, -1, r.Count, r);
                        break;
                    case SuperLoopCmd s:
                        buf.Bytes.AddRange([0xE6, 0x00]);
                        buf.Forget();
                        Sequence(s.Body, buf, triplet);
                        buf.Bytes.AddRange([0xE6, (byte)(s.Count - 1)]);
                        buf.Forget();
                        break;
                    case PitchSlideCmd p: Error(p, "A pitch slide (\"&\") must come after a note."); break;
                    case RemoteDefCmd or RemoteCallCmd: Error(c, "Remote code is not supported yet."); break;
                }
            }
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
        private void Group(Cmd head, int total, Buf buf)
        {
            var parts = Split(total);
            int noteByte = head switch
            {
                NoteCmd n => pendingPercussion >= 0 ? 0xBB + pendingPercussion : NoteByte(n),
                RestCmd => 0xC7,
                _ => 0xC6,
            };
            if (head is NoteCmd) pendingPercussion = -1;
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
                case 0xDC or 0xE1 or 0xE8 or 0xEA or 0xF2: r[1] = Div(b[1]); break;
                case 0xE3: r[1] = Div(b[1]); r[2] = Div(b[2]); break;
                case 0xDD or 0xEB or 0xEC: r[1] = Div(b[1]); r[2] = Div(b[2]); break;
                case 0xDE or 0xE5: r[1] = Div(b[1]); r[2] = (byte)Math.Min(255, b[2] * d); break;
                case 0xFB when b.Length >= 3 && b[1] >= 0x80: r[2] = Div(b[2]); break;
                case 0xFB when b.Length >= 3: r[2] = Div(b[2]); break;
            }
            return r;
        }

        private int Subroutine(List<Cmd> body, bool triplet)
        {
            var sub = new Buf(subs.Count);
            subs.Add(sub.Bytes);
            Sequence(body, sub, triplet);
            sub.Bytes.Add(0x00);
            lastLoop = sub.Sub;
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
