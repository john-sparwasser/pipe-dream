; The defines blocks and routines are written against, under the names GPS blocks use, so those
; assemble unchanged. Addresses are SMW's and SA-1 Pack's remaps of them.

namespace nested on

; SA-1 is decided by the ROM being assembled onto: map mode $23 at $00FFD5, and a ROM size
; byte of $0D for the 6MB+ mapping.
!sa1 = 0
!fullsa1 = 0
!dp = $0000
!addr = $0000
!bank = $800000
!bank8 = $80
!sprite_slots = $0C
if read1($00FFD5) == $23
	if read1($00FFD7) == $0D
		fullsa1rom
		!fullsa1 = 1
	else
		sa1rom
	endif
	!sa1 = 1
	!dp = $3000
	!addr = $6000
	!bank = $000000
	!bank8 = $00
	!sprite_slots = $16
endif

; !<define> = 1 when the ROM was last saved by Lunar Magic <version> (x100) or later; the
; version digits sit at $0FF0B4 ("2.57" style). Sets !lm_version too.
macro assert_lm_version(version, define)
	!lm_version #= (read1($0FF0B4)-'0')*100+(read1($0FF0B6)-'0')*10+(read1($0FF0B7)-'0')
	if !lm_version >= <version>
		!<define> = 1
	else
		!<define> = 0
	endif
endmacro
%assert_lm_version(257, "EXLEVEL")

; A sprite table under its name and its vanilla address: !sprite_status and !14C8 alike.
macro sprite_table(name, vanilla, lo, sa1)
	if !sa1
		!<name> = <sa1>
	else
		!<name> = <lo>
	endif
	!<vanilla> = !<name>
endmacro

%sprite_table(sprite_num, 9E, $9E, $3200)
%sprite_table(sprite_speed_y, AA, $AA, $9E)
%sprite_table(sprite_speed_x, B6, $B6, $B6)
%sprite_table(sprite_misc_c2, C2, $C2, $D8)
%sprite_table(sprite_y_low, D8, $D8, $3216)
%sprite_table(sprite_x_low, E4, $E4, $322C)
%sprite_table(sprite_status, 14C8, $14C8, $3242)
%sprite_table(sprite_y_high, 14D4, $14D4, $3258)
%sprite_table(sprite_x_high, 14E0, $14E0, $326E)
%sprite_table(sprite_speed_y_frac, 14EC, $14EC, $74C8)
%sprite_table(sprite_speed_x_frac, 14F8, $14F8, $74DE)
%sprite_table(sprite_misc_1504, 1504, $1504, $74F4)
%sprite_table(sprite_misc_1510, 1510, $1510, $750A)
%sprite_table(sprite_misc_151c, 151C, $151C, $3284)
%sprite_table(sprite_misc_1528, 1528, $1528, $329A)
%sprite_table(sprite_misc_1534, 1534, $1534, $32B0)
%sprite_table(sprite_misc_1540, 1540, $1540, $32C6)
%sprite_table(sprite_misc_154c, 154C, $154C, $32DC)
%sprite_table(sprite_misc_1558, 1558, $1558, $32F2)
%sprite_table(sprite_misc_1564, 1564, $1564, $3308)
%sprite_table(sprite_misc_1570, 1570, $1570, $331E)
%sprite_table(sprite_misc_157c, 157C, $157C, $3334)
%sprite_table(sprite_blocked_status, 1588, $1588, $334A)
%sprite_table(sprite_misc_1594, 1594, $1594, $3360)
%sprite_table(sprite_off_screen_horz, 15A0, $15A0, $3376)
%sprite_table(sprite_misc_15ac, 15AC, $15AC, $338C)
%sprite_table(sprite_slope, 15B8, $15B8, $7520)
%sprite_table(sprite_off_screen, 15C4, $15C4, $7536)
%sprite_table(sprite_being_eaten, 15D0, $15D0, $754C)
%sprite_table(sprite_obj_interact, 15DC, $15DC, $7562)
%sprite_table(sprite_oam_index, 15EA, $15EA, $33A2)
%sprite_table(sprite_oam_properties, 15F6, $15F6, $33B8)
%sprite_table(sprite_misc_1602, 1602, $1602, $33CE)
%sprite_table(sprite_misc_160e, 160E, $160E, $33E4)
%sprite_table(sprite_index_in_level, 161A, $161A, $7578)
%sprite_table(sprite_misc_1626, 1626, $1626, $758E)
%sprite_table(sprite_behind_scenery, 1632, $1632, $75A4)
%sprite_table(sprite_misc_163e, 163E, $163E, $33FA)
%sprite_table(sprite_in_water, 164A, $164A, $75BA)
%sprite_table(sprite_tweaker_1656, 1656, $1656, $75D0)
%sprite_table(sprite_tweaker_1662, 1662, $1662, $75EA)
%sprite_table(sprite_tweaker_166e, 166E, $166E, $7600)
%sprite_table(sprite_tweaker_167a, 167A, $167A, $7616)
%sprite_table(sprite_tweaker_1686, 1686, $1686, $762C)
%sprite_table(sprite_off_screen_vert, 186C, $186C, $7642)
%sprite_table(sprite_misc_187b, 187B, $187B, $3410)
%sprite_table(sprite_tweaker_190f, 190F, $190F, $7658)
%sprite_table(sprite_misc_1fd6, 1FD6, $1FD6, $766E)
%sprite_table(sprite_cape_disable_time, 1FE2, $1FE2, $7FD6)

; Sprite tool tables (Romi's Sprite Tool, PIXI): extra bits, properties and bytes.
%sprite_table(sprite_extra_bits, 7FAB10, $7FAB10, $6040)
%sprite_table(sprite_new_code_flag, 7FAB1C, $7FAB1C, $6056)
%sprite_table(sprite_extra_prop1, 7FAB28, $7FAB28, $6057)
%sprite_table(sprite_extra_prop2, 7FAB34, $7FAB34, $606D)
%sprite_table(extra_byte_1, 7FAB40, $7FAB40, $6099)
%sprite_table(extra_byte_2, 7FAB4C, $7FAB4C, $60AF)
%sprite_table(extra_byte_3, 7FAB58, $7FAB58, $60C5)
%sprite_table(extra_byte_4, 7FAB64, $7FAB64, $60DB)
%sprite_table(sprite_custom_num, 7FAB9E, $7FAB9E, $6083)

; Custom Bounce Blocks' tables; change these if that patch was set up at other addresses.
!RAM_BounceMap16Low = $7FC275
!RAM_BounceMap16High = $7FC279
!RAM_BounceTileTbl = $7FC27D
!RAM_BounceMap16Low_SA1 = $6132
!RAM_BounceMap16High_SA1 = $6136
!RAM_BounceTileTbl_SA1 = $613A
%sprite_table(bounce_map16_low, RAM_BounceMap16Low, !RAM_BounceMap16Low, !RAM_BounceMap16Low_SA1)
%sprite_table(bounce_map16_high, RAM_BounceMap16High, !RAM_BounceMap16High, !RAM_BounceMap16High_SA1)
%sprite_table(bounce_sprite_tile, RAM_BounceTileTbl, !RAM_BounceTileTbl, !RAM_BounceTileTbl_SA1)
