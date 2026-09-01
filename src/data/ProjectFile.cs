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

    /// <summary>LM ExAnimation (reference/EXANIMATION.md): the encoded record per level (key =
    /// level hex, value = ExAnimation.Encode hex, alt-file index in its header) and the global
    /// list's. Source files 60-63 live in <see cref="Gfx"/> under their ids.</summary>
    public ExAnimationState ExAnimation { get; set; } = new();

    public sealed class ExAnimationState
    {
        public Dictionary<string, string> Levels { get; set; } = new();
        public string? Global { get; set; }
    }

    public sealed class BaseRomInfo
    {
        public string Sha256 { get; set; } = "";
        public int Size { get; set; }
        public string Title { get; set; } = "";
        /// <summary>RomPrep version applied to the base copy (0 = base used as provided,
        /// e.g. an LM-prepared ROM). When &gt; 0 the pinned hash is of the PREPPED image,
        /// and a shared .pdp can be satisfied by prepping any verified-vanilla ROM.</summary>
        public int PrepVersion { get; set; }
        /// <summary>What <see cref="RomPrep.StampSignature"/> was when this base was stamped.
        /// The version alone cannot tell a base prepped by today's build from one prepped by
        /// last week's build of the SAME version, and the difference is a fix that silently
        /// never reaches the game. Empty on a project written before this existed, which reads
        /// as "unknown" and re-preps once.</summary>
        public string PrepStamp { get; set; } = "";
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
        /// <summary>Main entrance / entry settings edit: the table bytes as hex (4, or 6 with
        /// Lunar Magic's method-2 bytes), or null
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
        /// <summary>An edited layer-2 background for this level, base64 of the 0x400 BG Map16 def
        /// indices. Independent of <see cref="Layer2Background"/>, which only says WHICH stream the
        /// level points at: this is the stream's contents.</summary>
        public string? BgTilemap { get; set; }
        /// <summary>An imported layer-3 tilemap for this level, base64 of the raw 16-bit map
        /// (LM's LT3 file shape), or null to use vanilla's (level mode, option) pick.</summary>
        public string? Layer3Tilemap { get; set; }
        /// <summary>LM's advanced layer-3 bypass for this level — the scroll and blend settings
        /// that would otherwise come from whatever the Layer 3 Option implies — or null to leave
        /// the base ROM's. <see cref="Layer3AdvancedOff"/> tells the two kinds of null apart.</summary>
        public Layer3.Advanced? Layer3Advanced { get; set; }
        /// <summary>True when this level's edit is "no advanced bypass", as opposed to "no edit".
        /// A null <see cref="Layer3Advanced"/> alone cannot say which, and turning the group off
        /// on a base ROM that has it on has to survive a save.</summary>
        public bool Layer3AdvancedOff { get; set; }

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
        /// <summary>LM's 32-row band (extended sprite list); 0 for everything above row 31.</summary>
        public int Band { get; set; }

        public static SpriteDto From(Sprite s) => new()
        {
            Screen = s.Screen, XNibble = s.XNibble, Y = s.Y,
            Extra = s.Extra, Number = s.Number, ExtraBytes = s.ExtraBytes, Band = s.Band,
        };

        public Sprite ToSprite() => new(Screen, XNibble, Y, Extra, Number, ExtraBytes, Band);
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
