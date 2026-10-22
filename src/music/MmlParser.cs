using System.Globalization;
using System.Text;

namespace PipeDream.Music;

/// <summary>
/// AddmusicK's MML to a syntax tree (reference/AMK_PORT.md, phase 2). Written from the documentation and
/// from probing AddmusicK as a black box, not from its source. The preprocessor (<c>#define #if ...</c>) runs
/// first and keeps every line where it was; replacements (<c>"a=b"</c>) are applied to the text after them as
/// the parser reaches it. Errors carry the line and column; a bad token is skipped so one pass finds more
/// than the first.
/// </summary>
public static class MmlParser
{
    public static MmlSong Parse(string text)
    {
        var song = new MmlSong();
        var src = Preprocess(text, song.Diagnostics);
        new Parser(src, song).Run();
        return song;
    }

    // ---- the preprocessor ----

    /// <summary>#define #undef #ifdef #ifndef #if #endif #error, each alone on its line. Directive lines and the lines of an
    /// untaken branch become empty so line numbers are unchanged.</summary>
    internal static string Preprocess(string text, List<MmlDiagnostic> problems)
    {
        var defines = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var taken = new Stack<bool>();                // whether each open #if is being taken
        bool Active() => taken.All(t => t);
        var sb = new StringBuilder();
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            string line = raw.Trim();
            string body = line;
            int semi = body.IndexOf(';');
            if (semi >= 0) body = body[..semi].Trim();
            string lower = body.ToLowerInvariant();
            bool handled = true;
            if (lower.StartsWith("#define ") || lower == "#define")
            {
                if (Active())
                {
                    var parts = body.Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) problems.Add(new(n, 1, "Error parsing #define directive."));
                    else defines[parts[1]] = parts.Length > 2 && int.TryParse(parts[2].Trim(), out int v) ? v : null;
                }
            }
            else if (lower.StartsWith("#undef "))
            {
                if (Active()) defines.Remove(body[7..].Trim());
            }
            else if (lower.StartsWith("#ifdef "))
                taken.Push(Active() && defines.ContainsKey(body[7..].Trim()));
            else if (lower.StartsWith("#ifndef "))
                taken.Push(Active() && !defines.ContainsKey(body[8..].Trim()));
            else if (lower.StartsWith("#if "))
            {
                bool yes = false;
                var p = body[4..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 3 && int.TryParse(p[2], out int rhs) && defines.TryGetValue(p[0], out var lhs) && lhs is { } l)
                    yes = p[1] switch { "==" => l == rhs, "!=" => l != rhs, "<" => l < rhs, ">" => l > rhs, "<=" => l <= rhs, ">=" => l >= rhs, _ => false };
                else if (p.Length != 3) problems.Add(new(n, 1, "Error parsing #if directive."));
                taken.Push(Active() && yes);
            }
            else if (lower == "#endif")
            {
                if (taken.Count == 0) problems.Add(new(n, 1, "#endif found without a matching #if."));
                else taken.Pop();
            }
            else if (lower.StartsWith("#error"))
            {
                if (Active()) problems.Add(new(n, 1, "#error: " + body[6..].Trim()));
            }
            else handled = false;

