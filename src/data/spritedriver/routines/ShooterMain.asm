; Whether shooter X fires now, and the slot for its shot. A = its timer's reload; C set to hold
; fire while the player stands by it. C clear: fire, Y = A = the slot (SMW's $02A9DE). C set:
; not this time (Y = $FF only when there is no slot, else kept). Each timeout reloads the timer,
; fired or not, as SMW's Bullet Bill shooter ($02B466) does. 8-bit A/X/Y, X = the shooter
; (0-7), kept. DB on low RAM. Pipe Dream's, MIT, from SMW's $02B466 and
; reference/PIXI_ROUTINES.md.
    XBA
    LDA $17AB,x                 ; still counting
    BNE .no
    XBA
    STA $17AB,x
    BCC +
    LDA $94                     ; the player within 16 pixels of it
    SEC
    SBC $179B,x
    CLC
    ADC #$11
    CMP #$22
    BCC .no
+   LDA $178B,x                 ; off the screen, up or down
    SEC
    SBC $1C
    LDA $1793,x
    SBC $1D
    BNE .no
    LDA $179B,x                 ; or across
    SEC
    SBC $1A
    LDA $17A3,x
    SBC $1B
    BNE .no
    LDA $179B,x                 ; or in its last 16 pixels on the right
    SEC
    SBC $1A
    CLC
    ADC #$10
    CMP #$10
    BCC .no
    JSL $02A9DE
    BMI .no
    CLC
    RTL
.no
    SEC
    RTL
