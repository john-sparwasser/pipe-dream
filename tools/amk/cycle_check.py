#!/usr/bin/env python3
"""cycle_check.py: our SPC700 interpreter's instruction timing against blargg's snes_spc (in libgme, `brew install game-music-emu`).

Each group of instructions runs 256 x M times, then voice 0 is keyed on. libgme's audio says when the note started; our interpreter
says when KON was written. Both are measured against the same program with an empty group, so the DSP's own latency cancels.
A group whose times disagree has an instruction our interpreter counts wrong."""
import ctypes, os, struct, subprocess, sys, tempfile

DLL = os.environ.get('PIPEDREAM_DLL', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'bin', 'Debug', 'net10.0', 'PipeDream.dll'))
gme = ctypes.CDLL(os.environ.get('GME', '/opt/homebrew/lib/libgme.dylib'))

CODE, SUB, M = 0x0800, 0x0F00, 8

GROUPS = {
    'mov': '8D03 CD12 E805 C410 E410 F410 C50005 E50005 F50005 F60005 D50005 D60005 8F5510 FA1011 E8 00',
    'indirect': '8F0012 8F0513 8D00 F712 D712 CD14 E6 C6 BF AF',
    'alu': '60 8801 8410 850005 A801 6805 6410 650005 280F 0801 4801 1C 5C 3C 7C 0B10 4B10 2B10 6B10 AB10 8B10 BC 9C 3D 1D FC DC 9F',
    'word': 'BA10 DA10 3A10 1A10 7A10 9A10 5A10',
    'muldiv': 'CF 8D00 E864 CD07 9E',
    'stack': '2D AE 4D CE 6D EE 0D 8E',
    'branch': 'E800 F000 D000 2F00 9000 B000 1000 3000 8F0110 2E1000 6E1000 C805 AD05 1E0005',
    'call': '3F000F 5F{next}',
    'bits': '0210 1210 031000 131000 0E0005 4E0005 E0 ED 80',
    'regs': '7D DD 5D FD 9D',
    'io': 'E8 6C C4F2 E4F3 8F6CF2 E4FD',
}

def assemble(body_hex):
    out, pc = bytearray(), CODE
    def emit(h): out.extend(bytes.fromhex(h))
    emit('8F%02X1F' % M)                       # mov $1F,#M
    emit('8F001E')                             # mov $1E,#0 (256)
    loop = CODE + len(out)
    for tok in body_hex.split():
        if '{next}' in tok:
            at = CODE + len(out) + 3
            tok = tok.replace('{next}', '%02X%02X' % (at & 0xFF, at >> 8))
        emit(tok)
    emit('CD00')                               # mov x,#0 (bodies use x as a DP pointer; keep it sane)
    rel = loop - (CODE + len(out) + 3)
    emit('6E1E%02X' % (rel & 0xFF))            # dbnz $1E,loop
    rel = loop - (CODE + len(out) + 3)
    emit('6E1F%02X' % (rel & 0xFF))            # dbnz $1F,loop
    emit('8F4CF2 8F01F3'.replace(' ', ''))     # KON voice 0
    emit('2FFE')                               # bra $
    return out

def spc(code):
    d = bytearray(0x10200)
    d[0:33] = b'SNES-SPC700 Sound File Data v0.30'
    d[33:35] = b'\x1a\x1a'; d[35] = 27; d[36] = 30
    struct.pack_into('<H', d, 0x25, CODE); d[0x27:0x2C] = bytes([0, 0, 0, 0x02, 0xEF])
    ram = bytearray(0x10000)
    ram[CODE:CODE + len(code)] = code
    ram[SUB] = 0x6F                             # ret
    struct.pack_into('<HH', ram, 0x0200, 0x0300, 0x0300)         # sample 0 starts and loops at $0300
    ram[0x0300:0x0309] = bytes([0xB3]) + bytes([0x77] * 4 + [0x99] * 4)   # a looping square block
    ram[0xF1] = 0x00
    d[0x100:0x10100] = ram
    dsp = bytearray(0x80)
    for r, v in {0x00: 0x7F, 0x01: 0x7F, 0x02: 0x00, 0x03: 0x10, 0x04: 0, 0x05: 0xFF, 0x06: 0xE0, 0x07: 0x7F,
                 0x0C: 0x7F, 0x1C: 0x7F, 0x2C: 0, 0x3C: 0, 0x5D: 0x02, 0x6C: 0x20, 0x4C: 0, 0x5C: 0, 0x6D: 0xFF, 0x7D: 0}.items():
        dsp[r] = v
    d[0x10100:0x10180] = dsp
    return bytes(d)

