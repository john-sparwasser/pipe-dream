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
!Bank8 = $800000
!SprSize = $0C
!EXLEVEL = 1
; The 255-sprites-per-level remap is not part of the driver.
!Disable255SpritesPerLevel = 1

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
