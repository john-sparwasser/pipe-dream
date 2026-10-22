import sys, amk_probe as P, amk_diff as D
def show(t):
    r=P.one(t); ok,m=D.ours(t)
    if not r['ok'] or not ok: print('accept: amk',r['ok'],'ours',ok, r['msg'][:3] if not r['ok'] else m); return
    a=r['song']
    if a==m: print('same'); return
    i=next((k for k in range(min(len(a),len(m))) if a[k]!=m[k]), min(len(a),len(m)))
    print('len amk',len(a),'ours',len(m),'first diff',i)
    print(' amk ',P.hx(a[max(0,i-14):i+14])); print(' ours',P.hx(m[max(0,i-14):i+14]))
if __name__=='__main__':
    for t in sys.argv[1:]: print(repr(t)); show(t.replace('\\n','\n'))
