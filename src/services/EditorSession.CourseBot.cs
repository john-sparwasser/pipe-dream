namespace PipeDream.Services;

// EditorSession — Course Bot: named handles on level slots. The rest of the class:
// EditorSession.cs and the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- course bot ----
    // Named handles on level slots, so courses are organized by name instead of number. An
    // entry is an ordinary project level whose slot was auto-picked and seeded by copying a
    // base level; only the name (ProjectFile.CourseBot) is new state.

    /// <summary>Overworld-enterable level slots — the pool Course Bot assigns from and
    /// offers as bases.</summary>
    public static IEnumerable<int> EnterableLevels()
    {
        for (int l = 0x001; l <= 0x024; l++) yield return l;
        for (int l = 0x101; l <= 0x13B; l++) yield return l;
    }

    /// <summary>Course Bot entries, sorted by name.</summary>
    public IReadOnlyList<(int Level, string Name)> CourseBotEntries =>
        Project is null ? []
        : Project.Data.CourseBot
            .Select(kv => (Level: Convert.ToInt32(kv.Key, 16), kv.Value))
            .OrderBy(e => e.Value, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Level)
            .ToList();

    /// <summary>The course name a level slot carries, or null.</summary>
    public string? CourseBotName(int level) =>
        Project?.Data.CourseBot.GetValueOrDefault(level.ToString("X3"));

    /// <summary>
    /// Whether a slot can take a new course. A project entry does not by itself mean "used":
    /// every save stashes whichever level is being shown, so a merely-visited level carries an
    /// entry identical to its base ROM parse — that slot is still free.
    /// </summary>
    private bool SlotIsFree(int level)
    {
        var data = Project!.Data;
        if (data.CourseBot.ContainsKey(level.ToString("X3"))) return false;
        if (data.LevelOrNull(level) is not { } s) return true;
        if (s.Header is not null || s.MainEntrance is not null || s.Layer2Objects is not null
            || s.Layer2Background is not null || s.Palette.Count > 0 || s.GfxOverrides.Count > 0)
            return false;
        if (!s.Objects.Select(o => o.ToLevelObject())
                      .SequenceEqual(LevelParser.Parse(Rom!, level).Objects)) return false;
        var sd = SpriteData.Parse(Rom!, level);
        // Sprite.ExtraBytes is an array, so records carrying them never compare equal — that
        // only ever calls a free slot "used", never the reverse.
        return s.SpriteMemory == sd.SpriteMemory && s.Buoyancy == sd.Buoyancy
            && s.Sprites.Select(x => x.ToSprite()).SequenceEqual(sd.Sprites);
    }

    /// <summary>
    /// Create a Course Bot level: the first free enterable slot, seeded with a FULL copy of
    /// <paramref name="baseLevel"/> — header, main entrance, both layers, sprites, palette and
    /// GFX bins — so the slot's build output is determined entirely by its project entry.
    /// Returns the new slot, or -1 with the reason in Status.
    /// </summary>
    public int CreateCourseBotLevel(string name, int baseLevel)
    {
        if (Project is null || Rom is null) { Report("no project open"); return -1; }
        name = name.Trim();
        if (name.Length == 0) { Report("a course needs a name"); return -1; }
        StashCurrent();                       // a copy of the shown level must be fresh
        // The shown level is never the slot: its next stash would overwrite the copy with
        // whatever is on screen.
        int slot = EnterableLevels().Where(l => l != LevelNum && SlotIsFree(l))
                                    .DefaultIfEmpty(-1).First();
        if (slot < 0) { Report("no free enterable level slot left"); return -1; }

        var data = Project.Data;
        var state = FullCopyOf(baseLevel);

        string key = slot.ToString("X3");
        data.Levels[key] = state;
        data.CourseBot[key] = name;

        // Seed the session ROM the way Hydrate would on reopen, so the slot shows the copy
        // right away — and so the save-time entrance re-read (ProjectCapture) captures the
        // copied bytes rather than the slot's base ones.
        Rom.LevelHeaderOverrides[slot] = Convert.FromHexString(state.Header!);
        foreach (var (word, file) in state.GfxOverrides) Rom.GfxSlotOverrides[(slot, word)] = file;
        Rom.WriteMainEntrance(slot, new MainEntrance(Convert.FromHexString(state.MainEntrance!)));
        if (state.Layer2Background is { } bg) Rom.SetLayer2Pointer(slot, 0xFF0000 | bg);

        Project.MarkDirty();
        Report($"course \"{name}\" created in level ${slot:X3} from ${baseLevel:X3}");
        return slot;
    }

    /// <summary>A project entry holding everything that makes <paramref name="baseLevel"/> what
    /// it is, so a slot seeded from it builds identically.</summary>
    private ProjectFile.LevelState FullCopyOf(int baseLevel)
    {
        var data = Project!.Data;
        // Start from the base's project entry when it has one (its object/sprite edits live
        // only there), then fill the rest from the session ROM, whose reads already merge the
        // session edits (header overrides, replayed entrance tables).
        var state = data.LevelOrNull(baseLevel)?.Clone() ?? new ProjectFile.LevelState();
        var parsed = LevelParser.Parse(Rom!, baseLevel);
        state.Header = Convert.ToHexString(parsed.Header.ToBytes());
        if (data.LevelOrNull(baseLevel) is null)
            state.Objects = parsed.Objects.Select(ProjectFile.ObjectDto.From).ToList();
        // Layer 2 is recorded EXPLICITLY either way: null in the base's entry means "keep the
        // base ROM's layer 2", and the new slot's own base layer 2 is a different one.
        if (state.Layer2Objects is null && state.Layer2Background is null)
        {
            if (Rom!.Layer2IsBackground(baseLevel))
                state.Layer2Background = Rom.Layer2Pointer(baseLevel) & 0xFFFF;
            else
                state.Layer2Objects = LevelParser.ParseLayer2(Rom, baseLevel)!
                    .Select(ProjectFile.ObjectDto.From).ToList();
        }
        if (state.Sprites.Count == 0 && state.SpriteMemory == 0 && state.Buoyancy == 0)
        {
            // ponytail: all-defaults reads as "never stashed"; a base deliberately emptied of
            // sprites AT memory setting 0 copies the ROM's list instead — harmless to re-delete.
            var sd = SpriteData.Parse(Rom!, baseLevel);
            state.SpriteMemory = sd.SpriteMemory;
            state.Buoyancy = sd.Buoyancy;
            state.Sprites = sd.Sprites.Select(ProjectFile.SpriteDto.From).ToList();
        }
        state.MainEntrance = Convert.ToHexString(Rom!.ReadMainEntrance(baseLevel).ToBytes());
        state.GfxOverrides = Rom.GfxSlotOverrides.Where(kv => kv.Key.Level == baseLevel)
                                .ToDictionary(kv => kv.Key.Word, kv => kv.Value);
        return state;
    }

    /// <summary>
    /// Delete a Course Bot level: the name goes and the slot's project entry goes with it, so
    /// the slot reverts to the base ROM. The per-slot bytes create wrote into the session ROM
    /// (entrance table, layer-2 pointer) are restored from the base copy — a build replays
    /// onto a fresh base anyway, this just keeps what is on screen honest.
    /// </summary>
    public string DeleteCourseBotLevel(int level)
    {
        if (Project is null || Rom is null) { Report("no project open"); return Status; }
        string key = level.ToString("X3");
        if (!Project.Data.CourseBot.Remove(key))
        {
            Report($"${level:X3} is not a Course Bot level");
            return Status;
        }
        Project.Data.Levels.Remove(key);
        Rom.LevelHeaderOverrides.Remove(level);
        foreach (var k in Rom.GfxSlotOverrides.Keys.Where(k => k.Level == level).ToArray())
            Rom.GfxSlotOverrides.Remove(k);
        var baseRom = Rom.Load(Project.BaseRomPath);
        Rom.WriteMainEntrance(level, baseRom.ReadMainEntrance(level));
        Rom.SetLayer2Pointer(level, baseRom.Layer2Pointer(level));
        Project.MarkDirty();
        touched.Remove(level);
        // Same number, so ShowLevel does not stash the dying state on the way out.
        if (level == LevelNum) ShowLevel(level);
        Report($"course level ${level:X3} deleted — slot reverted to the base ROM");
        return Status;
    }
}
