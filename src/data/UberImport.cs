using System.Globalization;
using System.Text;

namespace PipeDream;

/// <summary>
/// UberASM Tool's folder and <c>list.txt</c> into an <see cref="UberState"/> (reference/UBERASM_PORT.md, phase 1). The list is
/// read as 2.1 reads it, and a 1.x list too: sections <c>level:</c>, <c>gamemode:</c>, <c>overworld:</c>; lines
/// <c>number|* file [: bytes] [, file …]</c> (a line ending in a comma goes on); <c>-file</c> skips a <c>*</c> resource; extra
/// bytes hex by default, <c>$</c>/<c>0x</c> hex, <c>@</c> decimal, <c>%</c> binary, negative, <c>(word)</c> and <c>[long]</c>
/// little-endian; comments <c>;</c> and <c>#</c>. Written from the readmes, not the tool's source.
/// </summary>
internal static class UberImport
{
    public sealed record Folder(UberState Uber, List<string> Problems);

    public const string ListFile = "list.txt";

    /// <summary>The folders UberASM Tool reads users' files from; <c>asm/</c> is the tool's own and is left behind.</summary>
    private static readonly string[] UserFolders = ["level", "gamemode", "overworld", "library", "routines", "other"];

    public static Folder ReadFolder(string dir)
    {
        var problems = new List<string>();
        var uber = new UberState();
        if (AmkImport.FindFile(dir, ListFile) is not { } list) throw new FileNotFoundException(ListFile);
        foreach (string folder in UserFolders)
            foreach (var (key, path) in AmkImport.Tree(dir, folder))
                uber.Files[$"{folder}/{key}"] = File.ReadAllBytes(path);
        ParseList(File.ReadAllText(list), uber, problems);
        foreach (var e in uber.Levels.Concat(uber.GameModes).Concat(uber.Overworlds))
            foreach (var c in e.Calls.Where(c => !uber.Files.ContainsKey(c.File)))
                problems.Add($"{c.File} is in {ListFile} but not in the folder");
        return new(uber, problems);
    }

    /// <summary>Read <paramref name="text"/> into <paramref name="uber"/>'s list and settings.</summary>
    public static void ParseList(string text, UberState uber, List<string> problems)
    {
        List<UberEntry>? section = null;
        string folder = "";
        UberEntry? open = null;                             // a line that ended in a comma
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            string line = raw;
            int comment = line.IndexOfAny([';', '#']);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (open is null && Command(line) is var (cmd, value))
            {
                switch (cmd)
                {
                    case "level": section = uber.Levels; folder = "level"; break;
                    case "gamemode": section = uber.GameModes; folder = "gamemode"; break;
                    case "overworld": section = uber.Overworlds; folder = "overworld"; break;
                    case "global": uber.Global = Path("", value); break;
                    case "statusbar": uber.StatusBar = Path("", value); break;
                    case "macrolib": uber.MacroLib = Path("", value); break;
                    case "freeram" or "sprite":                  // 1.x's sprite: is 2.x's freeram: (2.1 readme, Quick Start)
                        if (Number(value) is { } ram) uber.FreeRam = ram;
                        else problems.Add($"{ListFile} line {n}: \"{value}\" is not a RAM address");
                        break;
                    case "sprite-sa1" or "rom" or "verbose" or "deprecations": break;
                    default: problems.Add($"{ListFile} line {n}: unknown command \"{cmd}:\""); break;
                }
                continue;
            }
            if (section is null) { problems.Add($"{ListFile} line {n}: a resource before level:, gamemode: or overworld:"); continue; }

            string rest = line;
            if (open is null)
            {
                int space = line.IndexOfAny([' ', '\t']);
                string number = space < 0 ? line : line[..space];
                rest = space < 0 ? "" : line[(space + 1)..];
                int max = folder switch { "level" => 0x1FF, "gamemode" => 0xFF, _ => 6 };
                int? num = number == "*" ? UberEntry.All
                    : int.TryParse(number, NumberStyles.HexNumber, null, out int v) && v <= max ? v : null;
                if (num is null) { problems.Add($"{ListFile} line {n}: \"{number}\" is not a {folder} number"); continue; }
                open = section.Find(e => e.Number == num) ?? new UberEntry { Number = num.Value };
                if (!section.Contains(open)) section.Add(open);
            }
            bool goesOn = rest.TrimEnd().EndsWith(',');
            foreach (string part in rest.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
                if (Call(part, folder, n, problems) is { } call) open.Calls.Add(call);
            if (!goesOn) open = null;
        }
    }

