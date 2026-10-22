#!/usr/bin/env python3
"""amk_sfx.py PORT VALUE [SECONDS] [SPC|ROM]: a sound effect's voice registers, timer tick by timer tick (2048 cycles), from the
poke. PORT 0 is $1DF9 (voice 6 in AddmusicK), 3 is $1DFC (voice 7), 1 is $1DFA. The default SPC is a silent AddmusicK song."""
import os, re, subprocess, sys
DLL = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'bin', 'Debug', 'net10.0', 'PipeDream.dll')
SILENT = '/private/tmp/claude-501/-Users-john-projects-pipe-dream/238f89d9-a310-4017-a266-75ba614c0a2d/scratchpad/silent.spc'

def timeline(port, value, secs=1.5, spc=SILENT, voice=None):
    if voice is None: voice = {0: 6, 3: 7, 1: 7}[port]
    env = dict(os.environ, PIPEDREAM_POKES=f'0.3:{port}={value},0.317:{port}=00', PIPEDREAM_SPCVOICE=str(voice))
    out = subprocess.run(['dotnet', DLL, '--spcevents', spc, str(secs)], capture_output=True, text=True, env=env).stdout
    regs, rows = [0] * 4, []
    for l in out.splitlines():
        m = re.match(r't=\s*(\d+) (.*)', l)
        if not m: continue
        t = (int(m.group(1)) - 307200) / 2048
        ev = m.group(2)
        v = re.match(r'V%d (\w) (\w\w)' % voice, ev)
        if v: regs[int(v.group(1), 16)] = int(v.group(2), 16); rows.append((t, 'P %04X %02X/%02X' % (regs[2] | regs[3] << 8, regs[0], regs[1])))
        elif ev.startswith('KON v%d' % voice): rows.append((t, 'KON ' + ' '.join(ev.split()[4:7])))
        elif ev.startswith('KOFF') and int(ev.split()[1], 16) >> voice & 1: rows.append((t, 'KOFF'))
    out2 = []
    for t, r in rows:
        if out2 and out2[-1][1] == r: continue
        if out2 and r.startswith('P') and out2[-1][1].startswith('P') and abs(out2[-1][0] - t) < 0.2: out2[-1] = (t, r); continue
        out2.append((t, r))
    return out2

if __name__ == '__main__':
    port, value = int(sys.argv[1]), sys.argv[2]
    secs = float(sys.argv[3]) if len(sys.argv) > 3 else 1.5
    spc = sys.argv[4] if len(sys.argv) > 4 else SILENT
    print('  '.join(f'{t:.1f}:{r}' for t, r in timeline(port, value, secs, spc)))
