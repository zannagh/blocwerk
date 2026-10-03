import json,os,itertools,statistics as st,numpy as np
base=os.path.dirname(os.path.abspath(__file__))
def load(t): return json.load(open(os.path.join(base,f'detections_{t}.json')))
F=load('full'); A=F['detections']
imgs=sorted(F['per_image'])
seen={i:set() for i in imgs}
for d in A: seen[d['image']].add(d['id'])

print('=== PER IMAGE ===')
for i in imgs:
    pi=F['per_image'][i]
    print(f"{i} {pi['w']}x{pi['h']} n={pi['n']:2d} ids={sorted(seen[i])}")

print('\n=== EDGES ===')
edges=[]
for a,b in itertools.combinations(imgs,2):
    s=seen[a]&seen[b]
    if s: edges.append((a,b,sorted(s)))
for a,b,s in sorted(edges,key=lambda e:-len(e[2])):
    print(f"{a[-4:]}-{b[-4:]} n={len(s)} {s}")
print('edges total',len(edges))
for k in (1,2,3,4):
    print(f' edges with exactly {k} shared:',sum(1 for e in edges if len(e[2])==k))
print(' edges >=4:',sum(1 for e in edges if len(e[2])>=4))

adj={i:set() for i in imgs}
for a,b,_ in edges: adj[a].add(b); adj[b].add(a)
def comps(nodes,adj):
    seenn=set();out=[]
    for n in nodes:
        if n in seenn: continue
        st_=[n];c=set()
        while st_:
            x=st_.pop()
            if x in c: continue
            c.add(x); st_+= [y for y in adj[x] if y in nodes and y not in c]
        seenn|=c; out.append(sorted(c))
    return out
C=comps(set(imgs),adj)
print('\n=== CONNECTIVITY ===')
print('components:',len(C))
for c in C: print('  ',c)
print('degrees:',{i[-4:]:len(adj[i]) for i in imgs})
print('\narticulation points:')
for n in imgs:
    rest=set(imgs)-{n}
    c2=comps(rest,adj)
    if len(c2)>len(C): print('  ',n,'-> splits into',len(c2),[[x[-4:] for x in g] for g in c2])
# bridges
print('bridges (edges whose removal disconnects):')
for a,b,s in edges:
    adj2={k:set(v) for k,v in adj.items()}; adj2[a].discard(b); adj2[b].discard(a)
    if len(comps(set(imgs),adj2))>len(C): print('  ',a[-4:],b[-4:],'shared',s)

print('\n=== COVERAGE ===')
ROLES=['TL','TR','BR','BL','H','V']
allseen=sorted({d['id'] for d in A})
print('ids seen:',allseen)
print('ids 0-35 missing:',[i for i in range(36) if i not in allseen])
for seg in range(6):
    row=[]
    for r in range(6):
        i=seg*6+r
        cnt=sum(1 for d in A if d['id']==i)
        nimg=len({d['image'] for d in A if d['id']==i})
        row.append(f"{ROLES[r]}({i})={'-' if cnt==0 else f'{cnt}/{nimg}img'}")
    print(f'seg{seg}:', ' '.join(row))

print('\n=== SIZE STATS (mean side px, 125mm) ===')
sz=sorted(d['mean_side'] for d in A)
q=lambda p: float(np.percentile(sz,p))
print(f"n={len(sz)} min={sz[0]:.1f} p10={q(10):.1f} p25={q(25):.1f} median={q(50):.1f} p75={q(75):.1f} p90={q(90):.1f} max={sz[-1]:.1f}")
sm=sorted(A,key=lambda d:d['mean_side'])[:8]
print('smallest 8:')
for d in sm: print(f"  {d['image']} id={d['id']} side={d['mean_side']:.1f} edge_ratio={d['edge_ratio']:.2f} sq={d['squareness']:.3f} edge={d['touches_edge']}")
print('largest:',f"{sm and ''}")
for d in sorted(A,key=lambda d:-d['mean_side'])[:3]: print(f"  {d['image']} id={d['id']} side={d['mean_side']:.1f}")
for f in (0.64,):
    sc=[s*f for s in sz]
    for th in (20,25,30,40,50,60):
        print(f'  at x{f}: below {th}px: {sum(1 for s in sc if s<th)}/{len(sc)} ({100*sum(1 for s in sc if s<th)/len(sc):.0f}%)')
print('current below thresholds:')
for th in (30,50,60):
    print(f'  below {th}px: {sum(1 for s in sz if s<th)}/{len(sz)}')

print('\n=== OBLIQUENESS ===')
er=sorted(d['edge_ratio'] for d in A)
print(f"edge_ratio median={np.median(er):.2f} p90={np.percentile(er,90):.2f} max={er[-1]:.2f}")
print('most oblique:')
for d in sorted(A,key=lambda d:-d['edge_ratio'])[:5]:
    print(f"  {d['image']} id={d['id']} ratio={d['edge_ratio']:.2f} side={d['mean_side']:.1f}")
print('touching frame edge:',[(d['image'],d['id']) for d in A if d['touches_edge']])

print('\n=== DOWNSCALE COMPARISON ===')
for tag,f in (('s064',0.64),('s050',0.5)):
    S=load(tag)
    sseen={i:set() for i in imgs}
    for d in S['detections']: sseen[d['image']].add(d['id'])
    lost=[];gain=[]
    for i in imgs:
        for x in seen[i]-sseen[i]: lost.append((i,x))
        for x in sseen[i]-seen[i]: gain.append((i,x))
    se=[]
    for a,b in itertools.combinations(imgs,2):
        s=sseen[a]&sseen[b]
        if s: se.append((a,b,sorted(s)))
    sadj={i:set() for i in imgs}
    for a,b,_ in se: sadj[a].add(b); sadj[b].add(a)
    sc=comps(set(imgs),sadj)
    print(f'--- x{f}: total={len(S["detections"])} (full={len(A)}) lost={len(lost)} gained={len(gain)}')
    print('  lost:',[(i[-4:],x) for i,x in lost])
    print('  gained:',[(i[-4:],x) for i,x in gain])
    print('  components:',len(sc), sc if len(sc)>1 else '(connected)')
    print('  edges:',len(se),'  weak(1-shared):',sum(1 for e in se if len(e[2])==1))
    ap=[n for n in imgs if len(comps(set(imgs)-{n},sadj))>len(sc)]
    print('  articulation points:',[x[-4:] for x in ap])
    # sizes matched to full
    print('  ids never seen at this scale but seen at full:',sorted({x for _,x in lost}-{d['id'] for d in S['detections']}))