def gme_onset(data, seconds=4):
    emu = ctypes.c_void_p()
    err = gme.gme_open_data(data, len(data), ctypes.byref(emu), 32000)
    if err: raise RuntimeError(ctypes.cast(err, ctypes.c_char_p).value)
    gme.gme_ignore_silence(emu, 1)
    gme.gme_start_track(emu, 0)
    n = 32000 * seconds * 2
    buf = (ctypes.c_short * n)()
    gme.gme_play(emu, n, buf)
    gme.gme_delete(emu)
    for i in range(0, n, 2):
        if abs(buf[i]) > 200: return i // 2 * 32               # SPC cycles (32 per sample)
    return None

def ours(data):
    f = tempfile.NamedTemporaryFile(suffix='.spc', delete=False); f.write(data); f.close()
    try:
        out = subprocess.run(['dotnet', DLL, '--spcwrites', f.name, '4'], capture_output=True, text=True, timeout=120).stdout
    finally: os.unlink(f.name)
    for line in out.splitlines():
        if ' 4C=01' in line or '4C=01' in line.replace(' ', ''):
            return int(line.split()[0].split('=')[-1])
    return None

if __name__ == '__main__':
    base_g, base_o = gme_onset(spc(assemble(''))), ours(spc(assemble('')))
    print('baseline: gme onset %s cycles, ours KON %s' % (base_g, base_o))
    for name, body in GROUPS.items():
        if len(sys.argv) > 1 and name not in sys.argv[1:]: continue
        s = spc(assemble(body))
        g, o = gme_onset(s), ours(s)
        if g is None or o is None: print('%-9s gme %s ours %s' % (name, g, o)); continue
        dg, do = g - base_g, o - base_o
        per = (dg - do) / (256 * M)
        print('%-9s gme %8d ours %8d  per pass: gme %.2f ours %.2f cycles  %s' % (name, dg, do, dg / (256 * M), do / (256 * M), 'OK' if abs(per) < 0.2 else 'DIFF %+.2f' % per))

def timer_program(wait):
    """Count timer-0 ticks (500 Hz) by polling $FD with `wait` x 256 busy loops between reads; key on once 200 are counted."""
    out = bytearray()
    def emit(h): out.extend(bytes.fromhex(h.replace(' ', '')))
    emit('8F10FA 8F01F1')                      # timer 0 target 16, enable it
    emit('8F0010 8F0011')                      # $10/$11 = ticks counted
    loop = CODE + len(out)
    emit('E4FD 8410 C410 9002 AB11')           # a = $FD; $10 += a (carry into $11)  [clrc omitted: adc after mov keeps C from 'cmp' below]
    emit('8F%02X1F' % wait)                     # busy: wait x 256 dbnz
    emit('8F001E 6E1EFD 6E1FFA')
    emit('E411 6801')                          # $11 >= 1 (256 ticks counted)?
    rel = loop - (CODE + len(out) + 2)
    emit('90%02X' % (rel & 0xFF))              # bcc loop
    emit('8F4CF2 8F01F3 2FFE')
    return out

if __name__ == '__main__' and 'timer' in sys.argv[1:]:
    for wait in (1, 12, 30, 60):
        s = spc(timer_program(wait))
        print('wait %2d: gme onset %s, ours KON %s' % (wait, gme_onset(s, 6), ours(s)))
