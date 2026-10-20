; Pipe Dream's custom sprite defines (reference/PIXI_PORT.md).
;
; Every custom sprite is assembled with these first, so a sprite written for PIXI finds the names
; it expects. The names are the interface PIXI's sprites are written against; the values are SMW's
; own RAM (a LoROM, non-SA-1 ROM, which is all Pipe Dream builds) and the driver's tables below.

!SA1 = 0
!sa1 = 0
!dp = $0000
!addr = $0000
!bank = $800000
!Base1 = $0000
!Base2 = $0000
!BankB = $800000
!Bank8 = $80
!bank8 = $80
!GenStart = $D0
!SprSize = $0C
!EXLEVEL = 1
; The 255-sprites-per-level remap is not part of the driver, so the load table is SMW's own.
!Disable255SpritesPerLevel = 1
!1938 = $1938
!sprite_load_table = $1938

; ---- What the driver keeps per sprite slot (12 bytes each) ----
!extra_bits = $7FAB10           ; the level data's extra bits ($04 the extra bit, $08 custom)
!new_code_flag = $7FAB1C
!extra_prop_1 = $7FAB28         ; the config's two extra property bytes
!extra_prop_2 = $7FAB34
!extra_byte_1 = $7FAB40         ; the placement's extra bytes; with more than four,
!extra_byte_2 = $7FAB4C         ; 1-3 are a long pointer to them in the level data
!extra_byte_3 = $7FAB58
!extra_byte_4 = $7FAB64
!new_sprite_num = $7FAB9E       ; the custom sprite's own number ($9E holds its acts-like)
!CustomBit = $08

; LM's "extra bit": set when the placement's extra bits are 3 rather than 2.
macro LDE()
    LDA !extra_bits,x
    AND #$04
endmacro

; ---- SMW's sprite tables, by address ----
!9E = $9E
!AA = $AA
!B6 = $B6
!C2 = $C2
!D8 = $D8
!E4 = $E4
!14C8 = $14C8
!14D4 = $14D4
!14E0 = $14E0
!14EC = $14EC
!14F8 = $14F8
!1504 = $1504
!1510 = $1510
!151C = $151C
!1528 = $1528
!1534 = $1534
!1540 = $1540
!154C = $154C
!1558 = $1558
!1564 = $1564
!1570 = $1570
!157C = $157C
!1588 = $1588
!1594 = $1594
!15A0 = $15A0
!15AC = $15AC
!15B8 = $15B8
!15C4 = $15C4
!15D0 = $15D0
!15DC = $15DC
!15EA = $15EA
!15F6 = $15F6
!1602 = $1602
!160E = $160E
!161A = $161A
!1626 = $1626
!1632 = $1632
!163E = $163E
!164A = $164A
!1656 = $1656
!1662 = $1662
!166E = $166E
!167A = $167A
!1686 = $1686
!186C = $186C
!187B = $187B
!190F = $190F
!1FD6 = $1FD6
!1FE2 = $1FE2

; ---- The same tables by name ----
!sprite_num = $9E
!sprite_speed_y = $AA
!sprite_speed_x = $B6
!sprite_misc_c2 = $C2
!sprite_y_low = $D8
!sprite_x_low = $E4
!sprite_status = $14C8
!sprite_y_high = $14D4
!sprite_x_high = $14E0
!sprite_speed_y_frac = $14EC
!sprite_speed_x_frac = $14F8
!sprite_misc_1504 = $1504
!sprite_misc_1510 = $1510
!sprite_misc_151c = $151C
!sprite_misc_1528 = $1528
!sprite_misc_1534 = $1534
!sprite_misc_1540 = $1540
!sprite_misc_154c = $154C
!sprite_misc_1558 = $1558
!sprite_misc_1564 = $1564
!sprite_misc_1570 = $1570
!sprite_misc_157c = $157C
!sprite_blocked_status = $1588
!sprite_misc_1594 = $1594
!sprite_off_screen_horz = $15A0
!sprite_misc_15ac = $15AC
!sprite_slope = $15B8
!sprite_off_screen = $15C4
!sprite_being_eaten = $15D0
!sprite_obj_interact = $15DC
!sprite_oam_index = $15EA
!sprite_oam_properties = $15F6
!sprite_misc_1602 = $1602
!sprite_misc_160e = $160E
!sprite_load_index = $161A
!sprite_index_in_level = $161A
!sprite_misc_1626 = $1626
!sprite_behind_scenery = $1632
!sprite_misc_163e = $163E
!sprite_in_water = $164A
!sprite_tweaker_1656 = $1656
!sprite_tweaker_1662 = $1662
!sprite_tweaker_166e = $166E
!sprite_tweaker_167a = $167A
!sprite_tweaker_1686 = $1686
!sprite_off_screen_vert = $186C
!sprite_misc_187b = $187B
!sprite_tweaker_190f = $190F
!sprite_misc_1fd6 = $1FD6
!sprite_cape_disable_time = $1FE2

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
!00AA = $AA
!00B6 = $B6
!BankA = $7E0000

