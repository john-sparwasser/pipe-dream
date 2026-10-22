; UberASM Tool's own defines, as resources written for 2.1 expect them (reference/UBERASM_PORT.md). Assembled after the sprite
; driver's defines.asm, which has SMW's sprite tables under the same names UberASM Tool uses (sprite_status, extra_byte_1 …),
; and before macros.asm. Pipe Dream builds LoROM, non-SA-1 ROMs only, so each has its LoROM value.

!sprite_slots = 12
!fullsa1 = 0
!EXLEVEL = 1
!DisableTitleScreenLevelNMI = 0
