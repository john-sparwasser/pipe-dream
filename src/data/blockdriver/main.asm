; Pipe Dream's block driver: what runs a project's custom blocks in the game. Written for Pipe
; Dream from GPS's documented on-ROM format (reference/GPS_PORT.md, ROM_TOOLING.md "GPS"), so
; blocks written for GPS run unchanged and gps.exe and GpsBlocks.Read both find an insertion
; made with it. GpsInsert supplies __temp_settings.asm and writes the tables after assembly.
; Acts-like is not touched: the build writes the project's acts-as after the blocks.

incsrc "defines.asm"
incsrc "__temp_settings.asm"

; Lunar Magic's block dispatch has a 16-byte slot per contact. Each passes the offset of that
; contact's JMP in the block (1 + 3 * its place in the JMP list) to run_block, then rejoins LM.
; The slot is exactly this long; PHB first and the JSL at +7 are what GpsBlocks.Read looks for.
macro contact(slot, n)
	org <slot>
		PHB : PHX
		REP #$30
		LDA.w #<n>*3+1
		autoclean JSL run_block
		PLX : PLB
		JMP $F602
endmacro

%contact($06F690, 0)	; Mario below
%contact($06F6A0, 1)	; Mario above
%contact($06F6B0, 2)	; Mario side
%contact($06F720, 3)	; sprite V
%contact($06F730, 4)	; sprite H
%contact($06F780, 5)	; cape
%contact($06F7C0, 6)	; fireball
%contact($06F6C0, 7)	; top corner
%contact($06F6D0, 8)	; body inside
%contact($06F6E0, 9)	; head inside
%contact($06F7D0, 10)	; wall feet
%contact($06F7E0, 11)	; wall body

; LM's compare chain has no entry for the wall-run contacts, and its sprite H compare is also
; reached with another value; both send the rest back to LM's no-block path at $06F602.
org $06F67B
	autoclean JML wall_contacts
org $06F717
	autoclean JML sprite_h_contact

; A 32KB table needs a bank to itself; aligning breaks on 6MB+ SA-1, which has room without it.
macro table_space()
	if !fullsa1
		freedata cleaned
	else
		freedata cleaned,align
	endif
endmacro

freecode
wall_contacts:
	CMP #$39 : BNE +
	JML $06F7D0|!bank
+	CMP #$EA : BNE +
	JML $06F7E0|!bank
+	JML $06F602|!bank

sprite_h_contact:
	CMP #$82 : BNE +
	JML $06F730|!bank
+	JML $06F602|!bank

; In: A = the contact's JMP offset, $03-$04 = the Map16 tile, A/X/Y 16-bit. Y carries LM's
; acts-like and $03-$04 the tile through to the block, so neither is touched. The block runs with
; B = its own bank and X = the current sprite slot, and its RTL returns to the contact slot.
run_block:
	STA $05
	LDX $03
	if !__insert_pages_40_plus == 0
		CPX #$4000 : BCS .none		; no tables for pages 40-7F
	endif
	SEP #$20
	LDA.l block_banks,x : BEQ .none	; 0: no block on this tile
	STA $02
	PHA				; for the PLB below
	REP #$20
	TXA : ASL : TAX
	if !__insert_pages_40_plus == 1
		CPX #$8000 : BCC .low
		LDA.l block_pointers_2-$8000,x
		BRA .found
	.low
	endif
	LDA.l block_pointers_1,x
.found
	STA $00
	LDA $05 : CMP #$001E : BCC .run	; the wall contacts run only behind a $37 header
	SEP #$20
	LDA [$00] : CMP #$37
	BNE .skip
	REP #$20
.run
	LDA $00 : CLC : ADC $05 : STA $00
	SEP #$30
	LDX $15E9|!addr
	PLB
	JML [$0000|!dp]
.skip
	PLA
.none
	SEP #$30
	RTL

	db "GPS_VeRsIoN", 3, !__insert_pages_40_plus
	dl block_banks, block_pointers_1
	if !__insert_pages_40_plus == 1
		dw $8000, $8000
		dl block_pointers_2
		dw $8000
	else
		dw $4000, $8000
	endif

; The tables, empty here: GpsInsert fills them after assembly. Without pages 40+ the bank table
; shares the code's block, which a re-insert frees through the slots' autoclean.
fillbyte $00
if !__insert_pages_40_plus == 0
	block_banks: fill $4000
endif
%table_space()
block_pointers_1: fill $8000
if !__insert_pages_40_plus == 1
	%table_space()
	block_pointers_2: fill $8000
	%table_space()
	block_banks: fill $8000
endif
