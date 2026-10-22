import sys, amk_probe as P, amk_diff as D
from concurrent.futures import ThreadPoolExecutor
KIND=['bytes']
def bad(t):
    r=P.one(t); ok,m=D.ours(t)
    if KIND[0]=='bytes': return r['ok'] and ok and r['song']!=m
    return r['ok']!=ok
def reduce(t):
    lines=t.split('\n'); toks=[l.split(' ') for l in lines]
    flat=[(i,j) for i,l in enumerate(toks) for j in range(len(l))]
    def build(tk): return '\n'.join(' '.join(x for x in l if x!='') for l in tk if any(x for x in l))
    changed=True
    while changed:
        changed=False
        # try removing each token (in parallel batches)
        cands=[]
        for i,l in enumerate(toks):
            for j in range(len(l)):
                if l[j]=='' or (j==0 and l[j].startswith('#')): continue
                nt=[list(x) for x in toks]; nt[i][j]=''
                cands.append((i,j,build(nt)))
        with ThreadPoolExecutor(8) as ex: res=list(ex.map(lambda c: bad(c[2]) if c[2].strip() else False, cands))
        for (i,j,txt),b in zip(cands,res):
            if b:
                toks[i][j]=''; changed=True; break
    return build(toks)
if __name__=='__main__':
    for t in sys.argv[1:]:
        t=t.replace('\\n','\n'); KIND[0]='bytes'; print('REDUCED:',repr(reduce(t)))
