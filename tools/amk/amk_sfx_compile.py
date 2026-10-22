#!/usr/bin/env python3
"""amk_sfx_compile.py 'sfx text' ... : what AddmusicK compiles a sound effect to. Each text replaces $1DF9 effect 01 in a copy
of the oracle folder; the bytes are read back from the .spc's ARAM between effect 01's table entry and effect 02's (AddmusicK
1.0.5 keeps the $1DF9 table at $19A4)."""
import os, re, shutil, subprocess, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import amk_probe as P
from concurrent.futures import ThreadPoolExecutor

TABLE = 0x19A4

def one(text):
    w, t = P._setup()
    try:
        os.unlink(t + '/1DF9')                                  # a copy of our own, with effect 01 replaced
        shutil.copytree(P.ROOT + '/1DF9', t + '/1DF9')
        open(t + '/1DF9/01 Hit head.txt', 'w').write(text)
        open(t + '/music/t.mml', 'w').write('#amk 2\n#0 r1')
        p = subprocess.run(['./addmusick', '-norom', '-noblock', 't.mml'], cwd=t, capture_output=True, text=True, timeout=30)
        out = p.stdout + p.stderr
        if 'Success!' not in out:
            return None, [l for l in out.splitlines() if l.strip() and 'Protip' not in l and 'seem right' not in l][-4:]
        ram = open(t + '/SPCs/t.spc', 'rb').read()[0x100:0x10100]
        a = ram[TABLE + 2] | ram[TABLE + 3] << 8
        b = ram[TABLE + 4] | ram[TABLE + 5] << 8
        return ram[a:b], None
    finally:
        shutil.rmtree(w)

def compile_all(texts, workers=8):
    with ThreadPoolExecutor(workers) as ex: return list(ex.map(one, texts))

if __name__ == '__main__':
    for t, (b, err) in zip(sys.argv[1:], compile_all([a.replace('\\n', '\n') for a in sys.argv[1:]])):
        print(repr(t), '->', P.hx(b) if b is not None else 'FAIL ' + ' / '.join(err))
