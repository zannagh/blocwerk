import json,os,itertools,numpy as np
base=os.path.dirname(os.path.abspath(__file__))
F=json.load(open(base+'/detections_full.json'))
BAD=[('IMG_2771',17)]
for d in F['detections']:
    d['rejected_manual']= (d['image'],d['id']) in BAD
    d['reject_reason']='visually confirmed false positive (no marker present; 12.4px side, edge_ratio 5.4)' if d['rejected_manual'] else None
F['note']='Marker scheme id=segment*6+role, roles 0TL 1TR 2BR 3BL 4H 5V. Physical side assumed 125mm (black square). Detector: DICT_4X4_50, CORNER_REFINE_SUBPIX, adaptiveThreshWinSize 3..53 step 5, minMarkerPerimeterRate 0.01, perspectiveRemovePixelPerCell 8.'
json.dump(F,open(base+'/detections.json','w'),indent=1)
A=[d for d in F['detections'] if not d['rejected_manual']]
imgs=sorted(F['per_image']); seen={i:set() for i in imgs}
for d in A: seen[d['image']].add(d['id'])
edges=[(a,b,sorted(seen[a]&seen[b])) for a,b in itertools.combinations(imgs,2) if seen[a]&seen[b]]
adj={i:set() for i in imgs}
for a,b,_ in edges: adj[a].add(b); adj[b].add(a)
def comps(nodes):
    s=set();out=[]
    for n in nodes:
        if n in s: continue
        st=[n];c=set()
        while st:
            x=st.pop()
            if x in c: continue
            c.add(x); st+=[y for y in adj[x] if y in nodes and y not in c]
        s|=c; out.append(sorted(c))
    return out
print('after filter: dets',len(A),'edges',len(edges),'components',len(comps(set(imgs))))
print('articulation:',[n for n in imgs if len(comps(set(imgs)-{n}))>1])
print('weak(1):',sum(1 for e in edges if len(e[2])==1),'>=2:',sum(1 for e in edges if len(e[2])>=2),'>=4:',sum(1 for e in edges if len(e[2])>=4))
print('weak-only pairs:',[(a[-4:],b[-4:],s) for a,b,s in edges if len(s)==1])
ids=sorted({d['id'] for d in A}); print('ids seen:',ids)
print('missing 0-35:',[i for i in range(36) if i not in ids])
sz=sorted(d['mean_side'] for d in A)
print('sizes n=%d min=%.1f p10=%.1f median=%.1f p90=%.1f max=%.1f'%(len(sz),sz[0],np.percentile(sz,10),np.median(sz),np.percentile(sz,90),sz[-1]))
for th in (20,25,30,40,50,60):
    print(f'  x0.64 below {th}: {sum(1 for s in sz if s*0.64<th)}/{len(sz)}  | current below {th}: {sum(1 for s in sz if s<th)}')
print('smallest 5:',[(d['image'][-4:],d['id'],round(d['mean_side'],1)) for d in sorted(A,key=lambda d:d['mean_side'])[:5]])
# degree/min shared per image
print('per-image: n, degree, max-shared-with-any-neighbour')
for i in imgs:
    ms=max([len(s) for a,b,s in edges if i in (a,b)] or [0])
    print(f'  {i[-4:]} n={len(seen[i])} deg={len(adj[i])} maxshared={ms}')
