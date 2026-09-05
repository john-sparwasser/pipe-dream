r"""
Rebuild src/data/Map16Tiles.json from Lunar Magic's own Map16 tile descriptions.

LM ships no data files: the sentence its status bar shows for a Map16 tile ("Direct Map16
Access Tile : %X") comes from a 0x200-entry pointer table in the exe's .data (0x5C3808 in
LM 3.x). Part of that table is filled at startup by compiler-generated initializers rather than
stored ready-made, so this walks those initializer functions (straight-line mov copies in
.text 0x172000-0x175000) and applies their stores before reading the table.

Tile descriptions in LM do not vary by tileset — a tile whose meaning changes shows as
"A tileset specific tile." — so every line lands in `all`. Per-tileset lines already in the
JSON (any key other than `all`) are kept as hand-edited overrides.

Run on the machine that has LM (needs capstone; uv fetches it):
    uv run --with capstone tools/lm/Extract-LmTileDescriptions.py
    uv run --with capstone tools/lm/Extract-LmTileDescriptions.py --exe "D:\LM\Lunar Magic.exe"
"""
import json, os, struct, sys
import capstone
from capstone import x86

EXE = os.environ.get('PIPEDREAM_LUNAR_MAGIC', r'C:\SMW\Projects\.resources\Lunar Magic\Lunar Magic.exe')
if '--exe' in sys.argv: EXE = sys.argv[sys.argv.index('--exe') + 1]
OUT = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'data', 'Map16Tiles.json')
BASE, TABLE, COUNT = 0x400000, 0x5C3808, 0x200      # ponytail: LM 3.x layout; re-find with a string xref if a new LM moves it
INIT_LO, INIT_HI = 0x572000, 0x575000                # the dynamic initializers that patch .data
RDATA_LO, DATA_HI = 0x575000, 0x5C5000

b = bytearray(open(EXE, 'rb').read())
if not (b[:2] == b'MZ' and struct.unpack_from('<I', b, struct.unpack_from('<I', b, 0x3C)[0] + 0x34)[0] == BASE):
    sys.exit('not a 32-bit PE at image base 0x400000: ' + EXE)
rd = lambda va: struct.unpack_from('<I', b, va - BASE)[0]   # sections are raw==virtual in this exe

def cstr(va):
    if not RDATA_LO <= va < DATA_HI: return None
    fo = va - BASE; e = b.find(b'\0', fo, fo + 1000)
    s = bytes(b[fo:e]) if e >= 0 else b''
    return s.decode("latin1").strip() if all(0x20 <= c < 0x7F for c in s) else None

# Replay `mov reg,[abs] / mov reg,imm / mov [abs],reg / mov [abs],imm` from the initializers onto .data.
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32); md.detail = True; md.skipdata = True
absmem = lambda op: op.type == x86.X86_OP_MEM and op.mem.base == op.mem.index == op.mem.segment == 0
regs = {}
for ins in md.disasm(bytes(b[INIT_LO - BASE:INIT_HI - BASE]), INIT_LO):
    if ins.id == 0: continue
    if ins.mnemonic in ('ret', 'int3'): regs = {}; continue
    if ins.mnemonic != 'mov' or len(ins.operands) != 2: continue
    d, s = ins.operands
    val = (s.imm if s.type == x86.X86_OP_IMM
           else rd(s.mem.disp) if absmem(s) and RDATA_LO <= s.mem.disp < DATA_HI
           else regs.get(s.reg) if s.type == x86.X86_OP_REG else None)
    if d.type == x86.X86_OP_REG: regs[d.reg] = val
    elif absmem(d) and d.size == 4 and val is not None and 0x5B7000 <= d.mem.disp < DATA_HI:
        struct.pack_into('<I', b, d.mem.disp - BASE, val)

old = json.load(open(OUT, encoding='utf8')) if os.path.exists(OUT) else {}
tiles = {}
for t in range(COUNT):
    text = cstr(rd(TABLE + 4 * t))
    if text is None: sys.exit('tile %03X: entry is not a string pointer; table layout changed?' % t)
    by = {'all': text}
    by.update({k: v for k, v in old.get('tiles', {}).get('%03X' % t, {}).get('actAsTilesets', {}).items() if k != 'all'})
    entry = dict(old.get('tiles', {}).get('%03X' % t, {}))    # hand-edited fields (`spawns`) survive too
    entry['actAsTilesets'] = by
    tiles['%03X' % t] = entry

doc = {'note': [
    "What a Map16 TILE is, by tileset, for the editor's readouts.",
    "",
    "Keys are hex Map16 tile numbers. Each carries `actAsTilesets`: a description per FG tileset",
    "id (hex, 0-F) plus `all`, the description for every tileset not named — a tileset's own",
    "entry overrides `all`.",
    "",
    "`all` is Lunar Magic's own sentence for the tile, read out of its exe by",
    "tools/lm/Extract-LmTileDescriptions.py (rerunning it rewrites every `all` line). LM does not",
    "describe tiles per tileset — it says \"A tileset specific tile.\" where the meaning changes —",
    "so the per-tileset lines are hand-edited and survive a rerun.",
    "",
    "`spawns` is the sprite (hex, SpriteDisplay.json numbering) the block releases when hit, for the",
    "Spawns overlay. Blocks whose contents depend on their X position name the first option. A",
    "custom tile borrows the `spawns` of whatever it acts as."],
    'tiles': tiles}
json.dump(doc, open(OUT, 'w', encoding='utf8'), indent=2, ensure_ascii=False)
open(OUT, 'a', encoding='utf8').write('\n')
print('%d tiles -> %s' % (len(tiles), os.path.normpath(OUT)))