    /// <summary><c>name: value</c> for a command or section header; null for a resource line (whose file may hold a colon only
    /// before its extra bytes, which follow a file name, so a line starting with a number or <c>*</c> is never a command).</summary>
    private static (string Cmd, string Value)? Command(string line)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) return null;
        string cmd = line[..colon].Trim().ToLowerInvariant();
        if (!char.IsLetter(cmd[0]) || !cmd.All(c => char.IsLetterOrDigit(c) || c == '-')) return null;   // "sprite-sa1"; never "105 …"
        return (cmd, line[(colon + 1)..].Trim());
    }

    /// <summary><c>[-]file [: bytes]</c>.</summary>
    private static UberCall? Call(string part, string folder, int n, List<string> problems)
    {
        var call = new UberCall();
        if (part.StartsWith('-')) { call.Skip = true; part = part[1..].TrimStart(); }
        int colon = part.IndexOf(':');
        string file = (colon < 0 ? part : part[..colon]).Trim();
        call.File = Path(folder, file);
        if (!AmkImport.SafePath(call.File)) { problems.Add($"{ListFile} line {n}: \"{file}\" is not a file inside the folder"); return null; }
        if (colon >= 0)
        {
            var bytes = new List<byte>();
            foreach (string token in part[(colon + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                int width = token.StartsWith('(') && token.EndsWith(')') ? 2 : token.StartsWith('[') && token.EndsWith(']') ? 3 : 1;
                string inner = width == 1 ? token : token[1..^1];
                if (Value(inner) is not { } v) { problems.Add($"{ListFile} line {n}: \"{token}\" is not an extra byte"); return null; }
                for (int i = 0; i < width; i++) bytes.Add((byte)(v >> (8 * i)));
            }
            call.Bytes = [.. bytes];
        }
        return call;
    }

    /// <summary>An extra byte's value: hex by default, <c>$</c> or <c>0x</c> hex, <c>@</c> decimal, <c>%</c> binary, a leading
    /// <c>-</c> for negative.</summary>
    private static long? Value(string s)
    {
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        long? v = s switch
        {
            ['@', .. var d] when long.TryParse(d, out long x) => x,
            ['%', .. var b] when b.Length > 0 && b.All(c => c is '0' or '1') => Convert.ToInt64(b, 2),
            ['$', .. var h] => Hex(h),
            _ when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => Hex(s[2..]),
            _ => Hex(s),
        };
        return negative ? -v : v;

        static long? Hex(string h) => long.TryParse(h, NumberStyles.HexNumber, null, out long x) ? x : null;
    }

    /// <summary>A RAM address as the list gives it (<c>$7FAC80</c>, <c>7FAC80</c>, <c>0x7FAC80</c>).</summary>
    private static int? Number(string s)
    {
        s = s.Trim();
        if (s.StartsWith('$')) s = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, NumberStyles.HexNumber, null, out int v) ? v : null;
    }

    /// <summary>A list file name as a path in <see cref="UberState.Files"/>: relative to its section's folder, "../" resolved
    /// ("../overworld/music.asm" from level/ is "overworld/music.asm").</summary>
    private static string Path(string folder, string file)
    {
        var parts = new List<string>(folder.Length > 0 ? [folder] : []);
        foreach (string p in file.Replace('\\', '/').Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else parts.Add(p);
        }
        return string.Join('/', parts);
    }

    /// <summary>A line's calls as the list writes them: <c>[-]file [: bytes]</c>, comma separated, each file relative to the
    /// section's folder (<paramref name="kind"/>: level, gamemode or overworld).</summary>
    public static string FormatCalls(string kind, IEnumerable<UberCall> calls)
    {
        string folder = kind + "/";
        return string.Join(", ", calls.Select(c => (c.Skip ? "-" : "") + (c.File.StartsWith(folder) ? c.File[folder.Length..] : "../" + c.File)
            + (c.Bytes.Length > 0 ? " : " + string.Join(' ', c.Bytes.Select(b => b.ToString("X2"))) : "")));
    }

    /// <summary>The list as UberASM Tool 2.1 reads it, for writing a folder back out.</summary>
    public static string FormatList(UberState uber)
    {
        var sb = new StringBuilder();
        foreach (var (name, entries) in new[] { ("level", uber.Levels), ("overworld", uber.Overworlds), ("gamemode", uber.GameModes) })
        {
            sb.Append(name).Append(":\r\n");
            foreach (var e in entries)
            {
                sb.Append(e.Number == UberEntry.All ? "*" : e.Number.ToString(name == "level" ? "X3" : name == "gamemode" ? "X2" : "X"));
                sb.Append(' ').Append(FormatCalls(name, e.Calls)).Append("\r\n");
            }
            sb.Append("\r\n");
        }
        sb.Append($"global:\t\t{uber.Global}\r\nstatusbar:\t{uber.StatusBar}\r\nmacrolib:\t{uber.MacroLib}\r\nfreeram:\t${uber.FreeRam:X6}\r\n");
        return sb.ToString();
    }
}
