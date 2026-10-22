#!/usr/bin/env python3
"""Differential: AMK oracle vs our compiler. diff(list_of_mml) -> prints mismatches, returns (n, bad)."""
import subprocess, tempfile, os, re, sys
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0,os.path.dirname(__file__))
import amk_probe as P
DLL=os.environ.get('PIPEDREAM_DLL', os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','..','src','bin','Debug','net10.0','PipeDream.dll'))
def ours(text):
    if not re.search(r'#(amk|am4|amm)\b',text): text='#amk 2\n'+text
    f=tempfile.NamedTemporaryFile('w',suffix='.mml',delete=False); f.write(text); f.close()
    try:
        p=subprocess.run(['dotnet',DLL,'--mml',f.name],capture_output=True,text=True,timeout=30)
    finally: os.unlink(f.name)
    out=p.stdout.strip()
    if out.startswith('ERROR') or p.returncode!=0: return False,out.splitlines()[:2]
    return True,bytes.fromhex(out.replace(' ',''))
def diff(texts, show_ok=False, workers=8):
    oracle=P.probe(texts, workers=workers)
    with ThreadPoolExecutor(workers) as ex: mine=list(ex.map(ours,texts))
    bad=0
    for t,o,(ok,m) in zip(texts,oracle,mine):
        if o['ok']!=ok:
            bad+=1; print('ACCEPT MISMATCH', repr(t), '| amk:', 'OK' if o['ok'] else ' / '.join(o['msg'])[:90], '| ours:', 'OK' if ok else m)
        elif ok and o['song']!=m:
            bad+=1; print('BYTES MISMATCH', repr(t)); print('   amk ', P.hx(o['song'])); print('   ours', P.hx(m))
        elif show_ok: print('same', repr(t))
    print(f'{len(texts)-bad}/{len(texts)} agree'); return len(texts),bad
if __name__=='__main__':
    import json
    cases=json.load(open(sys.argv[1])) if len(sys.argv)>1 else []
    diff(cases)
