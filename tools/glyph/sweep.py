import cv2,numpy as np,glob,os,itertools,json
D=cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
base=os.path.dirname(os.path.abspath(__file__))
files=sorted(glob.glob(os.path.join(base,'png','*.png')))
grays={os.path.basename(f)[:-4]:cv2.cvtColor(cv2.imread(f),cv2.COLOR_BGR2GRAY) for f in files}
def mk(**kw):
    p=cv2.aruco.DetectorParameters()
    for k,v in kw.items(): setattr(p,k,v)
    return p
cfgs={
 'A_default':dict(),
 'B_subpix':dict(cornerRefinementMethod=cv2.aruco.CORNER_REFINE_SUBPIX),
 'C_subpix_win':dict(cornerRefinementMethod=cv2.aruco.CORNER_REFINE_SUBPIX,
    adaptiveThreshWinSizeMin=3,adaptiveThreshWinSizeMax=53,adaptiveThreshWinSizeStep=5),
 'D_C_perim':dict(cornerRefinementMethod=cv2.aruco.CORNER_REFINE_SUBPIX,
    adaptiveThreshWinSizeMin=3,adaptiveThreshWinSizeMax=53,adaptiveThreshWinSizeStep=5,
    minMarkerPerimeterRate=0.01),
 'E_D_cells':dict(cornerRefinementMethod=cv2.aruco.CORNER_REFINE_SUBPIX,
    adaptiveThreshWinSizeMin=3,adaptiveThreshWinSizeMax=53,adaptiveThreshWinSizeStep=5,
    minMarkerPerimeterRate=0.01,perspectiveRemovePixelPerCell=8),
 'F_E_strictborder':dict(cornerRefinementMethod=cv2.aruco.CORNER_REFINE_SUBPIX,
    adaptiveThreshWinSizeMin=3,adaptiveThreshWinSizeMax=53,adaptiveThreshWinSizeStep=5,
    minMarkerPerimeterRate=0.01,perspectiveRemovePixelPerCell=8,
    maxErroneousBitsInBorderRate=0.1,errorCorrectionRate=0.4),
 'G_F_nopoly':dict(cornerRefinementMethod=cv2.aruco.CORNER_REFINE_SUBPIX,
    adaptiveThreshWinSizeMin=3,adaptiveThreshWinSizeMax=53,adaptiveThreshWinSizeStep=5,
    minMarkerPerimeterRate=0.01,perspectiveRemovePixelPerCell=8,
    maxErroneousBitsInBorderRate=0.1,errorCorrectionRate=0.2,polygonalApproxAccuracyRate=0.02),
}
res={}
for name,kw in cfgs.items():
    det=cv2.aruco.ArucoDetector(D,mk(**kw))
    tot=0;bad=0;dup=0;per={}
    for k,g in grays.items():
        c,ids,_=det.detectMarkers(g)
        ids=[] if ids is None else list(ids.flatten())
        tot+=len(ids); bad+=sum(1 for i in ids if i>35)
        dup+=len(ids)-len(set(ids))
        per[k]=sorted(int(i) for i in ids)
    valid=[i for k in per for i in per[k] if i<=35]
    res[name]=dict(total=tot,invalid_id=bad,dup=dup,valid_unique_ids=sorted(set(valid)),per=per)
    print(f'{name:18s} total={tot:4d} id>35={bad:3d} dups={dup:3d} uniq_valid={len(set(valid))}')
json.dump(res,open(os.path.join(base,'sweep.json'),'w'),indent=1)
