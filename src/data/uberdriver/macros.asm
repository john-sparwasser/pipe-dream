; UberASM Tool's macros under their names, written for Pipe Dream from what 2.1's readme says each does (reference/
; UBERASM_PORT.md). The build leaves out any the user's macro library defines itself, as a 1.x one does, so theirs is used.
; Each macro starts at "macro" at the start of a line and ends at "endmacro".

; Fail unless this is UberASM Tool major.minor or later: Pipe Dream does what 2.1 does.
macro require_uber_ver(major, minor)
    if <major> > 2 || (<major> == 2 && <minor> > 1)
        error "this resource needs UberASM Tool <major>.<minor>; Pipe Dream does what 2.1 does"
    endif
endmacro

; A binary file at <label>, in freespace of its own.
macro prot_file(file, label)
    pushpc
    freedata
<label>:
    incbin <file>
    pullpc
endmacro

; A source file at <label>, in freespace of its own.
macro prot_source(file, label)
    pushpc
    freecode
<label>:
    incsrc <file>
    pullpc
endmacro

; Run <label> on the SA-1 CPU: on the LoROM ROMs Pipe Dream builds there is none, so it runs here.
macro invoke_sa1(label)
    JSL <label>
endmacro

; From SA-1 code, run <addr> on the SNES CPU: always this CPU here.
macro invoke_snes(addr)
    JSL <addr>
endmacro

; Copy <len> bytes from <src> to <dest> (long addresses), as MVN does; A, X and Y end 8-bit.
macro move_block(src, dest, len)
    PHB
    REP #$30
    LDA.w #<len>-1
    LDX.w #<src>
    LDY.w #<dest>
    MVN <dest>>>16, <src>>>16
    SEP #$30
    PLB
endmacro

; Call the shared routine routines/<name>.asm, which the build puts in once.
macro UberRoutine(name)
    JSL UberRoutine_<name>
endmacro

; !<name> for a sprite table: the LoROM address.
macro define_sprite_table(name, addr, addr_sa1)
    !<name> = <addr>
endmacro