            sb.Append(handled || !Active() ? "" : raw.TrimEnd('\r'));
            sb.Append('\n');
        }
        if (taken.Count > 0) problems.Add(new(n, 1, "Missing #endif."));
        sb.Length--;
        return sb.ToString();
    }

    // ---- hex commands: the byte after $ and how many parameter bytes follow ----

    private static readonly Dictionary<int, int> HexParams = new()
    {
        [0xDA] = 1, [0xDB] = 1, [0xDC] = 2, [0xDD] = 3, [0xDE] = 3, [0xDF] = 0, [0xE0] = 1, [0xE1] = 2, [0xE2] = 1,
        [0xE3] = 2, [0xE4] = 1, [0xE5] = 3, [0xE6] = 1, [0xE7] = 1, [0xE8] = 2, [0xE9] = 3, [0xEA] = 1, [0xEB] = 3,
        [0xEC] = 3, [0xED] = 2, [0xEE] = 1, [0xEF] = 3, [0xF0] = 0, [0xF1] = 3, [0xF2] = 3, [0xF3] = 2, [0xF4] = 1,
        [0xF5] = 8, [0xF6] = 2, [0xF8] = 1, [0xF9] = 2, [0xFA] = 2, [0xFB] = -1, [0xFC] = 4,
    };

    private sealed class Parser(string src, MmlSong song)
    {
        private int pos, line = 1, lineStart;
        private readonly Dictionary<string, string> replacements = new();
        private int col => pos - lineStart + 1;
        private bool Eof => pos >= src.Length;
        private char Peek(int ahead = 0) => pos + ahead < src.Length ? src[pos + ahead] : '\0';

        private void Error(string message, int atLine = 0, int atCol = 0)
        {
            if (song.Diagnostics.Count < 200) song.Diagnostics.Add(new(atLine == 0 ? line : atLine, atCol == 0 ? col : atCol, message));
        }

        private void Advance()
        {
            if (Peek() == '\n') { line++; lineStart = pos + 1; }
            pos++;
        }

        private void SkipTrivia()
        {
            while (!Eof)
            {
                char c = Peek();
                if (c is ' ' or '\t' or '\r' or '\n') Advance();
                else if (c == ';') while (!Eof && Peek() != '\n') Advance();
                else break;
            }
        }

        /// <summary>The text at the cursor starts a defined replacement: swap it for its value, as often as it takes.</summary>
        private void Replace()
        {
            if (replacements.Count == 0) return;
            for (int guard = 0; guard < 1000; guard++)
            {
                string? best = null;
                foreach (string key in replacements.Keys)
                    if (string.CompareOrdinal(src, pos, key, 0, key.Length) == 0 && (best is null || key.Length > best.Length)) best = key;
                if (best is null) return;
                src = src[..pos] + replacements[best] + src[(pos + best.Length)..];
            }
            Error("Replacement loop: a replacement keeps producing itself.");
        }

        public void Run()
        {
            int channel = -1;
            while (true)
            {
                SkipTrivia();
                if (Eof) break;
                if (Peek() == '#') { ParseDirective(ref channel); continue; }
                if (channel < 0 && Peek() == '(' && Peek(1) == '!')
                {
                    var defs = new List<Cmd>();
                    ParseItem(defs, End.Channel);
                    foreach (var d in defs)
                    {
                        if (d is RemoteDefCmd r) song.RemoteDefs.Add(r);
                        else Error("Remote code must be defined before any channel's text.", d.Line, d.Column);
                    }
                    continue;
                }
                if (channel < 0)
                {
                    Error("Note data must be inside a channel!");
                    while (!Eof && Peek() != '#') Advance();
                    continue;
                }
                var list = song.Channels[channel] ??= new List<Cmd>();
                ParseSequence(list, End.Channel);
            }
            if (song.Channels.All(c => c is null or { Count: 0 }) && song.Diagnostics.Count == 0)
                Error("This song doesn't seem to have any data.", 1, 1);
            if (song.AmkVersion == 0 && !song.Am4 && !song.Amm && song.Diagnostics.Count == 0)
                Error("Song did not specify target program with #amk, #am4, or #amm.", 1, 1);
        }

        // ---- directives ----

        private string ReadWord()
        {
            int start = pos;
            while (char.IsLetterOrDigit(Peek()) || Peek() == '_') Advance();
            return src[start..pos];
        }

        private string? ReadQuoted()
        {
            if (Peek() != '"') return null;
            Advance();
            int start = pos;
            while (!Eof && Peek() != '"' && Peek() != '\n') Advance();
            if (Peek() != '"') { Error("Missing closing quote."); return null; }
            string s = src[start..pos];
            Advance();
            return s;
        }

        private void ParseDirective(ref int channel)
        {
            int dLine = line, dCol = col;
            Advance();                                     // '#'
            if (char.IsDigit(Peek()))
            {
                int n = Peek() - '0';
                Advance();
                if (char.IsDigit(Peek()) || n > 7) { Error("Error parsing '#' directive.", dLine, dCol); return; }
                channel = n;
                if (song.Channels[n] is { Count: > 0 } existing) existing.Add(new ResumeCmd(dLine, dCol));
                song.Channels[n] ??= new List<Cmd>();
                return;
            }
            string word = ReadWord().ToLowerInvariant();
            switch (word)
            {
                case "amk": SkipSpaces(); song.AmkVersion = ReadInt() ?? 0; if (song.AmkVersion == 0) Error("Error parsing #amk directive.", dLine, dCol); break;
                case "am4": song.Am4 = true; break;
                case "amm": song.Amm = true; break;
                case "louder": song.Louder = true; break;
                case "halvetempo": song.HalveTempo = true; break;
                case "path": { SkipSpaces(); var p = ReadQuoted(); if (p is null) Error("Error parsing #path directive.", dLine, dCol); else song.Path = p; break; }
                case "pad": { SkipSpaces(); int? v = ReadHex(); if (v is null) Error("Error parsing #pad directive.", dLine, dCol); else song.Pad = v; break; }
                case "option": ParseOption(dLine, dCol); break;
                case "samples": ParseSamples(dLine, dCol); break;
                case "instruments": ParseInstruments(dLine, dCol); break;
                case "spc": ParseSpc(dLine, dCol); break;
                case "define" or "undef" or "ifdef" or "ifndef" or "if" or "endif" or "error": break;     // the preprocessor took these
                default:
                    Error("Error parsing '#' directive.", dLine, dCol);
                    break;
            }
            if (word is not ("samples" or "instruments" or "spc")) while (!Eof && Peek() is ' ' or '\t' or '\r') Advance();
        }

        private void SkipSpaces() { while (Peek() is ' ' or '\t') Advance(); }

        private int? ReadInt()
        {
            int start = pos;
            if (Peek() is '-' or '+') Advance();
            while (char.IsDigit(Peek())) Advance();
            return int.TryParse(src.AsSpan(start, pos - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : null;
        }

        private int? ReadHex()
        {
            if (Peek() == '$') Advance();
            int start = pos;
            while (Uri.IsHexDigit(Peek())) Advance();
            return pos > start && int.TryParse(src.AsSpan(start, pos - start), NumberStyles.HexNumber, null, out int v) ? v : null;
        }

        private void ParseOption(int dLine, int dCol)
        {
            SkipSpaces();
            string name = ReadWord().ToLowerInvariant();
            switch (name)
            {
                case "tempoimmunity": song.TempoImmunity = true; song.OptionOrder.Add(name); break;
                case "noloop": song.NoLoop = true; break;
                case "smwvtable": song.SmwVTable = true; song.OptionOrder.Add(name); break;
                case "dividetempo": { SkipSpaces(); int? v = ReadInt(); if (v is null or < 1) Error("Error parsing #option dividetempo.", dLine, dCol); else song.DivideTempo = v; break; }
                default: Error("Unknown #option.", dLine, dCol); break;
            }
        }

        private bool OpenBrace(string what)
        {
            SkipTrivia();
            if (Peek() != '{') { Error($"Error parsing {what}: expected '{{'."); return false; }
            Advance();
            return true;
        }

        private void ParseSamples(int dLine, int dCol)
        {
            song.SamplesDeclared = true;
            if (!OpenBrace("#samples")) return;
            while (true)
            {
                SkipTrivia();
                if (Eof) { Error("Missing '}' in #samples.", dLine, dCol); return; }
                if (Peek() == '}') { Advance(); return; }
                if (Peek() == '#') { Advance(); song.SampleGroups.Add(ReadWord()); continue; }
                if (Peek() == '"') { var s = ReadQuoted(); if (s is not null) song.Samples.Add(s); continue; }
                Error($"Unexpected character \"{Peek()}\" in #samples."); Advance();
            }
        }

        private void ParseInstruments(int dLine, int dCol)
        {
            if (!OpenBrace("#instruments")) return;
            while (true)
            {
                SkipTrivia();
                if (Eof) { Error("Missing '}' in #instruments.", dLine, dCol); return; }
                if (Peek() == '}') { Advance(); return; }
                string? sample = null; int? from = null, noise = null;
                int eLine = line, eCol = col;
                if (Peek() == '"') sample = ReadQuoted();
                else if (Peek() == '@') { Advance(); from = ReadInt(); }
                else if (Peek() == 'n') { Advance(); noise = ReadHex(); }
                else { Error($"Unexpected character \"{Peek()}\" in #instruments."); Advance(); continue; }
                var bytes = new List<byte>();
                for (int i = 0; i < 5; i++)
                {
                    SkipTrivia();
                    if (Peek() != '$') break;
                    int? v = ReadHex();
                    if (v is null or > 0xFF) { Error("Error parsing an instrument's bytes.", eLine, eCol); break; }
                    bytes.Add((byte)v);
                }
                if (bytes.Count != 5) { Error("An instrument needs five bytes.", eLine, eCol); continue; }
                song.Instruments.Add(new InstrumentDecl(sample, from, noise, bytes.ToArray()));
            }
        }

        private void ParseSpc(int dLine, int dCol)
        {
            if (!OpenBrace("#spc")) return;
            while (true)
            {
                SkipTrivia();
                if (Eof) { Error("Missing '}' in #spc.", dLine, dCol); return; }
                if (Peek() == '}') { Advance(); return; }
                if (Peek() != '#') { Error($"Unexpected character \"{Peek()}\" in #spc."); Advance(); continue; }
                Advance();
                string key = ReadWord().ToLowerInvariant();
                SkipSpaces();
                var value = ReadQuoted();
                if (value is null) { Error($"Error parsing #{key} in #spc."); continue; }
                if (key == "length") song.Length = value; else song.Spc[key] = value;
            }
        }

        // ---- channel text ----

        private enum End { Channel, Loop, SuperLoop, Triplet, Remote }

        private static bool IsNoteLetter(char c) => c is >= 'a' and <= 'g';
        private static readonly int[] Semitone = [9, 11, 0, 2, 4, 5, 7];       // a b c d e f g, counted from c

        /// <summary>Commands until the end that <paramref name="end"/> names. A channel ends at the next '#'.</summary>
        private void ParseSequence(List<Cmd> into, End end)
        {
            while (true)
            {
                SkipTrivia();
                Replace();
                SkipTrivia();
                if (Eof)
                {
                    if (end != End.Channel) Error("Missing the end of a loop, triplet or remote code.");
                    return;
                }
                char c = Peek();
                if (end == End.Channel && c == '#') return;
                if (end == End.Loop && c == ']') return;
                if (end == End.SuperLoop && c == ']' && Peek(1) == ']') return;
                if (end == End.Triplet && c == '}') return;
                if (end == End.Remote && c == ']') return;
                ParseItem(into, end);
            }
        }

        private MmlLength ParseLength()
        {
            if (Peek() == '=')
            {
                Advance();
                int? t = ReadInt();
                if (t is null or < 0) { Error("Error parsing a note length."); return MmlLength.None; }
                return new MmlLength(null, 0, t);
            }
            int? d = char.IsDigit(Peek()) ? ReadInt() : null;
            int dots = 0;
            while (Peek() == '.') { Advance(); dots++; }
            return new MmlLength(d is 0 ? null : d, dots, null);
        }

        private void ParseItem(List<Cmd> into, End end)
        {
            int l = line, cc = col;
            char c = Peek();
            if (IsNoteLetter(c))
            {
                Advance();
                int acc = 0;
                if (Peek() == '+') { Advance(); acc = 1; }
                else if (Peek() == '-') { Advance(); acc = -1; }
                var len = ParseLength();
                into.Add(new NoteCmd(l, cc, Semitone[c - 'a'], acc, len));
                return;
            }
            switch (c)
            {
                case 'r': Advance(); into.Add(new RestCmd(l, cc, ParseLength())); return;
                case '^': Advance(); into.Add(new TieCmd(l, cc, ParseLength())); return;
                case 'l':
                    {
                        Advance();
                        int? d = char.IsDigit(Peek()) ? ReadInt() : null;
                        if (d is null) { Error("Error parsing note length (\"l\") directive.", l, cc); return; }
                        if (d is < 1 or > 192) { Error("Illegal value for note length (\"l\") directive.", l, cc); return; }
                        into.Add(new DefaultLengthCmd(l, cc, d.Value));
                        return;
                    }
                case 'o':
                    {
                        Advance();
                        int? o = ReadInt();
                        if (o is null or < 1 or > 6) { Error("Illegal value for octave (\"o\") directive.", l, cc); return; }
                        into.Add(new OctaveCmd(l, cc, o.Value));
                        return;
                    }
                case '<': Advance(); into.Add(new OctaveShiftCmd(l, cc, -1)); return;
                case '>': Advance(); into.Add(new OctaveShiftCmd(l, cc, +1)); return;
                case 'h':
                    {
                        Advance();
                        int? h = ReadInt();
                        if (h is null or < -127 or > 127) { Error("Illegal value for transpose (\"h\") directive.", l, cc); return; }
                        into.Add(new TuneCmd(l, cc, h.Value));
                        return;
                    }
                case '@':
                    {
                        Advance();
                        int? n = ReadInt();
                        if (n is null or < 0 or > 255) { Error("Error parsing instrument (\"@\") command.", l, cc); return; }
                        into.Add(new InstrumentCmd(l, cc, n.Value));
                        return;
                    }
                case 'v': { Advance(); int? v = ReadInt(); if (v is null or < 0 or > 255) Error("Illegal value for volume (\"v\") command.", l, cc); else into.Add(new VolumeCmd(l, cc, v.Value)); return; }
                case 'w': { Advance(); int? v = ReadInt(); if (v is null or < 0 or > 255) Error("Illegal value for global volume (\"w\") command.", l, cc); else into.Add(new GlobalVolumeCmd(l, cc, v.Value)); return; }
                case 't': { Advance(); int? v = ReadInt(); if (v is null or < 1 or > 255) Error("Illegal value for tempo (\"t\") command.", l, cc); else into.Add(new TempoCmd(l, cc, v.Value)); return; }
                case 'y':
                    {
                        Advance();
                        int? v = ReadInt();
                        int sl = 0, sr = 0;
                        if (v is null or < 0 or > 20) { Error("Illegal value for pan (\"y\") command.", l, cc); return; }
                        if (Peek() == ',')
                        {
                            Advance(); sl = ReadInt() ?? -1;
                            if (Peek() != ',') { Error("Error parsing pan (\"y\") command.", l, cc); return; }
                            Advance(); sr = ReadInt() ?? -1;
                            if (sl is not (0 or 1) || sr is not (0 or 1)) { Error("Error parsing pan (\"y\") command.", l, cc); return; }
                        }
                        into.Add(new PanCmd(l, cc, v.Value, sl, sr));
                        return;
                    }
                case 'q':
                    {
                        Advance();
                        int start = pos;
                        while (pos - start < 2 && Uri.IsHexDigit(Peek())) Advance();
                        if (pos - start != 2 || src[start] > '7') { Error("Error parsing quantization (\"q\") command.", l, cc); return; }
                        int qv = Convert.ToInt32(src.Substring(start, 2), 16);
                        if (qv == 0) { Error("Illegal value for quantization (\"q\") command.", l, cc); return; }
                        into.Add(new QuantCmd(l, cc, qv));
                        return;
                    }
                case 'n':
                    {
                        Advance();
                        int start = pos;
                        while (pos - start < 2 && Uri.IsHexDigit(Peek())) Advance();
                        int v = pos - start == 2 ? Convert.ToInt32(src.Substring(start, 2), 16) : -1;
                        if (v is < 0 or > 0x1F) { Error("Error parsing noise (\"n\") command.", l, cc); return; }
                        into.Add(new NoiseCmd(l, cc, v));
                        return;
                    }
                case 'p':
                    {
                        Advance();
                        var args = new List<int>();
                        int? a = ReadInt();
                        while (a is not null) { args.Add(a.Value); if (Peek() != ',') break; Advance(); a = ReadInt(); }
                        if (args.Count is not (2 or 3) || args.Any(x => x is < 0 or > 255)) { Error("Error parsing vibrato (\"p\") command.", l, cc); return; }
                        into.Add(args.Count == 2 ? new VibratoCmd(l, cc, 0, args[0], args[1]) : new VibratoCmd(l, cc, args[0], args[1], args[2]));
                        return;
                    }
                case '&':
                    Advance();
                    // AddmusicK reads a slide only straight after a note, rest or tie.
                    if (into.Count == 0 || into[^1] is not (NoteCmd or RestCmd or TieCmd)) { Error("Unexpected character \"&\" found.", l, cc); return; }
                    into.Add(new PitchSlideCmd(l, cc));
                    return;
                case '?': Advance(); into.Add(new NoLoopCmd(l, cc)); return;
                case '/': Advance(); into.Add(new IntroCmd(l, cc)); return;
                case '$': ParseHex(into); return;
                case '"': ParseReplacementDefinition(); return;
                case '{':
                    {
                        Advance();
                        var body = new List<Cmd>();
                        ParseSequence(body, End.Triplet);
                        if (Peek() == '}') Advance();
                        into.Add(new TripletCmd(l, cc, body));
                        return;
                    }
                case '[': ParseLoop(into, l, cc); return;
                case '(': ParseParenthesis(into, l, cc); return;
                case '*':
                    {
                        Advance();
                        int? n = char.IsDigit(Peek()) ? ReadInt() : 1;
                        if (n is null or < 1 or > 255) { Error("Invalid loop count.", l, cc); return; }
                        into.Add(new RecallCmd(l, cc, n.Value));
                        return;
                    }
                case ']' or '}' or ')':
                    Error(c == ']' ? "Loop end found outside of a loop." : $"Unexpected character \"{c}\" found.");
                    Advance();
                    return;
                default:
                    Error($"Unexpected character \"{c}\" found.");
                    Advance();
                    return;
            }
        }

        private int? ReadCount(int l, int cc, int dflt)
        {
            if (!char.IsDigit(Peek())) return dflt;
            int? n = ReadInt();
            if (n is null or < 1 or > 255) { Error("Invalid loop count.", l, cc); return null; }
            return n;
        }

        private void ParseLoop(List<Cmd> into, int l, int cc)
        {
            Advance();                                  // '['
            bool super = Peek() == '[';
            if (super) Advance();
            var body = new List<Cmd>();
            ParseSequence(body, super ? End.SuperLoop : End.Loop);
            if (Eof) return;
            Advance();                                  // ']'
            if (super) Advance();                       // the second ']'
            if (!super && body.Any(x => x is LoopCmd or LabelLoopCmd)) Error("Loops may not be nested; use [[ ]] around them.", l, cc);
            if (super && !char.IsDigit(Peek())) { Error("Error parsing subloop (\"[[ ]]\") command: it needs a count.", l, cc); return; }
            int? count = ReadCount(l, cc, 1);
            if (count is null) return;
            into.Add(super ? new SuperLoopCmd(l, cc, body, count.Value) : new LoopCmd(l, cc, body, count.Value));
        }

        private void ParseParenthesis(List<Cmd> into, int l, int cc)
        {
            Advance();                                  // '('
            if (Peek() == '"')
            {
                var name = ReadQuoted();
                SkipTrivia();
                if (Peek() != ',') { Error("Error parsing sample load.", l, cc); return; }
                Advance(); SkipTrivia();
                int? tune = ReadHex();
                SkipTrivia();
                if (name is null || tune is null or > 0xFF || Peek() != ')') { Error("Error parsing sample load.", l, cc); return; }
                Advance();
                into.Add(new SampleLoadCmd(l, cc, name, tune.Value));
                return;
            }
            bool remote = Peek() == '!';
            if (remote) Advance();
            int? id = char.IsDigit(Peek()) ? ReadInt() : null;
            SkipTrivia();
            if (id is null) { Error("Error parsing a loop label.", l, cc); return; }
            if (remote && Peek() == ',')
            {
                Advance(); SkipTrivia();
                int? type = ReadInt();
                SkipTrivia();
                MmlLength? len = null; int? argByte = null;
                if (Peek() == ',')
                {
                    Advance(); SkipTrivia();
                    if (Peek() == '$') argByte = ReadHex();
                    else len = new MmlLength(ReadInt(), 0, null);
                    SkipTrivia();
                }
                if (type is null || Peek() != ')') { Error("Error parsing a remote code call.", l, cc); return; }
                Advance();
                into.Add(new RemoteCallCmd(l, cc, id.Value, type.Value, len, argByte));
                return;
            }
            if (Peek() != ')') { Error("Error parsing a loop label.", l, cc); return; }
            Advance();
            // "(1)[" with nothing between is a definition; "(1) [" is a call followed by an ordinary loop.
            if (Peek() == '[' && Peek(1) != '[')
            {
                Advance();
                var body = new List<Cmd>();
                ParseSequence(body, remote ? End.Remote : End.Loop);
                if (Eof) return;
                Advance();                              // ']'
                if (remote)
                {
                    foreach (var b in body)
                        if (b is NoteCmd or RestCmd or TieCmd) { Error("Remote definitions cannot contain note data!", b.Line, b.Column); return; }
                    into.Add(new RemoteDefCmd(l, cc, id.Value, body));
                    return;
                }
                int? count = ReadCount(l, cc, 1);
                if (count is null) return;
                into.Add(new LabelLoopCmd(l, cc, id.Value, body, count.Value));
                return;
            }
            if (remote) { Error("Error parsing a remote code definition.", l, cc); return; }
            int? calls = ReadCount(l, cc, 1);
            if (calls is not null) into.Add(new LabelCallCmd(l, cc, id.Value, calls.Value));
        }

        private void ParseHex(List<Cmd> into)
        {
            int l = line, cc = col;
            int? first = ReadHexToken();
            if (first is null || !HexParams.TryGetValue(first.Value, out int need)) { Error("Unknown hex command type.", l, cc); return; }
            var bytes = new List<byte> { (byte)first.Value };
            for (int i = 0; i < need || need < 0; i++)
            {
                SkipTrivia();
                if (Peek() != '$') { Error("Unknown hex command type.", l, cc); return; }
                int? v = ReadHexToken();
                if (v is null) { Error("Unknown hex command type.", l, cc); return; }
                bytes.Add((byte)v);
                if (need < 0 && bytes.Count == 3)                // $FB: the count, the length, then that many notes
                    need = bytes[1] < 0x80 ? 2 + bytes[1] : 2;
            }
            // The subcommands of $F4 and $FA that exist, and the echo delay $F1 can ask for before the buffer outgrows ARAM.
            if (bytes[0] == 0xF4 && bytes[1] is not (0 or 1 or 2 or 3 or 5 or 6 or 7 or 8 or 9)) { Error("Unknown $F4 hex command type.", l, cc); return; }
            if (bytes[0] == 0xFA && bytes[1] is not (0 or 1 or 2 or 3 or 4 or 6)) { Error("Unknown $FA hex command type.", l, cc); return; }
            if (bytes[0] == 0xF1 && bytes[1] > 0x0F) { Error("Echo buffer exceeded total space in ARAM.", l, cc); return; }
            into.Add(new HexCmd(l, cc, bytes.ToArray()));
        }

        private int? ReadHexToken()
        {
            if (Peek() != '$') return null;
            Advance();
            int start = pos;
            while (pos - start < 2 && Uri.IsHexDigit(Peek())) Advance();
            if (pos == start || Uri.IsHexDigit(Peek())) return null;
            return Convert.ToInt32(src.Substring(start, pos - start), 16);
        }

        /// <summary><c>"name=value"</c>: from here on, "name" in the text means "value".</summary>
        private void ParseReplacementDefinition()
        {
            int l = line, cc = col;
            var s = ReadQuoted();
            if (s is null) return;
            int eq = s.IndexOf('=');
            if (eq <= 0) { Error("Error parsing a replacement: it needs a name and an '='.", l, cc); return; }
            replacements[s[..eq]] = s[(eq + 1)..];
        }
    }
}
