# Lunar Magic's UI, swept against ours  [2026-09-11]

What Lunar Magic exposes, and whether pipe-dream has it. The point is triage: turn "unknown
surface" into a list you can pick from, so the next parity item is chosen rather than guessed.

**Method.** LM's side is its own help index (`reference/lm-help/html/`, 270 topics — the complete
feature surface, not a guess). Ours is the named controls and menu headers in
`src/ui/MainWindow.axaml` plus `reference/CONTRACT.md`'s implemented sections, with the uncertain
items resolved by grepping `src/` for the feature. Where a row says **missing**, it means nothing
in `src/` mentions the feature at all — those were checked, not assumed.

**Status.** `done` = usable in our UI · `partial` = the ROM side is decoded and/or some of the UI
exists · `missing` = not implemented · `n/a` = belongs to LM's ROM-editing workflow, not ours
(we have a project model instead).

Trivia is collapsed: clipboard, undo/redo, z-order, zoom, selection and the like all work through
our canvas and gesture layer (`Gestures.cs`, `Overlay.cs`) and are not listed item by item.

---

## Level editor

| LM | status | notes |
|---|---|---|
| Objects: insert/edit/size/layer (`edit_*`, `editor_objects`) | done | canvas modes, `ObjectPanel` |
| Change Properties / Graphics / Sprite / Other (`level_change_*`) | done | `LevelPropertiesWindow` |
| Screen exits (`level_screen_exit`) | done | `Screen exits…`, plus exit-destination bit 8 (§9d-2) |
| Scan for exits (`level_scan_exits`) | missing | a utility over the same data we already parse |
| Main / secondary entrances (`level_main_entrance`, `level_second_entrance`) | partial | ROM side complete (v10/v21/v22, `MainEntrance.cs`); UI is the Entrances canvas mode, no dialog |
| Super GFX Bypass (`level_super_bypass`) | done | drawer GFX slots; LM's dialog round-trips (v20) |
| FG/SP GFX bypass (`level_bypass_fg`, `level_bypass_sp`) | done | same records |
| **Music bypass** (`level_bypass_music`) | **missing** | nothing in `src/` addresses music |
| Layer 3 GFX + settings (`level_layer3_*`) | done | `Layer 3 Options`, prep v14/v15/v16 |
| Map16 for BG (`level_map16_bg`) | partial | BG mode renders it; no dedicated editor |
| ExAnimation (`level_extend_ani`, `level_ex20*`) | done | Anim mode, prep v11/v17 |
| BG offset / copy BG (`level_bg_offset`, `level_copy_bg`) | partial | Bg mode has tilemaps + palette, not the offset dialog |

## Editor windows

| LM | status |
|---|---|
| 16x16 Tile Map (`editor_16x16`) | done — Map16 mode, incl. acts-like |
| 8x8 Tile (`editor_8x8`) | done — Gfx mode |
| Palette (`editor_palette`) | done — Palette mode |
| Background (`editor_back`) | done — Bg mode |
| Sprites (`editor_sprites`) | done — sprite list + `Sprite data…` |
| Overworld (`editor_ov`) | partial — see below |

## Overworld — where most of the gaps are

| LM | status | notes |
|---|---|---|
| Layer 1/2 tile editing, paths (`ov_edit_*`, `ov_view_layer1_paths`) | partial | view overlays + the Tiles canvas; LM's full edit set is larger |
| Submap GFX (`ov_overworld_super_bypass`) | done | prep v18/v19, LM's dialog round-trips |
| Submap Layer 3 GFX + tilemap (`ov_overworld_layer3_gfx`) | done | GFX free with v14/v18; tilemap is prep v29 |
| Events (`ov_editor_event`, `ov_edit_layer1_event`, `ov_view_event_number`) | partial | event numbers shown; no event editor |
| Star/pipe/exit links, teleports (`ov_overworld_exitstar_link`, `*teleport`) | partial | `Overworld.Transitions.cs` reads them; badges shown |
| Modify/destroy level tile settings (`ov_overworld_modify_level`, `_destroy_level`) | partial | `OwEditBtn` |
| **Level names** (`ov_overworld_level_names`) | **missing** | |
| **Message box text** (`ov_overworld_message_box`) | **missing** | |
| **Boss sequence text** (`ov_overworld_boss_text`) | **missing** | |
| **Submap music selection** (`ov_overworld_music`) | **missing** | |
| **Overworld sprite list** (`ov_overworld_sprite_list`) | **missing** | level sprites only |
| **Reveal tile list** (`ov_overworld_reveal_list`) | **missing** | |
| **Events passed / max 6x6 area** (`ov_overworld_passed`, `_max_6x6`) | **missing** | |
| **Boss / star / switch / no-auto-move level lists** | **missing** | four separate dialogs |
| **Title screen + credits + recordings** (`ov_file_load_title_screen`, `_load_credits`, `_install_recording`) | **missing** | |
| Layer 3 of level/submap load+save (`ov_file_load_layer3_*`) | partial | prep v29 loads it in game; no file import/export |
| Overworld ExAnimation (`ov_overworld_ex20*`) | done | prep v17 |

## File

| LM | status | notes |
|---|---|---|
| Open ROM / level / next / previous / recent | n/a | we open a project, not a ROM |
| Save level / save as / save to directory | n/a | project model |
| **MWL level import/export** (`file_open_mwl`, `file_save_mwl`) | **missing** | LM's level interchange format, and its `-ExportLevel`/`-ImportLevel` CLI — the most interop-relevant gap here |
| Insert/extract GFX + ExGFX (`file_insert_gfx`, `file_extract_exgfx`, …) | partial | Gfx mode imports/exports single files; no bulk folder round-trip like LM's |
| Insert/extract/import/export **palette files** | missing | nothing handles `.pal` |
| Insert/extract **bypass** files (`file_*_bypass`) | missing | |
| Expand ROM (`file_expand_rom`) | partial | `RomBuilder`/prep expand as needed; no user-facing command |
| Restore system (`file_restore*`, IPS) | n/a | LM's `sysLMRestore`; we have BPS export + project history |
| Run/setup emulator (`file_emulator_*`, `file_simulator_*`) | done | `Run in emulator`, `Set emulator…` |
| **Export bitmap / directory bitmap** | **missing** | screenshot export |
| Analyze levels, scan ROM, clear level area | missing | utilities over data we already parse |

## View toggles

Ours: hitboxes, spawns, screen grid, sprite overlay, animate tiles, layer 3 preview, layer 1/2,
Map16 tooltips. LM additionally has: block contents, exit tiles, invisible blocks (`view_invisible`,
`_invisible_pow`), on/off + POW + silver POW + switch states, surface outlines, line-guide outlines,
sub-screen, game screen, 512-height, translucency, CDM16. **All missing** — each is a render-time
overlay over data we already decode, so they are cheap individually.

---

## Triage

Three clusters, in the order I would take them:

1. **MWL import/export.** The one gap that blocks a workflow rather than a feature: it is how
   levels move between LM and anything else, and LM's command line already speaks it, so it is
   testable the way the GFX work was. Nothing in `src/` touches it today.
2. **The overworld text/list dialogs** — level names, message box text, boss text, music
   selection, the four level lists, reveal tiles, events passed. Individually small, all reading
   tables we can locate the same way the submap GFX records were located, and together they are
   most of what "the overworld editor is partial" means.
3. **The view overlays.** Cheapest of the three and purely additive: every one draws from data the
   renderer already has.

Deliberately not on this list: LM's own ROM-editing workflow (open/save ROM, restore system, IPS),
which our project model replaces by design — see CONTRACT §0.
