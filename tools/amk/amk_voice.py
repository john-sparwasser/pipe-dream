#!/usr/bin/env python3
"""amk_voice.py TEMPO SECONDS 'mml' ... : what AddmusicK's engine writes to voice 0's volume and pitch, tick by tick.

Prints, for each tick after the first key-on, the pitch and left/right volume half a tick in (only where they changed),
and the key-ons. TEMPO is the song's t value (for the tick length). VOICE=n picks another voice."""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import amk_probe as P
import amk_events as E

def timeline(text, secs, voice):
    os.environ['PIPEDREAM_SPCVOICE'] = str(voice)
    r = P.probe([text])[0]
    if not r['ok']: return None, 'FAIL ' + ' / '.join(r['msg'])[:200]
    regs, kons, writes = [0] * 4, [], []
    for line in E.events(r['spc'], secs).splitlines():
        m = re.match(r't=\s*(\d+) V%d (\w) (\w\w)' % voice, line)
        if m: writes.append((int(m.group(1)), int(m.group(2), 16), int(m.group(3), 16))); continue
        m = re.match(r't=\s*(\d+) KON v%d' % voice, line)
        if m: kons.append(int(m.group(1)))
    return (writes, kons), None

def show(text, secs, tempo, voice=0):
    data, err = timeline(text, secs, voice)
    if err: print(err); return
    writes, kons = data
    if not kons: print('no key-on'); return
    tick = 1_024_000 / (500 / 256 * (tempo + 1))
    regs, i, last, out = [0] * 4, 0, None, []
    t0 = kons[0]
    n = int((writes[-1][0] - t0) / tick) + 2 if writes else 0
    for k in range(n):
        at = t0 + (k + 0.5) * tick
        while i < len(writes) and writes[i][0] <= at:
            regs[writes[i][1]] = writes[i][2]; i += 1
        cur = (regs[2] | regs[3] << 8, regs[0], regs[1])
        if cur != last:
            out.append('%d:%04X %02X/%02X' % (k, *cur)); last = cur
    print('key-ons at ticks', [round((c - t0) / tick, 1) for c in kons][:12])
    print('  '.join(out))

if __name__ == '__main__':
    tempo, secs = int(sys.argv[1]), float(sys.argv[2])
    for t in sys.argv[3:]:
        print('##', t)
        show(t.replace('\\n', '\n'), secs, tempo, int(os.environ.get('VOICE', '0')))
