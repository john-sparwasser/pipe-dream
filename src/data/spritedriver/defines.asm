; Pipe Dream's custom sprite defines (reference/PIXI_PORT.md).
;
; Every custom sprite is assembled with these first, so a sprite written for PIXI finds the names
; it expects. The names are the interface PIXI's sprites are written against; the values are SMW's
; own RAM and the driver's tables below — on an SA-1 build (the SA-1 Pack is in, map mode $23) where
; the pack moved them (reference/SA1_PORT.md): direct page to $3000, $0100-$1FFF to $6100-$7FFF,
; the sprite tables to its 22-slot layout (more_sprites/sprite_tables.asm).

!SA1 = 0
!sa1 = 0
!fullsa1 = 0
!dp = $0000
!addr = $0000
!bank = $800000
!Base1 = $0000
!Base2 = $0000
!BankB = $800000
!Bank8 = $80
!bank8 = $80
!SprSize = $0C
if read1($00FFD5) == $23
    if read1($00FFD7) == $0D
        fullsa1rom
        !fullsa1 = 1
    else
        sa1rom
    endif
    !SA1 = 1
    !sa1 = 1
    !dp = $3000
    !addr = $6000
    !bank = $000000
    !Base1 = $3000
    !Base2 = $6000
    !BankB = $000000
    !Bank8 = $00
    !bank8 = $00
    !SprSize = $16
endif
!GenStart = $D0
!EXLEVEL = 1
; The level's load table, moved for 255 sprites a level as PIXI moves it (main.asm, "The load
; table"); $7E1938 is free. The SA-1 Pack has its own, at $418A00.
!Disable255SpritesPerLevel = 0
if !SA1
    !sprite_load_table = $418A00
else
    !sprite_load_table = $7FAF00
endif
!1938 = !sprite_load_table
!7FAF00 = !sprite_load_table

; ---- What the driver keeps per sprite slot (12 bytes each; 22 on SA-1, where they are PIXI's) ----
if !SA1
    !extra_bits = $6040
    !new_code_flag = $6056
    !extra_prop_1 = $6057
    !extra_prop_2 = $606D
    !new_sprite_num = $6083
    !extra_byte_1 = $6099
    !extra_byte_2 = $60AF
    !extra_byte_3 = $60C5
    !extra_byte_4 = $60DB
else
    !extra_bits = $7FAB10       ; the level data's extra bits ($04 the extra bit, $08 custom)
    !new_code_flag = $7FAB1C
    !extra_prop_1 = $7FAB28     ; the config's two extra property bytes
    !extra_prop_2 = $7FAB34
    !extra_byte_1 = $7FAB40     ; the placement's extra bytes; with more than four,
    !extra_byte_2 = $7FAB4C     ; 1-3 are a long pointer to them in the level data
    !extra_byte_3 = $7FAB58
    !extra_byte_4 = $7FAB64
    !new_sprite_num = $7FAB9E   ; the custom sprite's own number ($9E holds its acts-like)
endif
!CustomBit = $08

; The macros a PIXI sprite may call from PIXI's sa1def.asm (reference/PIXI_ROUTINES.md, "sa1def.asm
; macros"; ours, written from that). Not LDE: PIXI's _header.asm files define that, and a second
; definition is an error once one is imported.
; BES/BEC: branch if LM's "extra bit" (extra bits 3 rather than 2) is set/clear, A = $04/$00.
macro BES(label)
    LDA !extra_bits,x
    AND #$04
    BNE <label>
endmacro

macro BEC(label)
    LDA !extra_bits,x
    AND #$04
    BEQ <label>
endmacro

; Fall through when -x <= A < x (signed), else branch; A is left + x, C set on the branch.
macro Between(x, label)
    CLC
    ADC <x>
    CMP <x>*2
    BCS <label>
endmacro

; With !__debug = 1, print where it was assembled.
macro debugmsg(msg)
    if not(defined("__debug"))
        !__debug = 0
    endif
    if !__debug == 1
        print "$", hex(pc), " <msg>"
    endif
endmacro

; The 8 x 8 multiplier, A 8-bit: %mul_a() takes A as the first factor, %mul_b() A as the second and
; starts it, and the product is at !MulLo (16-bit) / !MulHi after a NOP #4. On SA-1, where sprite
; code runs on the SA-1 CPU, the SA-1's ($2250 mode 0, $2251-$2254, the product from $2306; the
; write to $2254 starts it), as the pack's guide has it; the SNES CPU's ($4202/$4203, $4216) otherwise.
macro mul_a()
    if !SA1
        STZ $2250
        STA $2251
        STZ $2252
    else
        STA $4202
    endif
