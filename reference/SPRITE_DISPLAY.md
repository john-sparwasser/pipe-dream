# SpriteDisplay.json — sprite display spec

`src/data/SpriteDisplay.json` (embedded resource, logical name `SpriteDisplay.json`)
defines how every sprite is DISPLAYED in the editor: the level canvas overlay, the
Sprites-tab catalog thumbnails, and the "Loaded only" GFX filter. It is designed to be
**hand-edited** — fix a sprite's tiles by eye, add a hitbox, rename — and is loaded by
`SpriteDisplay` (src/rom/SpriteDisplay.cs).

To fix a sprite by eye, launch with `--dev` and use **Debug ▸ Sprite tiles…**: pick tiles
off the SP sheets, drag them into place, Save. It rewrites this file in the source tree (same
layout, so the diff is only the changed tiles) and swaps the table in live.

Regenerate from a clean ROM with:

    PipeDream --gen-spritedisplay <cleanRom> src\Data\SpriteDisplay.json

**WARNING**: regeneration overwrites hand edits. The file is in git — review the diff
and re-apply/keep what you want before committing.

## Format

```json
{
  "_spec": "…",
  "sprites": {
    "05": {
      "name": "Red Koopa",
      "tiles": [
        { "x": -1, "y": -15, "tile": "0x082", "pal": 3, "size": 16 },
        { "x": -1, "y": 1,   "tile": "0x0A2", "pal": 3, "size": 16, "xflip": true }
      ],
      "hitbox": { "x": 2, "y": 3, "w": 12, "h": 10 },
      "gfx": { "1": ["0x01"] }
    }
  }
}
```

Sprite keys are the **sprite-list numbers** in hex: `00`-`C8` are regular sprites,
`DA`-`DD`/`DF` are the stationary koopa shells (the game remaps them to sprite
`(num-$DA)+4` at status 9, CONTRACT §14).

### `name`
Display name (shown in the catalog). Free text.

### `tiles` — the display tiles, drawn in array order (later entries BEHIND earlier)
| field  | meaning |
|--------|---------|
| `x`,`y`  | pixel offset of the tile's top-left from the sprite's **placement cell** top-left (can be negative — heads extend above the cell) |
| `tile` | 9-bit VRAM tile index, hex string. Slot mapping: `0x000-0x07F` SP1, `0x080-0x0FF` SP2, `0x100-0x17F` SP3, `0x180-0x1FF` SP4 (each slot = one GFX file, 128 8x8 tiles, 16 per row) |
| `pal`  | sprite palette 0-7 → CGRAM row `8+pal` (colors `0x80+pal*16`…) |
| `size` | 8 or 16. A 16x16 tile assembles from VRAM tiles T, T+1, T+16, T+17 |
| `xflip`,`yflip` | optional, default false. Flips mirror the whole 16x16 assembly (quadrants swap) |

To find tile indexes by eye: View → Level GFX shows the 4 SP sheets for the current
level; a tile's index is `slot*0x80 + row*16 + column` within its sheet.

### `hitbox` — sprite↔sprite clipping box, pixels relative to the placement cell
Generated from the ROM: `GetSpriteClippingA` ($03B69F) reads displacement/size tables
`$03B56C` (x, signed), `$03B5E4` (y, signed), `$03B5A8` (w), `$03B620` (h), indexed by
tweaker `$1662 & 0x3F`. Optional; hand-tune freely (display-only for now).

### `gfx` — per-SP-slot GFX file requirements ("Loaded only" filter)
Keys are slot indexes `"0"`-`"3"` (SP1-SP4); values are the GFX file numbers (hex
strings) that satisfy the slot. A sprite is "loaded" in a level when, for every listed
slot, the level's resolved file (SPRITEGFXLIST + Super GFX Bypass) is in the list.
Slots the sprite's tiles don't touch are omitted. Derived by scanning all 512 vanilla
levels: wherever a sprite appears, the files loaded in its used slots are compatible.
The generator seeds it that way; after that it is a hand-kept setting. In Debug ▸ Sprite tiles
a ticked SP slot is a needed one, and Save writes the file on show for it (keeping a vanilla
list whole when that file is already in it). The "show" dropdown views a sprite against its
own files, the level's, or any of the 16 sprite GFX sets without changing what is saved.

## What is NOT here
Pixels and colors: tiles resolve against the CURRENT level's SP GFX files and palette
at draw time, so the same entry looks right (or honestly wrong) per level. PIXI custom
sprites (extra bits 2/3) bypass this table and use live 65816 capture.
