using System.Text.Json;
using System.Text.Json.Serialization;

namespace PipeDream;

/// <summary>
/// Serialized project state (project.pdp, JSON): a semantic SNAPSHOT of all edits
/// relative to the pinned base ROM — never ROM data. This is the shareable artifact:
/// a collaborator with a byte-identical base opens it and sees the same project.
///
/// Explicit DTOs, not the domain structs: LevelObject/Sprite are readonly structs whose
/// computed properties would serialize as noise and can't round-trip by reflection.
///
/// Key conventions: Map16 vanilla def slots are keyed by the def slot's SNES ADDRESS
/// (hex) — canonical across tilesets (tiles &lt; 0x200 alias per-tileset regions) and
/// stable across ROM expansion. Extended tiles (0x200+) are keyed by TILE NUMBER (hex)
/// because their region relocates on page allocation. Level keys are 3-digit hex.
/// </summary>
public sealed class ProjectFile
{
    public int SchemaVersion { get; set; } = 1;
    public BaseRomInfo BaseRom { get; set; } = new();
    public Map16State Map16 { get; set; } = new();
    public Dictionary<string, LevelState> Levels { get; set; } = new();
    /// <summary>Imported ExGFX files: file id hex ("100") → base64 raw planar bytes at the
    /// base ROM's bit depth. Project-scoped (shared by all levels); which slot uses a file
    /// stays per-level in <see cref="LevelState.GfxOverrides"/>.</summary>
    public Dictionary<string, string> Gfx { get; set; } = new();
    /// <summary>Display names for imported ExGFX: file id hex ("100") → name, defaulted from
    /// the imported filename. Kept beside <see cref="Gfx"/> rather than folded into it so an
    /// older .pdp (which has no names) still loads unchanged.</summary>
    public Dictionary<string, string> GfxNames { get; set; } = new();
    /// <summary>Edited secondary entrances: index hex ("0D4") → the 4 table bytes as hex.
    /// Project-scoped like Map16 — an entrance is a global record that any level's
    /// secondary exit can point at.</summary>
    public Dictionary<string, string> Entrances { get; set; } = new();
    /// <summary>Course Bot entries: level key hex ("105") → display name. A named handle on a
    /// level slot so courses are picked by name instead of number; the slot's content is an
    /// ordinary <see cref="Levels"/> entry. A separate map (the <see cref="GfxNames"/> pattern)
    /// so an older .pdp still loads unchanged.</summary>
    public Dictionary<string, string> CourseBot { get; set; } = new();

    public sealed class BaseRomInfo
    {
        public string Sha256 { get; set; } = "";
        public int Size { get; set; }
        public string Title { get; set; } = "";
        /// <summary>RomPrep version applied to the base copy (0 = base used as provided,
        /// e.g. an LM-prepared ROM). When &gt; 0 the pinned hash is of the PREPPED image,
        /// and a shared .pdp can be satisfied by prepping any verified-vanilla ROM.</summary>
        public int PrepVersion { get; set; }
    }

    public sealed class Map16State
    {
        /// <summary>Allocated Map16 tile count (page-granular); 0 = base ROM's count.</summary>
        public int TileCount { get; set; }
        /// <summary>Vanilla FG/BG def slots: SNES addr hex ("0D8000") → 8 bytes hex (TL,BL,TR,BR raw).</summary>
        public Dictionary<string, string> Slots { get; set; } = new();
        /// <summary>Extended defs (tiles 0x200+): tile hex → 8 bytes hex.</summary>
        public Dictionary<string, string> Ext { get; set; } = new();
        /// <summary>LM acts-as table values: tile hex → 14-bit acts value.</summary>
        public Dictionary<string, int> ActsAs { get; set; } = new();
    }

