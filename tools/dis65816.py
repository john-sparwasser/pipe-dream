"""Minimal 65816 disassembler for tracing LM's inserted ASM in LoROM images.
Usage: python dis65816.py <rom.smc> <snesAddrHex> [count] [--m8] [--x8]
Assumes M/X 16-bit unless flags; tracks REP/SEP as it goes (linear sweep).
"""
import sys

# opcode -> (mnemonic, mode)  mode determines operand size
# modes: imp, imm_m (M-dependent), imm_x (X-dependent), imm8, dp, dpx, dpy,
#        abs, absx, absy, long, longx, ind, indy, indl, indly, indx, rel8, rel16,
#        sr, sry, absind, absindx, absindl, blk
OPS = {
0x00:("BRK","imm8"),0x01:("ORA","indx"),0x02:("COP","imm8"),0x03:("ORA","sr"),
0x04:("TSB","dp"),0x05:("ORA","dp"),0x06:("ASL","dp"),0x07:("ORA","indl"),
0x08:("PHP","imp"),0x09:("ORA","imm_m"),0x0A:("ASL","imp"),0x0B:("PHD","imp"),
0x0C:("TSB","abs"),0x0D:("ORA","abs"),0x0E:("ASL","abs"),0x0F:("ORA","long"),
0x10:("BPL","rel8"),0x11:("ORA","indy"),0x12:("ORA","ind"),0x13:("ORA","sry"),
0x14:("TRB","dp"),0x15:("ORA","dpx"),0x16:("ASL","dpx"),0x17:("ORA","indly"),
0x18:("CLC","imp"),0x19:("ORA","absy"),0x1A:("INC","imp"),0x1B:("TCS","imp"),
0x1C:("TRB","abs"),0x1D:("ORA","absx"),0x1E:("ASL","absx"),0x1F:("ORA","longx"),
0x20:("JSR","abs"),0x21:("AND","indx"),0x22:("JSL","long"),0x23:("AND","sr"),
0x24:("BIT","dp"),0x25:("AND","dp"),0x26:("ROL","dp"),0x27:("AND","indl"),
0x28:("PLP","imp"),0x29:("AND","imm_m"),0x2A:("ROL","imp"),0x2B:("PLD","imp"),
0x2C:("BIT","abs"),0x2D:("AND","abs"),0x2E:("ROL","abs"),0x2F:("AND","long"),
0x30:("BMI","rel8"),0x31:("AND","indy"),0x32:("AND","ind"),0x33:("AND","sry"),
0x34:("BIT","dpx"),0x35:("AND","dpx"),0x36:("ROL","dpx"),0x37:("AND","indly"),
0x38:("SEC","imp"),0x39:("AND","absy"),0x3A:("DEC","imp"),0x3B:("TSC","imp"),
0x3C:("BIT","absx"),0x3D:("AND","absx"),0x3E:("ROL","absx"),0x3F:("AND","longx"),
0x40:("RTI","imp"),0x41:("EOR","indx"),0x42:("WDM","imm8"),0x43:("EOR","sr"),
0x44:("MVP","blk"),0x45:("EOR","dp"),0x46:("LSR","dp"),0x47:("EOR","indl"),
0x48:("PHA","imp"),0x49:("EOR","imm_m"),0x4A:("LSR","imp"),0x4B:("PHK","imp"),
0x4C:("JMP","abs"),0x4D:("EOR","abs"),0x4E:("LSR","abs"),0x4F:("EOR","long"),
0x50:("BVC","rel8"),0x51:("EOR","indy"),0x52:("EOR","ind"),0x53:("EOR","sry"),
0x54:("MVN","blk"),0x55:("EOR","dpx"),0x56:("LSR","dpx"),0x57:("EOR","indly"),
0x58:("CLI","imp"),0x59:("EOR","absy"),0x5A:("PHY","imp"),0x5B:("TCD","imp"),
0x5C:("JML","long"),0x5D:("EOR","absx"),0x5E:("LSR","absx"),0x5F:("EOR","longx"),
0x60:("RTS","imp"),0x61:("ADC","indx"),0x62:("PER","rel16"),0x63:("ADC","sr"),
0x64:("STZ","dp"),0x65:("ADC","dp"),0x66:("ROR","dp"),0x67:("ADC","indl"),
0x68:("PLA","imp"),0x69:("ADC","imm_m"),0x6A:("ROR","imp"),0x6B:("RTL","imp"),
0x6C:("JMP","absind"),0x6D:("ADC","abs"),0x6E:("ROR","abs"),0x6F:("ADC","long"),
0x70:("BVS","rel8"),0x71:("ADC","indy"),0x72:("ADC","ind"),0x73:("ADC","sry"),
0x74:("STZ","dpx"),0x75:("ADC","dpx"),0x76:("ROR","dpx"),0x77:("ADC","indly"),
0x78:("SEI","imp"),0x79:("ADC","absy"),0x7A:("PLY","imp"),0x7B:("TDC","imp"),
0x7C:("JMP","absindx"),0x7D:("ADC","absx"),0x7E:("ROR","absx"),0x7F:("ADC","longx"),
0x80:("BRA","rel8"),0x81:("STA","indx"),0x82:("BRL","rel16"),0x83:("STA","sr"),
0x84:("STY","dp"),0x85:("STA","dp"),0x86:("STX","dp"),0x87:("STA","indl"),
0x88:("DEY","imp"),0x89:("BIT","imm_m"),0x8A:("TXA","imp"),0x8B:("PHB","imp"),
0x8C:("STY","abs"),0x8D:("STA","abs"),0x8E:("STX","abs"),0x8F:("STA","long"),
0x90:("BCC","rel8"),0x91:("STA","indy"),0x92:("STA","ind"),0x93:("STA","sry"),
0x94:("STY","dpx"),0x95:("STA","dpx"),0x96:("STX","dpy"),0x97:("STA","indly"),
0x98:("TYA","imp"),0x99:("STA","absy"),0x9A:("TXS","imp"),0x9B:("TXY","imp"),
0x9C:("STZ","abs"),0x9D:("STA","absx"),0x9E:("STZ","absx"),0x9F:("STA","longx"),
0xA0:("LDY","imm_x"),0xA1:("LDA","indx"),0xA2:("LDX","imm_x"),0xA3:("LDA","sr"),
0xA4:("LDY","dp"),0xA5:("LDA","dp"),0xA6:("LDX","dp"),0xA7:("LDA","indl"),
0xA8:("TAY","imp"),0xA9:("LDA","imm_m"),0xAA:("TAX","imp"),0xAB:("PLB","imp"),
0xAC:("LDY","abs"),0xAD:("LDA","abs"),0xAE:("LDX","abs"),0xAF:("LDA","long"),
0xB0:("BCS","rel8"),0xB1:("LDA","indy"),0xB2:("LDA","ind"),0xB3:("LDA","sry"),
0xB4:("LDY","dpx"),0xB5:("LDA","dpx"),0xB6:("LDX","dpy"),0xB7:("LDA","indly"),
0xB8:("CLV","imp"),0xB9:("LDA","absy"),0xBA:("TSX","imp"),0xBB:("TYX","imp"),
0xBC:("LDY","absx"),0xBD:("LDA","absx"),0xBE:("LDX","absy"),0xBF:("LDA","longx"),
0xC0:("CPY","imm_x"),0xC1:("CMP","indx"),0xC2:("REP","imm8"),0xC3:("CMP","sr"),
0xC4:("CPY","dp"),0xC5:("CMP","dp"),0xC6:("DEC","dp"),0xC7:("CMP","indl"),
0xC8:("INY","imp"),0xC9:("CMP","imm_m"),0xCA:("DEX","imp"),0xCB:("WAI","imp"),
0xCC:("CPY","abs"),0xCD:("CMP","abs"),0xCE:("DEC","abs"),0xCF:("CMP","long"),
0xD0:("BNE","rel8"),0xD1:("CMP","indy"),0xD2:("CMP","ind"),0xD3:("CMP","sry"),
0xD4:("PEI","dp"),0xD5:("CMP","dpx"),0xD6:("DEC","dpx"),0xD7:("CMP","indly"),
0xD8:("CLD","imp"),0xD9:("CMP","absy"),0xDA:("PHX","imp"),0xDB:("STP","imp"),
0xDC:("JML","absindl"),0xDD:("CMP","absx"),0xDE:("DEC","absx"),0xDF:("CMP","longx"),
0xE0:("CPX","imm_x"),0xE1:("SBC","indx"),0xE2:("SEP","imm8"),0xE3:("SBC","sr"),
0xE4:("CPX","dp"),0xE5:("SBC","dp"),0xE6:("INC","dp"),0xE7:("SBC","indl"),
0xE8:("INX","imp"),0xE9:("SBC","imm_m"),0xEA:("NOP","imp"),0xEB:("XBA","imp"),
0xEC:("CPX","abs"),0xED:("SBC","abs"),0xEE:("INC","abs"),0xEF:("SBC","long"),
0xF0:("BEQ","rel8"),0xF1:("SBC","indy"),0xF2:("SBC","ind"),0xF3:("SBC","sry"),
0xF4:("PEA","abs"),0xF5:("SBC","dpx"),0xF6:("INC","dpx"),0xF7:("SBC","indly"),
0xF8:("SED","imp"),0xF9:("SBC","absy"),0xFA:("PLX","imp"),0xFB:("XCE","imp"),
0xFC:("JSR","absindx"),0xFD:("SBC","absx"),0xFE:("INC","absx"),0xFF:("SBC","longx"),
}

