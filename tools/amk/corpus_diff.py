import sys,os
sys.path.insert(0,os.path.dirname(__file__))
import amk_probe as P, amk_diff as D
from concurrent.futures import ThreadPoolExecutor
M=os.environ['PIPEDREAM_AMK']+'/music/'
songs=['cal.mml','click.mml','ct_fanfare_2.opt.mml','dv.mml','dv2.mml','dv3.mml','ek.mml','exp.mml','exp2.mml','exp3.mml','rt_ct.mml','rt_exp2.mml','ww_solo.mml']
sel=sys.argv[1:] or songs
texts=[open(M+s).read() for s in sel]
oracle=P.probe(texts, prefix=False) if False else None
with ThreadPoolExecutor(4) as ex: oracle=list(ex.map(lambda t:P.one(t,prefix=False), texts))
for s,t,o in zip(sel,texts,oracle):
    ok,m=D.ours(t)
    if not o['ok']: print(s,'ORACLE FAIL',o['msg'][:2]); continue
    if not ok: print(s,'OURS FAIL',m); continue
    a=o['song']
    if a==m: print(s,'SAME',len(a),'bytes')
    else:
        i=next((k for k in range(min(len(a),len(m))) if a[k]!=m[k]), min(len(a),len(m)))
        print(s,'DIFF: amk',len(a),'ours',len(m),'first diff at',i)
        print('   amk ',P.hx(a[max(0,i-12):i+16])); print('   ours',P.hx(m[max(0,i-12):i+16]))