    public sealed class LevelState
    {
        /// <summary>Level header edit: the 5 replacement bytes as hex, or null to keep the
        /// base ROM's header.</summary>
        public string? Header { get; set; }
        /// <summary>Main entrance / entry settings edit: the 4 table bytes as hex, or null
        /// to keep the base ROM's.</summary>
        public string? MainEntrance { get; set; }
        public List<ObjectDto> Objects { get; set; } = new();
        /// <summary>Layer-2 object stream, or null to keep whatever the base ROM has. A
        /// non-null list also selects object mode: the build writes a real bank into the
        /// layer-2 pointer, and the mode IS that bank ($FF = background image). So an empty
        /// list converts a background-image level to an empty object layer, and clearing it
        /// back to null restores the base ROM's background.</summary>
        public List<ObjectDto>? Layer2Objects { get; set; }
        /// <summary>Layer 2 as a BACKGROUND IMAGE: the stream's low 16 bits (the bank is
        /// always $FF, which is what selects background mode). null = keep the base ROM's
        /// layer 2. Mutually exclusive with <see cref="Layer2Objects"/> — a level's layer 2
        /// is one or the other — and this wins if both are somehow set.
        ///
        /// Only an address the ROM already uses is offered, because a background's page byte
        /// comes from its address ($E8FE and up = page 1), so a stream cannot be relocated
        /// without recolouring every tile in it.</summary>
        public int? Layer2Background { get; set; }
        public int SpriteMemory { get; set; }
        public int Buoyancy { get; set; }
        public List<SpriteDto> Sprites { get; set; } = new();
        /// <summary>Palette edits: CGRAM index → BGR555 word.</summary>
        public Dictionary<int, int> Palette { get; set; } = new();
        /// <summary>GFX slot overrides: bypass word index (0-15) → GFX file id.</summary>
        public Dictionary<int, int> GfxOverrides { get; set; } = new();

        /// <summary>Deep copy, via the same JSON round-trip the file itself makes.</summary>
        public LevelState Clone() =>
            JsonSerializer.Deserialize<LevelState>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
    }

    public sealed class ObjectDto
    {
        public bool NewScreen { get; set; }
        public int Number { get; set; }
        public int Screen { get; set; }
        public int XNibble { get; set; }
        public int Y { get; set; }
        public int Byte3 { get; set; }
        public int ExtraByte { get; set; } = -1;
        public int Dm16Tile { get; set; } = -1;
        public int Dm16Page { get; set; } = -1;
        public int Dm16ExtX { get; set; } = -1;
        public int Dm16ExtH { get; set; } = -1;

        public static ObjectDto From(LevelObject o) => new()
        {
            NewScreen = o.NewScreen, Number = o.Number, Screen = o.Screen, XNibble = o.XNibble,
            Y = o.Y, Byte3 = o.Byte3, ExtraByte = o.ExtraByte, Dm16Tile = o.Dm16Tile,
            Dm16Page = o.Dm16Page, Dm16ExtX = o.Dm16ExtX, Dm16ExtH = o.Dm16ExtH,
        };

        public LevelObject ToLevelObject() =>
            new(NewScreen, Number, Screen, XNibble, Y, Byte3, ExtraByte, Dm16Tile,
                Dm16Page, Dm16ExtX, Dm16ExtH);
    }

    public sealed class SpriteDto
    {
        public int Screen { get; set; }
        public int XNibble { get; set; }
        public int Y { get; set; }
        public int Extra { get; set; }
        public int Number { get; set; }
        public byte[]? ExtraBytes { get; set; }

        public static SpriteDto From(Sprite s) => new()
        {
            Screen = s.Screen, XNibble = s.XNibble, Y = s.Y,
            Extra = s.Extra, Number = s.Number, ExtraBytes = s.ExtraBytes,
        };

        public Sprite ToSprite() => new(Screen, XNibble, Y, Extra, Number, ExtraBytes);
    }

    /// <summary>Level state for a level number, created on first touch.</summary>
    public LevelState Level(int levelNum)
    {
        string key = levelNum.ToString("X3");
        if (!Levels.TryGetValue(key, out var s)) Levels[key] = s = new LevelState();
        return s;
    }

    public LevelState? LevelOrNull(int levelNum) =>
        Levels.TryGetValue(levelNum.ToString("X3"), out var s) ? s : null;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static ProjectFile FromJson(string json) =>
        JsonSerializer.Deserialize<ProjectFile>(json, JsonOpts)
        ?? throw new InvalidDataException("empty project file");
}