endmacro

macro mul_b()
    if !SA1
        STA $2253
        STZ $2254
    else
        STA $4203
    endif
endmacro

if !SA1
    !MulLo = $2306
    !MulHi = $2307
else
    !MulLo = $4216
    !MulHi = $4217
endif

; Define !name as a table's address, its SA-1 one on an SA-1 ROM.
macro define_sprite_table(name, addr, addr_sa1)
    if !SA1
        !<name> = <addr_sa1>
    else
        !<name> = <addr>
    endif
endmacro

macro define_base2_address(name, addr)
    !<name> = <addr>+!Base2
endmacro

; ---- SMW's sprite tables, by name and by address (the SA-1 Pack's 22-slot layout on SA-1) ----
%define_sprite_table(sprite_num, $9E, $3200)
%define_sprite_table(sprite_speed_y, $AA, $9E)
%define_sprite_table(sprite_speed_x, $B6, $B6)
%define_sprite_table(sprite_misc_c2, $C2, $D8)
%define_sprite_table(sprite_y_low, $D8, $3216)
%define_sprite_table(sprite_x_low, $E4, $322C)
%define_sprite_table(sprite_status, $14C8, $3242)
%define_sprite_table(sprite_y_high, $14D4, $3258)
%define_sprite_table(sprite_x_high, $14E0, $326E)
%define_sprite_table(sprite_speed_y_frac, $14EC, $74C8)
%define_sprite_table(sprite_speed_x_frac, $14F8, $74DE)
%define_sprite_table(sprite_misc_1504, $1504, $74F4)
%define_sprite_table(sprite_misc_1510, $1510, $750A)
%define_sprite_table(sprite_misc_151c, $151C, $3284)
%define_sprite_table(sprite_misc_1528, $1528, $329A)
%define_sprite_table(sprite_misc_1534, $1534, $32B0)
%define_sprite_table(sprite_misc_1540, $1540, $32C6)
%define_sprite_table(sprite_misc_154c, $154C, $32DC)
%define_sprite_table(sprite_misc_1558, $1558, $32F2)
%define_sprite_table(sprite_misc_1564, $1564, $3308)
%define_sprite_table(sprite_misc_1570, $1570, $331E)
%define_sprite_table(sprite_misc_157c, $157C, $3334)
%define_sprite_table(sprite_blocked_status, $1588, $334A)
%define_sprite_table(sprite_misc_1594, $1594, $3360)
%define_sprite_table(sprite_off_screen_horz, $15A0, $3376)
%define_sprite_table(sprite_misc_15ac, $15AC, $338C)
%define_sprite_table(sprite_slope, $15B8, $7520)
%define_sprite_table(sprite_off_screen, $15C4, $7536)
%define_sprite_table(sprite_being_eaten, $15D0, $754C)
%define_sprite_table(sprite_obj_interact, $15DC, $7562)
%define_sprite_table(sprite_oam_index, $15EA, $33A2)
%define_sprite_table(sprite_oam_properties, $15F6, $33B8)
%define_sprite_table(sprite_misc_1602, $1602, $33CE)
%define_sprite_table(sprite_misc_160e, $160E, $33E4)
%define_sprite_table(sprite_index_in_level, $161A, $7578)
%define_sprite_table(sprite_misc_1626, $1626, $758E)
%define_sprite_table(sprite_behind_scenery, $1632, $75A4)
%define_sprite_table(sprite_misc_163e, $163E, $33FA)
%define_sprite_table(sprite_in_water, $164A, $75BA)
%define_sprite_table(sprite_tweaker_1656, $1656, $75D0)
%define_sprite_table(sprite_tweaker_1662, $1662, $75EA)
%define_sprite_table(sprite_tweaker_166e, $166E, $7600)
%define_sprite_table(sprite_tweaker_167a, $167A, $7616)
%define_sprite_table(sprite_tweaker_1686, $1686, $762C)
%define_sprite_table(sprite_off_screen_vert, $186C, $7642)
%define_sprite_table(sprite_misc_187b, $187B, $3410)
%define_sprite_table(sprite_tweaker_190f, $190F, $7658)
%define_sprite_table(sprite_misc_1fd6, $1FD6, $766E)
%define_sprite_table(sprite_cape_disable_time, $1FE2, $7FD6)

