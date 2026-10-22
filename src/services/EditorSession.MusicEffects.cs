using PipeDream.Music;

namespace PipeDream.Services;

// EditorSession — the project's sound effects (ProjectFile.Music): the $1DF9 and $1DFC lists and their
// sources, as AddmusicK keeps them. A slot the project fills replaces SMW's effect there; the rest stay
// SMW's (MusicInsert). Songs and samples are in EditorSession.Music.cs.
public sealed partial class EditorSession
{
    /// <summary>One effect as the Music window lists it: its port ("1DF9" or "1DFC"), the slot (the value
    /// written there), the list's flag and the file, and what it compiles to.</summary>
    public sealed record EffectInfo(string Port, int Slot, string Flag, string File, string Size);

    public IReadOnlyList<EffectInfo> MusicEffects
    {
        get
        {
            if (Project is null) return [];
            var m = Project.Data.Music;
            return [.. Ports(m).SelectMany(p => p.Slots.Select(s => new EffectInfo(p.Port, s.Slot, s.Flag, s.File, EffectSize(m, p.Port, s))))];
        }
    }

    private static string EffectSize(MusicState m, string port, MusicState.SfxSlot s)
    {
        if (s.Flag == "*") return "shares";
        if (!m.SfxSources.TryGetValue($"{port}/{s.File}", out var text)) return "missing";
        var (bytes, problems) = SfxCompiler.Compile(text, s.Flag == "?");
        return problems.Count > 0 ? "error" : $"{bytes.Length:N0} bytes";
    }

    public string? EffectText(string port, string file) => Project?.Data.Music.SfxSources.GetValueOrDefault($"{port}/{file}");

    public void SetEffectText(string port, string file, string text)
    {
        var m = Project?.Data.Music;
        if (m is null || !m.SfxSources.TryGetValue($"{port}/{file}", out var old) || old == text) return;
        m.SfxSources[$"{port}/{file}"] = text;
        Report(Dirty2($"saved {port}/{file}"));
    }

    /// <summary>Put an effect file from disk in a slot of $1DF9 or $1DFC, replacing what the slot held.</summary>
    public string ImportEffectFile(string path, string port, int slot)
    {
        if (Project is null) return Report2("no project open");
        if (port is not ("1DF9" or "1DFC")) return Report2("an effect goes in 1DF9 or 1DFC");
        if (slot is < 1 or > 0xFF) return Report2("an effect's slot is 01-FF");
        string? text = null;
        if (!Guard("read the effect", () => text = File.ReadAllText(path), path)) return Status;
        var m = Project.Data.Music;
        string file = Path.GetFileName(path);
        m.SfxSources[$"{port}/{file}"] = text!;
        var slots = Slots(m, port);
        slots.RemoveAll(s => s.Slot == slot);
        slots.Add(new MusicState.SfxSlot { Slot = slot, File = file });
        slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return Report2(Dirty2($"{port} {slot:X2} is {file}"));
    }

    /// <summary>Empty a slot (SMW's effect plays there again). The source goes too once no slot names it.</summary>
    public string DeleteEffect(string port, int slot)
    {
        if (Project is null) return Report2("no project open");
        var m = Project.Data.Music;
        var slots = Slots(m, port);
        if (slots.Find(s => s.Slot == slot) is not { } gone) return Report2($"{port} {slot:X2} is empty");
        slots.Remove(gone);
        if (!slots.Any(s => s.File == gone.File)) m.SfxSources.Remove($"{port}/{gone.File}");
        return Report2(Dirty2($"emptied {port} {slot:X2}"));
    }

    private static List<MusicState.SfxSlot> Slots(MusicState m, string port) => port == "1DF9" ? m.Sfx1DF9 : m.Sfx1DFC;

    private static (string Port, List<MusicState.SfxSlot> Slots)[] Ports(MusicState m) => [("1DF9", m.Sfx1DF9), ("1DFC", m.Sfx1DFC)];
}
