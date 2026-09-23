"""Draw the detected markers of one capture photo onto a downscaled copy.

Usage: python annotate.py <image-name> <out.png>

Reads detections.json next to this script and <image-name>.png from the PNG folder, which defaults
to png/ next to this script (where detect.py and sweep.py look too); set GLYPH_PNG_DIR to use
another folder."""
import cv2, json, os, sys, numpy as np
here = os.path.dirname(os.path.abspath(__file__))
png_dir = os.environ.get("GLYPH_PNG_DIR", os.path.join(here, "png"))
det = json.load(open(os.path.join(here, "detections.json")))["detections"]
# normalize structure
def rows(d):
    if isinstance(d, dict):
        for k, v in d.items():
            if isinstance(v, list):
                for m in v:
                    yield k, m
    elif isinstance(d, list):
        for m in d:
            yield m.get("image"), m
img_name = sys.argv[1]
out = sys.argv[2]
im = cv2.imread(os.path.join(png_dir, img_name + ".png"))
if im is None:
    sys.exit(f"cannot read {os.path.join(png_dir, img_name + '.png')} (set GLYPH_PNG_DIR)")
n = 0
for k, m in rows(det):
    if k is None or img_name not in str(k):
        continue
    if m.get("rejected_manual"):
        continue
    c = np.array(m["corners"], dtype=np.int32).reshape(-1, 2)
    cv2.polylines(im, [c], True, (0, 0, 255), 6)
    cx, cy = c.mean(axis=0).astype(int)
    mid = m.get("id", m.get("marker_id"))
    cv2.putText(im, str(mid), (cx - 40, cy - 30), cv2.FONT_HERSHEY_SIMPLEX, 3.0, (0, 255, 255), 8)
    n += 1
h, w = im.shape[:2]
s = 1800.0 / max(h, w)
cv2.imwrite(out, cv2.resize(im, (int(w*s), int(h*s))))
print("annotated", n, "markers ->", out)