!9E = !sprite_num
!AA = !sprite_speed_y
!B6 = !sprite_speed_x
!C2 = !sprite_misc_c2
!D8 = !sprite_y_low
!E4 = !sprite_x_low
!14C8 = !sprite_status
!14D4 = !sprite_y_high
!14E0 = !sprite_x_high
!14EC = !sprite_speed_y_frac
!14F8 = !sprite_speed_x_frac
!1504 = !sprite_misc_1504
!1510 = !sprite_misc_1510
!151C = !sprite_misc_151c
!1528 = !sprite_misc_1528
!1534 = !sprite_misc_1534
!1540 = !sprite_misc_1540
!154C = !sprite_misc_154c
!1558 = !sprite_misc_1558
!1564 = !sprite_misc_1564
!1570 = !sprite_misc_1570
!157C = !sprite_misc_157c
!1588 = !sprite_blocked_status
!1594 = !sprite_misc_1594
!15A0 = !sprite_off_screen_horz
!15AC = !sprite_misc_15ac
!15B8 = !sprite_slope
!15C4 = !sprite_off_screen
!15D0 = !sprite_being_eaten
!15DC = !sprite_obj_interact
!15EA = !sprite_oam_index
!15F6 = !sprite_oam_properties
!1602 = !sprite_misc_1602
!160E = !sprite_misc_160e
!161A = !sprite_index_in_level
!1626 = !sprite_misc_1626
!1632 = !sprite_behind_scenery
!163E = !sprite_misc_163e
!164A = !sprite_in_water
!1656 = !sprite_tweaker_1656
!1662 = !sprite_tweaker_1662
!166E = !sprite_tweaker_166e
!167A = !sprite_tweaker_167a
!1686 = !sprite_tweaker_1686
!186C = !sprite_off_screen_vert
!187B = !sprite_misc_187b
!190F = !sprite_tweaker_190f
!1FD6 = !sprite_misc_1fd6
!1FE2 = !sprite_cape_disable_time
!sprite_load_index = !sprite_index_in_level

; The same per-slot tables by their addresses, as some sprites name them.
!7FAB10 = !extra_bits
!7FAB1C = !new_code_flag
!7FAB28 = !extra_prop_1
!7FAB34 = !extra_prop_2
!7FAB40 = !extra_byte_1
!7FAB4C = !extra_byte_2
!7FAB58 = !extra_byte_3
!7FAB64 = !extra_byte_4
!7FAB9E = !new_sprite_num
!00AA = !AA
!00B6 = !B6
if !SA1
    !BankA = $400000
else
    !BankA = $7E0000
endif

; ---- Cluster sprites (20 slots) ----
; A custom one's number is its list number plus this (SMW's are 01-08); bit 7 is its extra bit.
!ClusterOffset = $09
!ClusterSize = $14
!cluster_num = $1892|!Base2
!cluster_y_low = $1E02|!Base2
!cluster_x_low = $1E16|!Base2
!cluster_y_high = $1E2A|!Base2
!cluster_x_high = $1E3E|!Base2
!cluster_misc_0f4a = $0F4A|!Base2
!cluster_misc_0f5e = $0F5E|!Base2
!cluster_misc_0f72 = $0F72|!Base2
!cluster_misc_0f86 = $0F86|!Base2
!cluster_misc_0f9a = $0F9A|!Base2
!cluster_misc_1e52 = $1E52|!Base2
!cluster_misc_1e66 = $1E66|!Base2
!cluster_misc_1e7a = $1E7A|!Base2
!cluster_misc_1e8e = $1E8E|!Base2

; ---- Extended sprites (10 slots) ----
; A custom one's number is its list number plus this (SMW's are 01-12). Spawning keeps to the
; first eight: 8 and 9 are Mario's fireballs'.
!ExtendedOffset = $13
!ExSprSize = $07
!ExtendedSize = $08
!FireballSprSize = $09
!extended_num = $170B|!Base2
!extended_y_low = $1715|!Base2
!extended_x_low = $171F|!Base2
!extended_y_high = $1729|!Base2
!extended_x_high = $1733|!Base2
!extended_y_speed = $173D|!Base2
!extended_x_speed = $1747|!Base2
!extended_y_fraction = $1751|!Base2
!extended_x_fraction = $175B|!Base2
!extended_table = $1765|!Base2
!extended_table_1 = $1765|!Base2
!extended_table_2 = $198C|!Base2
!extended_table_3 = $1996|!Base2
!extended_table_4 = $19A0|!Base2
!extended_table_5 = $19AA|!Base2
!extended_timer = $176F|!Base2
!extended_behind = $1779|!Base2

