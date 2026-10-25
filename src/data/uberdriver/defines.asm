; UberASM Tool's own defines, as resources written for 2.1 expect them (reference/UBERASM_PORT.md). Assembled after the sprite
; driver's defines.asm, which has SMW's sprite tables under the same names UberASM Tool uses (sprite_status, extra_byte_1 …)
; and decides LoROM or SA-1 from the header (!sa1, !fullsa1, !addr, !dp, !bank), and before macros.asm.

!sprite_slots = !SprSize
!EXLEVEL = 1
!DisableTitleScreenLevelNMI = 0
