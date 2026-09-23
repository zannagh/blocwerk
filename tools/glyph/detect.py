import cv2, numpy as np, json, os, sys, glob, math

ROLES={0:'TL',1:'TR',2:'BR',3:'BL',4:'H',5:'V'}
DICT=cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)

def params(mode):
    p=cv2.aruco.DetectorParameters()
    if mode=='default':
        return p
    if mode=='tuned':
        p.cornerRefinementMethod=cv2.aruco.CORNER_REFINE_SUBPIX
        p.adaptiveThreshWinSizeMin=3
        p.adaptiveThreshWinSizeMax=53
        p.adaptiveThreshWinSizeStep=5
        p.minMarkerPerimeterRate=0.01
        p.perspectiveRemovePixelPerCell=8
    return p

def metrics(c):
    c=np.asarray(c,dtype=float).reshape(4,2)
    e=[float(np.linalg.norm(c[(i+1)%4]-c[i])) for i in range(4)]
    d=[float(np.linalg.norm(c[2]-c[0])),float(np.linalg.norm(c[3]-c[1]))]
    area=0.5*abs(sum(c[i][0]*c[(i+1)%4][1]-c[(i+1)%4][0]*c[i][1] for i in range(4)))
    side=float(np.mean(e))
    return dict(edges=e,mean_side=side,min_edge=min(e),max_edge=max(e),
        edge_ratio=max(e)/max(min(e),1e-9),
        diag_ratio=max(d)/max(min(d),1e-9),
        area_px=float(area),
        sqrt_area=float(math.sqrt(area)),
        squareness=float(math.sqrt(area)/max(side,1e-9)))

def run(img,mode,clahe=False):
    g=cv2.cvtColor(img,cv2.COLOR_BGR2GRAY)
    if clahe:
        g=cv2.createCLAHE(clipLimit=3.0,tileGridSize=(8,8)).apply(g)
    det=cv2.aruco.ArucoDetector(DICT,params(mode))
    corners,ids,rej=det.detectMarkers(g)
    out=[]
    if ids is not None:
        for c,i in zip(corners,ids.flatten()):
            out.append((int(i),np.asarray(c).reshape(4,2)))
    return out,(0 if rej is None else len(rej))

def main():
    scale=float(sys.argv[1]) if len(sys.argv)>1 else 1.0
    tag=sys.argv[2] if len(sys.argv)>2 else 'full'
    base=os.path.dirname(os.path.abspath(__file__))
    recs=[];summary={}
    for f in sorted(glob.glob(os.path.join(base,'png','*.png'))):
        name=os.path.basename(f).replace('.png','')
        img=cv2.imread(f)
        if scale!=1.0:
            img=cv2.resize(img,None,fx=scale,fy=scale,interpolation=cv2.INTER_AREA)
        h,w=img.shape[:2]
        res={}
        for mode in ('default','tuned'):
            for cl in (False,True):
                d,nrej=run(img,mode,cl)
                res[f'{mode}{"_clahe" if cl else ""}']=sorted(set(i for i,_ in d))
        # canonical = tuned, no clahe
        dets,nrej=run(img,'tuned',False)
        ids=[i for i,_ in dets]
        summary[name]=dict(w=w,h=h,rejected=nrej,variants={k:len(v) for k,v in res.items()},
                           variant_ids={k:v for k,v in res.items()},
                           n=len(dets),ids=sorted(ids),
                           dupes=sorted(set(x for x in ids if ids.count(x)>1)))
        for i,c in dets:
            m=metrics(c)
            recs.append(dict(image=name,id=int(i),segment=int(i)//6,role=ROLES.get(int(i)%6,'?'),
                role_idx=int(i)%6,corners=[[float(x),float(y)] for x,y in c],**m,
                img_w=w,img_h=h,
                touches_edge=bool(c[:,0].min()<5 or c[:,1].min()<5 or c[:,0].max()>w-5 or c[:,1].max()>h-5)))
    json.dump(dict(scale=scale,detections=recs,per_image=summary),
              open(os.path.join(base,f'detections_{tag}.json'),'w'),indent=1)
    print(f'--- scale={scale} tag={tag}')
    for k,v in summary.items():
        print(k,v['w'],'x',v['h'],'n=',v['n'],'ids=',v['ids'],'dupes=',v['dupes'],'variants=',v['variants'])
    print('total',len(recs))
main()