; ---- Minor extended, bounce, smoke, spinning coin and score sprites ----
; A custom one's number is its list number plus its kind's offset, SMW's own count of that kind
; (a smoke number's bit 7 is a flag, as SMW's are). The sizes are slot counts.
!MinorExtendedOffset = $0C
!MinorExtendedSize = $0C
!BounceOffset = $08
!BounceSize = $04
!SmokeOffset = $06
!SmokeSize = $04
!SpinningCoinOffset = $02
!SpinningCoinSize = $04
!ScoreOffset = $16
!ScoreSize = $06

!minor_extended_num = $17F0|!Base2
!minor_extended_y_low = $17FC|!Base2
!minor_extended_x_low = $1808|!Base2
!minor_extended_y_high = $1814|!Base2
!minor_extended_x_high = $18EA|!Base2
!minor_extended_y_speed = $1820|!Base2
!minor_extended_x_speed = $182C|!Base2
!minor_extended_y_fraction = $1838|!Base2
!minor_extended_x_fraction = $1844|!Base2
!minor_extended_timer = $1850|!Base2
!minor_extended_table_1 = $1938|!Base2
!minor_extended_table_2 = $1944|!Base2
!minor_extended_table_3 = $1950|!Base2

!bounce_num = $1699|!Base2
!bounce_init = $169D|!Base2
!bounce_y_low = $16A1|!Base2
!bounce_x_low = $16A5|!Base2
!bounce_y_high = $16A9|!Base2
!bounce_x_high = $16AD|!Base2
!bounce_y_speed = $16B1|!Base2
!bounce_x_speed = $16B5|!Base2
!bounce_x_bits = $16B9|!Base2
!bounce_y_bits = $16BD|!Base2
!bounce_map16_tile = $16C1|!Base2
!bounce_map16_low = $16C1|!Base2
!bounce_timer = $16C5|!Base2
!bounce_table = $16C9|!Base2
!bounce_table_1 = $16C9|!Base2
!bounce_properties = $1901|!Base2
!bounce_map16_high = $1968|!Base2
!bounce_table_2 = $1968|!Base2
!bounce_table_3 = $196C|!Base2
!bounce_table_4 = $1970|!Base2

!smoke_num = $17C0|!Base2
!smoke_y_low = $17C4|!Base2
!smoke_x_low = $17C8|!Base2
!smoke_timer = $17CC|!Base2
!smoke_table_1 = $195C|!Base2
!smoke_table_2 = $1960|!Base2
!smoke_table_3 = $1964|!Base2
!smoke_y_high = $1960|!Base2
!smoke_x_high = $1964|!Base2

!spinning_coin_num = $17D0|!Base2
!spinning_coin_y_low = $17D4|!Base2
!spinning_coin_y_speed = $17D8|!Base2
!spinning_coin_y_bits = $17DC|!Base2
!spinning_coin_x_low = $17E0|!Base2
!spinning_coin_layer = $17E4|!Base2
!spinning_coin_y_high = $17E8|!Base2
!spinning_coin_x_high = $17EC|!Base2
!spinning_coin_table_1 = $1974|!Base2
!spinning_coin_table_2 = $1978|!Base2
!spinning_coin_table_3 = $197C|!Base2

!score_num = $16E1|!Base2
!score_y_low = $16E7|!Base2
!score_x_low = $16ED|!Base2
!score_x_high = $16F3|!Base2
!score_y_high = $16F9|!Base2
!score_y_speed = $16FF|!Base2
!score_layer = $1705|!Base2
!score_table_1 = $1980|!Base2
!score_table_2 = $1986|!Base2

; ---- Shooters (8 slots) ----
!shoot_num = $1783|!Base2
!shoot_y_low = $178B|!Base2
!shoot_y_high = $1793|!Base2
!shoot_x_low = $179B|!Base2
!shoot_x_high = $17A3|!Base2
!shoot_timer = $17AB|!Base2
!shooter_num = $1783|!Base2
!shooter_y_low = $178B|!Base2
!shooter_y_high = $1793|!Base2
!shooter_x_low = $179B|!Base2
!shooter_x_high = $17A3|!Base2
!shooter_timer = $17AB|!Base2
