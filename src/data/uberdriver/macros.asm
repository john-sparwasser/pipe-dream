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

; Run <label> on the SA-1 CPU and wait for it, A 8-bit; on a LoROM ROM there is none, so it runs here. On SA-1 the pack's own
; way (docs/programming.html, "Invoking SA-1"): the address in $3180-$3182, then its routine at $1E80.
macro invoke_sa1(label)
    if !sa1
        LDA.b #<label>
        STA $3180
        LDA.b #<label>>>8
        STA $3181
        LDA.b #<label>>>16
        STA $3182
        JSR $1E80
    else
        JSL <label>
    endif
endmacro

; From SA-1 code, run <addr> on the SNES CPU and wait for it, A 8-bit; always this CPU on LoROM. On SA-1 the pack's way
; ("Invoking the SNES from SA-1 CPU"): the address in $0183-$0185 (I-RAM $3183, as the SA-1 CPU sees it), $D0 to $2209, then
; wait for $018A.
macro invoke_snes(addr)
    if !sa1
        LDA.b #<addr>
        STA $0183
        LDA.b #<addr>>>8
        STA $0184
        LDA.b #<addr>>>16
        STA $0185
        LDA #$D0
        STA $2209
    ?wait:
        LDA $018A
        BEQ ?wait
        STZ $018A
    else
        JSL <addr>
    endif
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

; !<name> for a sprite table: its SA-1 address on an SA-1 ROM.
macro define_sprite_table(name, addr, addr_sa1)
    if !sa1
        !<name> = <addr_sa1>
    else
        !<name> = <addr>
    endif
endmacro