; ---- Cluster sprites (20 slots) ----
; A custom one's number is its list number plus this (SMW's are 01-08); bit 7 is its extra bit.
!ClusterOffset = $09
!ClusterSize = $14
!cluster_num = $1892
!cluster_y_low = $1E02
!cluster_x_low = $1E16
!cluster_y_high = $1E2A
!cluster_x_high = $1E3E
!cluster_misc_0f4a = $0F4A
!cluster_misc_0f5e = $0F5E
!cluster_misc_0f72 = $0F72
!cluster_misc_0f86 = $0F86
!cluster_misc_0f9a = $0F9A
!cluster_misc_1e52 = $1E52
!cluster_misc_1e66 = $1E66
!cluster_misc_1e7a = $1E7A
!cluster_misc_1e8e = $1E8E

; ---- Extended sprites (10 slots) ----
; A custom one's number is its list number plus this (SMW's are 01-12). Spawning keeps to the
; first eight: 8 and 9 are Mario's fireballs'.
!ExtendedOffset = $13
!ExSprSize = $07
!ExtendedSize = $08
!FireballSprSize = $09
!extended_num = $170B
!extended_y_low = $1715
!extended_x_low = $171F
!extended_y_high = $1729
!extended_x_high = $1733
!extended_y_speed = $173D
!extended_x_speed = $1747
!extended_y_fraction = $1751
!extended_x_fraction = $175B
!extended_table = $1765
!extended_table_1 = $1765
!extended_table_2 = $198C
!extended_table_3 = $1996
!extended_table_4 = $19A0
!extended_table_5 = $19AA
!extended_timer = $176F
!extended_behind = $1779

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

!minor_extended_num = $17F0
!minor_extended_y_low = $17FC
!minor_extended_x_low = $1808
!minor_extended_y_high = $1814
!minor_extended_x_high = $18EA
!minor_extended_y_speed = $1820
!minor_extended_x_speed = $182C
!minor_extended_y_fraction = $1838
!minor_extended_x_fraction = $1844
!minor_extended_timer = $1850
!minor_extended_table_1 = $1938
!minor_extended_table_2 = $1944
!minor_extended_table_3 = $1950

!bounce_num = $1699
!bounce_init = $169D
!bounce_y_low = $16A1
!bounce_x_low = $16A5
!bounce_y_high = $16A9
!bounce_x_high = $16AD
!bounce_y_speed = $16B1
!bounce_x_speed = $16B5
!bounce_x_bits = $16B9
!bounce_y_bits = $16BD
!bounce_map16_tile = $16C1
!bounce_map16_low = $16C1
!bounce_timer = $16C5
!bounce_table = $16C9
!bounce_table_1 = $16C9
!bounce_properties = $1901
!bounce_map16_high = $1968
!bounce_table_2 = $1968
!bounce_table_3 = $196C
!bounce_table_4 = $1970

!smoke_num = $17C0
!smoke_y_low = $17C4
!smoke_x_low = $17C8
!smoke_timer = $17CC
!smoke_table_1 = $195C
!smoke_table_2 = $1960
!smoke_table_3 = $1964
!smoke_y_high = $1960
!smoke_x_high = $1964

!spinning_coin_num = $17D0
!spinning_coin_y_low = $17D4
!spinning_coin_y_speed = $17D8
!spinning_coin_y_bits = $17DC
!spinning_coin_x_low = $17E0
!spinning_coin_layer = $17E4
!spinning_coin_y_high = $17E8
!spinning_coin_x_high = $17EC
!spinning_coin_table_1 = $1974
!spinning_coin_table_2 = $1978
!spinning_coin_table_3 = $197C

!score_num = $16E1
!score_y_low = $16E7
!score_x_low = $16ED
!score_x_high = $16F3
!score_y_high = $16F9
!score_y_speed = $16FF
!score_layer = $1705
!score_table_1 = $1980
!score_table_2 = $1986

; ---- Shooters (8 slots) ----
!shoot_num = $1783
!shoot_y_low = $178B
!shoot_y_high = $1793
!shoot_x_low = $179B
!shoot_x_high = $17A3
!shoot_timer = $17AB
!shooter_num = $1783
!shooter_y_low = $178B
!shooter_y_high = $1793
!shooter_x_low = $179B
!shooter_x_high = $17A3
!shooter_timer = $17AB
