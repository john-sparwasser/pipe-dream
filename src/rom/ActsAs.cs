using System.Text.Json;

namespace PipeDream;

/// <summary>
/// What an acts-as code makes a tile DO, loaded from the embedded Map16ActsAs.json.
///
/// SMW stores no such table — behaviour is decided by comparisons scattered through the
/// collision code — so the JSON is a hand-edited reading of reference/smw-disasm, each entry
/// carrying the label it was read from. A code with no entry describes as nothing at all
/// rather than as a guess: the readout shows the bare number and the reader is none the wiser
/// about behaviour, which beats being told something untrue.
/// </summary>
public static class ActsAs
{
    private record Range(int From, int To, string Name);

    private static Dictionary<int, string>? codes;
    private static List<Range>? ranges;

    /// <summary>A short description of the behaviour, or "" when the table has nothing to say.</summary>
    public static string Describe(int code)
    {
        Load();
        if (codes!.TryGetValue(code, out var exact)) return exact;
        foreach (var r in ranges!)
            if (code >= r.From && code <= r.To) return r.Name;
        return "";
    }

    private static void Load()
    {
        if (codes is not null) return;
        codes = [];
        ranges = [];
        try
        {
            using var s = typeof(ActsAs).Assembly.GetManifestResourceStream("Map16ActsAs.json");
            if (s is null) return;
            using var r = new StreamReader(s);
            Parse(r.ReadToEnd());
        }
        catch { /* a malformed table costs the readout its words, not the editor its life */ }
    }

    internal static void Parse(string json)
    {
        codes = [];
        ranges = [];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("codes", out var cs))
            foreach (var p in cs.EnumerateObject())
                codes[Convert.ToInt32(p.Name, 16)] = p.Value.GetProperty("name").GetString() ?? "";
        if (doc.RootElement.TryGetProperty("ranges", out var rs))
            foreach (var e in rs.EnumerateArray())
                ranges.Add(new(Convert.ToInt32(e.GetProperty("from").GetString(), 16),
                               Convert.ToInt32(e.GetProperty("to").GetString(), 16),
                               e.GetProperty("name").GetString() ?? ""));
    }
}