SIZES = {"imp":0,"imm8":1,"dp":1,"dpx":1,"dpy":1,"sr":1,"sry":1,"ind":1,"indy":1,
"indl":1,"indly":1,"indx":1,"rel8":1,"abs":2,"absx":2,"absy":2,"rel16":2,
"absind":2,"absindx":2,"absindl":2,"blk":2,"long":3,"longx":3}

def snes_to_pc(a):
    bank, addr = a >> 16, a & 0xFFFF
    return (bank & 0x7F) * 0x8000 + (addr - 0x8000)

def dis(rom, snes, count, m8=False, x8=False):
    pc = snes_to_pc(snes)
    out = []
    for _ in range(count):
        op = rom[pc]
        mn, mode = OPS[op]
        if mode == "imm_m": n = 1 if m8 else 2
        elif mode == "imm_x": n = 1 if x8 else 2
        else: n = SIZES[mode]
        opnd = rom[pc+1:pc+1+n]
        v = int.from_bytes(opnd, "little")
        if mn == "REP":
            if v & 0x20: m8 = False
            if v & 0x10: x8 = False
        elif mn == "SEP":
            if v & 0x20: m8 = True
            if v & 0x10: x8 = True
        if mode == "rel8":
            tgt = (snes & 0xFF0000) | ((snes + 2 + (v - 256 if v > 127 else v)) & 0xFFFF)
            s = f"{mn} ${tgt:06X}"
        elif mode == "rel16":
            tgt = (snes & 0xFF0000) | ((snes + 3 + (v - 65536 if v > 32767 else v)) & 0xFFFF)
            s = f"{mn} ${tgt:06X}"
        elif mode in ("imm_m","imm_x","imm8"): s = f"{mn} #${v:0{n*2}X}"
        elif mode == "blk": s = f"{mn} ${opnd[1]:02X},${opnd[0]:02X}"
        elif n == 0: s = mn
        else:
            suff = {"dpx":",X","dpy":",Y","absx":",X","absy":",Y","longx":",X",
                    "sr":",S","sry":",S),Y","indy":"),Y","indly":"],Y"}.get(mode,"")
            pre = {"ind":"(","indy":"(","indx":"(","indl":"[","indly":"[",
                   "absind":"(","absindx":"(","absindl":"[","sry":"("}.get(mode,"")
            post = {"ind":")","indx":",X)","absind":")","absindx":",X)",
                    "indl":"]","absindl":"]"}.get(mode,"")
            s = f"{mn} {pre}${v:0{max(n,1)*2}X}{post}{suff}"
        raw = rom[pc:pc+1+n].hex()
        out.append(f"${snes:06X}: {raw:<10} {s}")
        pc += 1 + n
        snes = (snes & 0xFF0000) | ((snes + 1 + n) & 0xFFFF)
        if mn in ("RTS","RTL","RTI","JML","BRA","STP") and mode != "absindl":
            out.append("-" * 40)
    return "\n".join(out)

if __name__ == "__main__":
    path, addr = sys.argv[1], int(sys.argv[2], 16)
    count = int(sys.argv[3]) if len(sys.argv) > 3 else 40
    data = open(path, "rb").read()
    if len(data) % 0x8000 == 0x200: data = data[0x200:]
    print(dis(data, addr, count, "--m8" in sys.argv, "--x8" in sys.argv))
