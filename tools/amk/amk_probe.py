#!/usr/bin/env python3
"""Parallel AMK oracle probes. probe(list_of_mml) -> list of dicts {ok,msg,size,song(bytes),spc}"""
import subprocess, os, shutil, tempfile, re
from concurrent.futures import ThreadPoolExecutor
ROOT=os.environ['PIPEDREAM_AMK']          # a folder laid out like AddmusicK's own test/ (the addmusick binary, libasar, asm/, samples/ ...)
def _setup():
    w=tempfile.mkdtemp(prefix='amkp_'); t=w+'/t'; os.makedirs(t)
    for f in os.listdir(ROOT):
        src=ROOT+'/'+f
        if f in ('samples','1DF9','1DFC','SPCs'): 
            if f=='SPCs': os.makedirs(t+'/SPCs/1DF9'); os.makedirs(t+'/SPCs/1DFC')
            else: os.symlink(src,t+'/'+f)
        elif os.path.isdir(src): shutil.copytree(src,t+'/'+f)
        else: shutil.copy(src,t+'/'+f)
    return w,t
S0=0x238D
def one(text, extra_files=None, prefix=True):
    w,t=_setup()
    try:
        if prefix and not re.search(r'#(amk|am4|amm)\b',text): text='#amk 2\n'+text
        open(t+'/music/t.mml','w').write(text)
        for k,v in (extra_files or {}).items(): open(t+'/music/'+k,'w').write(v)
        try:
            p=subprocess.run(['./addmusick','-norom','-noblock','t.mml'],cwd=t,capture_output=True,text=True,timeout=20)
        except subprocess.TimeoutExpired:
            return {'ok':False,'msg':['TIMEOUT (AMK hung)']}
        out=p.stdout+p.stderr
        ok='Success!' in out
        msg=[l for l in out.splitlines() if l.strip() and not l.startswith(('AddmusicK','Parser','Protip','seem right','Total','test.mml','Completed','Success'))]
        r={'ok':ok,'msg':msg}
        m=re.search(r'^t\.mml: .*?0x([0-9A-F]+) bytes',out,re.M)
        if ok and m:
            size=int(m.group(1),16); spc=open(t+'/SPCs/t.spc','rb').read(); ram=spc[0x100:0x10100]
            r.update(size=size,ram=ram,song=ram[S0:S0+size],dsp=spc[0x10100:0x10180],spc=spc)
        return r
    finally: shutil.rmtree(w)
def probe(texts, workers=8, **kw):
    with ThreadPoolExecutor(workers) as ex: return list(ex.map(lambda x: one(x,**kw), texts))
def hx(b): return ' '.join(f'{x:02X}' for x in b)
if __name__=='__main__':
    import sys
    for t,r in zip(sys.argv[1:], probe([a.replace('\\n','\n') for a in sys.argv[1:]])):
        print(repr(t),'->','OK' if r['ok'] else 'FAIL', hx(r.get('song',b'')) if r['ok'] else ' / '.join(r['msg'])[:160])
