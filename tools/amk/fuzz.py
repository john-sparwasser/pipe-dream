import random, sys, amk_diff as D
def gen_len(r):
    k=r.random()
    if k<0.35: return ''
    if k<0.8: return str(r.choice([1,2,3,4,5,6,7,8,9,12,16,24,32,48,64,96,192]))+r.choice(['','','','.','..'])
    if k<0.92: return '='+str(r.choice([1,2,3,5,7,12,24,36,48,96,100,127,128,200,300]))
    return str(r.randint(0,200))
def note(r): return r.choice('abcdefg')+r.choice(['','','','+','-'])+gen_len(r)
HEX={0xDA:1,0xDB:1,0xDC:2,0xDD:3,0xDE:3,0xDF:0,0xE0:1,0xE1:2,0xE2:1,0xE3:2,0xE4:1,0xE5:3,0xE7:1,0xE8:2,0xEA:1,0xEB:3,0xEC:3,0xED:2,0xEE:1,0xEF:3,0xF0:0,0xF1:3,0xF2:3,0xF3:2,0xF4:1,0xF5:8,0xF6:2,0xF8:1,0xF9:2,0xFA:2,0xFC:4}
def hexcmd(r):
    c=r.choice(list(HEX)); ps=[r.choice([0,1,2,5,0x10,0x20,0x7F,0x80,0xFF,r.randint(0,255)]) for _ in range(HEX[c])]
    if c in (0xDA,0xDB): ps=[r.randint(0,0x13)]
    return ' '.join('$%02X'%x for x in [c]+ps)
def simple(r):
    k=r.random()
    if k<0.38: return note(r)
    if k<0.46: return 'r'+gen_len(r)
    if k<0.52: return '^'+gen_len(r)
    c=r.choice(['o','<','>','l','h','@','v','w','y','q','t','p','n','&','hex','hex','hex','trip'])
    if c=='o': return 'o%d'%r.randint(1,6)
    if c in '<>': return c
    if c=='l': return 'l%d'%r.choice([1,2,4,8,12,16,24,32,48,64])
    if c=='h': return 'h%d'%r.randint(-6,6)
    if c=='@': return '@%d'%r.choice(list(range(0,19))+list(range(21,30))+[30,31])
    if c=='v': return 'v%d'%r.randint(0,255)
    if c=='w': return 'w%d'%r.randint(0,255)
    if c=='y': return r.choice(['y%d'%r.randint(0,20),'y%d,%d,%d'%(r.randint(0,20),r.randint(0,1),r.randint(0,1))])
    if c=='q': return 'q%X%X'%(r.randint(0,7),r.randint(0,15))
    if c=='t': return 't%d'%r.randint(1,255)
    if c=='p': return r.choice(['p%d,%d'%(r.randint(0,40),r.randint(0,40)),'p%d,%d,%d'%(r.randint(0,40),r.randint(0,40),r.randint(0,40))])
    if c=='n': return 'n%02X'%r.randint(0,0x1F)
    if c=='&': return '&'
    if c=='trip': return '{'+' '.join(note(r) for _ in range(r.randint(1,4)))+'}'
    return hexcmd(r)
def chan(r, labels):
    out=[]; used_intro=False
    for _ in range(r.randint(2,14)):
        k=r.random()
        if k<0.10:
            body=' '.join(simple(r) for _ in range(r.randint(1,4))); out.append('[%s]%s'%(body,r.choice(['','2','3','7'])))
        elif k<0.14:
            body=' '.join(simple(r) for _ in range(r.randint(1,3))); i=r.randint(1,3); labels.append(i); out.append('(%d)[%s]%s'%(i,body,r.choice(['','2','4'])))
        elif k<0.17 and labels: out.append('(%d)%s'%(r.choice(labels),r.choice(['','2','3'])))
        elif k<0.20:
            body=' '.join(simple(r) for _ in range(r.randint(1,3))); out.append('[[%s]]%s'%(body,r.choice(['','2','3'])))
        elif k<0.22 and not used_intro: out.append('/'); used_intro=True
        else: out.append(simple(r))
    return ' '.join(out)
def song(seed):
    r=random.Random(seed); labels=[]; parts=[]
    for ch in sorted(r.sample(range(8),r.randint(1,3))): parts.append('#%d '%ch+chan(r,labels))
    opts=[]
    if r.random()<0.10: opts.append('#option tempoimmunity')
    if r.random()<0.05: opts.append('#halvetempo')
    if r.random()<0.05: opts.append('#option noloop')
    return '\n'.join(opts+parts)
if __name__=='__main__':
    start=int(sys.argv[1]); n=int(sys.argv[2])
    D.diff([song(s) for s in range(start,start+n)])
