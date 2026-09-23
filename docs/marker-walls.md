# Marker walls: admin guide

This guide is for whoever runs a Blocwerk server and for anyone who administers a wall on it. It
covers the printed-marker ("glyph") features: what they need, how to set them up, how to capture a
wall, how to read the 3D view, and how to run the compute services behind them.

The features are **experimental**. Everything described here is opt-in, and a wall that never turns
markers on keeps working as before.

---

## 1. What this is and who it's for

Blocwerk's normal model is a grid of **panel photos**: people browse them and set boulders on them.
Every hold position is stored relative to its own photo. That works well, but the app knows nothing
about the real wall: not its size, not its angles, and not how one photo relates to the next beyond
"these two panels are neighbours".

**Printed ArUco markers** fixed to the wall give the app that missing information. From the markers
it can work out:

- **real sizes** (hold widths in millimetres),
- **where each photo sits on the wall**, so holds line up more reliably when you update the wall,
- **a 3D model of the wall**: every surface with its measured angle, plus an optional photo-real view.

Markers are **optional, per wall**. You don't have to stick paper squares on your wall to use
Blocwerk, and a wall without markers doesn't lose anything it had before.

### What works on every wall, markers or not

Some improvements from this work don't need markers, so they apply to **every** wall:

| Feature | What it does | Where |
|---|---|---|
| Hold outlines | New auto-detected holds get their real traced shape instead of a circle, when the tracing is confident. Otherwise they stay circles. | Automatic at photo upload |
| Pocket holds | Outlines keep the hole of a pocket or donut hold and draw it as a hole. Tapping the hole still selects the hold. | Automatic |
| Fingerprints | Each hold gets a small appearance fingerprint (colour and shape) that the matcher uses to recognise it later. | Automatic at photo upload |
| "Possibly moved" | During a wall update, holds that vanished from their spot but look like a new hold elsewhere are offered as suggestions. | Wall update review |
| Outline upgrade | A one-click upgrade that traces real outlines for holds that are still circles. | Wall Settings → Wall Shape → Hold outlines |
| Stricter photo alignment | If a new photo can't be lined up with the old one, the update now says so instead of silently carrying holds to the wrong place. | Wall update review |

**Tiled hold detection** is now the default on every wall. The detection model looks at 640 px
images, so a whole 4032 px photo used to be shrunk about six times and most small holds (feet,
kickboard) disappeared. Now the model runs on overlapping full-resolution windows instead (1280 px
tiles, 256 px overlap, confidence 0.35). Server owners can tune or switch it off with
`HoldDetection:Tiling:*` (`HOLDDETECTION__TILING__ENABLED`, `…__TILESIZE`, `…__OVERLAP`,
`…__CONFIDENCE`, `…__SAMPLING`). `ENABLED=false` brings back the old whole-image detection without a
deploy.

### What needs markers

Only the marker-dependent parts are gated behind the per-wall switch:

- marker detection on photos,
- millimetre hold sizes and hold positions on the wall's surfaces,
- the 3D model, its textures and the photo-real view,
- marker-seeded photo alignment and wall-space matching during updates.

Blocwerk never runs marker detection on a wall that hasn't declared markers.

---

## 2. Markers

### What they are

An ArUco marker is a black square with a pattern of black and white cells inside, like a very
simple QR code. Software can find its four corners in a photo precisely and read its number (its
**id**). Because the app knows the marker's real size, the four corners tell it how far away and at
what angle the surface is.

Blocwerk uses:

| Property | Value |
|---|---|
| Dictionary | `DICT_4X4_50` (4×4 cells, ids 0–49 exist) |
| Ids used | **With a marker plan:** exactly the plan's ids, any of 0–49. **Without a plan:** 0–35 only. Anything else is treated as a false detection and ignored. |
| Id meaning | **With a marker plan:** none; the plan says which surface each id is on. **Without a plan (legacy scheme):** `id = segment × 6 + role`. Segments 0–5; roles 0 = top-left, 1 = top-right, 2 = bottom-right, 3 = bottom-left, 4 = horizontal filler, 5 = vertical filler. |
| Size | The side of the **black square**, not the paper sheet. Default 125 mm. With a plan, each marker has its own size; without one, the wall has one size. |

In the legacy scheme, marker 7 is segment 1, role 1 (top-right of segment 1), and marker 24 is
segment 4, top-left. Walls without a marker plan still work this way. The roles are only a naming
convention. The solver works out which surface a marker is on from the
photos (see "Markers mark the plane" below), so a spare marker on the "wrong" segment is fine.

Print them with any ArUco generator that offers the 4×4 / 50 dictionary. Print on matte paper if you
can, because glare on glossy prints is the main reason markers fail to decode. Measure the black
square after printing: printers scale.

### Sizes

| Use | Size | Why |
|---|---|---|
| Corner markers (roles 0–3) | **125 mm** | Measured on a real wall: a 125 mm square triangulated back to 125.5 mm. These anchor each surface and are the ones you can least afford to lose. |
| Filler markers (roles 4–5) | 80 mm is viable | At 80 mm the photo set still held together, but a corner seen in only one photo was lost. Only use smaller fillers if every marker is well covered. |

**Mixed sizes:** a marker plan (below) stores a printed size **per marker**, and the planner often
suggests different sizes for corners and fillers. Without a plan, a wall still has **one** marker
size (the "Marker size" field), so if you have no plan, use one size for all markers on that wall.
The in-app capture reads each marker's size from the plan (see "Using the plan with your photos"
below).

Printed sheets at different sizes share ids. Size can't be worked out from the id, so it always has
to be declared.

### Placement rules

These were learned the hard way on a real wall.

1. **Keep every marker fully inside the photo, with a margin.** A marker that is cut off by the
   edge of the frame, even by a few centimetres, is **silently dropped**. It isn't reported as
   "partly seen"; it's simply missing. The largest, sharpest marker in one test photo was lost this
   way.
2. **Every corner marker in at least 3 photos.** No single photo should be the only evidence for a
   surface's corner.
3. **Shoot markers face-on, not at a grazing angle.** A marker on a near-horizontal face (a
   kickboard rail, the top of a volume) is squashed into a thin sliver in a photo taken from
   standing height and **will not decode**. This is a limit of the method, not a setting. Put
   markers on faces the camera sees roughly head-on, or take extra photos aimed at them.
4. **Markers mark the plane, not the corner.** A marker only needs to lie flat on its surface. If a
   hold is where you wanted the marker, put the marker beside it. The app never uses marker positions
   as a surface's outline and never derives "vertical" from a line through two markers.
5. **A folded surface needs markers on each side.** If a piece of wall has a bend (a corner inside
   one panel, a kinked volume), give each flat side at least two markers of its own. The solver
   splits a segment into separate flat facets only when each side has two or more markers; a single
   odd marker is treated as a bent or badly stuck sheet.
6. **Stick them flat.** A curled or bent sheet is detected but gives a poor fit. The solver lowers
   the weight of markers that disagree badly, but a flat sheet is better.
7. **Don't cover them with anything shiny.** Glare is the most common reason a marker isn't read.

### Planning markers with the marker planner

In the marker planner you sketch your wall as an unfolded net of rectangles and triangles, give
each piece its angle and rough size, and say how far away you usually take photos. The planner then
suggests where to put markers and at what size, and prints them with a placement map. A plan records
which marker sits on which surface and at what size, so with a plan marker ids no longer need to
encode segment and role. The file format is described in `tools/glyph/marker-plan.schema.md`.

Where: **Wall Settings → Wall Shape → Printed markers → Plan marker placement →**, or directly at
`/walls/{id}/markers/plan`. Only the wall's owner and admins can use it, and never from a kiosk
tablet. The page opens with a short "How this works" list; once a plan is saved it starts collapsed.

#### 1. Draw the surfaces

A new plan starts with one rectangle, "main wall" (4000 × 3000 mm, vertical). This is the **root**
surface: the one you stand in front of when you take photos. Everything else is attached to it,
directly or through other surfaces.

- Under **Surfaces**, pick a surface and press **+ Rectangle** or **+ Triangle**. The new piece is
  attached to the selected one on its next free edge (right, top, left, bottom, then hypotenuse),
  1 m deep and tilted like its parent. **Delete surface** removes the selected one. The root and
  surfaces that others hang off can't be deleted.
- Tap a surface on the drawing, or its chip in the list, to edit it:

| Field | Meaning |
|---|---|
| **Name** | e.g. "main wall", "kickboard", "left triangle". |
| **Shape** | **Rectangle** or **Right triangle**. For a triangle, **Right angle at** picks which corner holds the right angle. |
| **Width (mm)** | The rectangle's width, or the triangle's horizontal leg. |
| **Height along the surface (mm)** | Measured along the surface, not straight up: for an overhang, the length up the slope. For a triangle, its vertical leg. |
| **Angle** | Tilt from vertical. Pick **Slab** (the top leans away from you), **Vertical** or **Overhang** (the top leans over you), then type the size in degrees. Picking Slab or Overhang starts at 10° or 30°. A small side drawing shows the lean. Under 2°, a surface counts as vertical and becomes a plumb reference for the photos. |
| **Turn** | How far the surface is turned about the vertical axis relative to the **root** surface, seen from above. Pick **Turned left**, **Straight** or **Turned right**, then type the size (starts at 90°). A small top view shows the turn. A side wall is about 90°. |

The buttons exist because the iPhone number pad has no minus key. The value is still stored signed,
and the planner describes it in words ("30° left", "straight", "90° right") rather than as a signed
number:

- **Angle:** overhang is positive, slab negative.
- **Turn:** **positive = turned left**. You turn left to face the surface square-on, so it sits on
  your **left** and its face points to your right. Turned right is negative. This is the same sign
  the solver and the 3D model use. On The Attic, the left side triangle measured **+89°**.

**Yaws from the old wall-segment editor may have the wrong sign.** The segment editor's old facing
toggle labelled a positive yaw "Side (right)", the opposite of what the maths did. The toggle is gone,
but a yaw someone entered through it can carry the opposite sign. If a side surface appears on the
wrong side, flip its sign.

- **Attached to** (every surface except the root) says how it joins its neighbour, like a cardboard
  net: **Surface** (the neighbour), **Its edge** (top, right, bottom, left or hypotenuse of the
  neighbour), **This surface's edge** (which of its own edges touches it) and **Offset along it
  (mm)**. The offset slides this surface along the neighbour's edge, measured from that edge's lower
  (or left) end. Negative offsets are allowed.

The drawing is the wall unfolded flat, redrawn after every change. Each surface's own coordinates
start at its bottom-left corner, with x to the right and y up the surface.

#### 2. Photos: distance and camera

Under **Photos**:

- **Usual photo distance**, from the camera to the wall. Type it in mm, cm or m: "2500", "2.5 m" and
  "250 cm" all work. **A bare number below 30 is read as metres** ("2.5" = 2.5 m), from 30 up as
  millimetres ("2500" = 2.5 m). It's shown back in metres.
- **Camera**: **Phone, main camera (1×)** (about 69° wide, 4032 px), **Phone, ultra-wide (0.5×)**
  (about 104°, 4032 px), or **Other camera…**, which asks for the horizontal field of view (10–150°)
  and the photo's long edge in pixels (640–20000).

Underneath, the page shows how much wall one photo covers (e.g. about 6.4 × 4.8 m at 2.5 m with the
ultra-wide). Distance and camera decide how big the markers must be printed and how far apart they
can be.

#### 3. Generate markers

Under **Markers**, press **Generate markers**. The generator places:

- a **corner** marker in every corner of every surface (pulled inside by a small gap), and
- **filler** markers along every edge, and inside big surfaces, no further apart than half the
  height of one photo, so every photo sees several.

Markers on both sides of a shared edge are wanted: they tie the two surfaces together. Ids are
numbered 0, 1, 2, … surface by surface, corners first.

Sizes are chosen per surface from the printable sizes **50, 80, 100, 125, 150 and 200 mm**: the
smallest size that comes out at least **60 px** on your photos for corners and **40 px** for fillers.
Surfaces that lean or turn away from you look smaller on the photo, so they get bigger markers. A
surface turned more than 72° away from the standing camera (side walls, near-horizontal faces) is
marked for a face-on photo and sized for that instead. For example, at 2.5 m with the phone
ultra-wide, the 45° main wall of the reference wall gets 150 mm corners and 100 mm fillers, and its
vertical kickboard 100/80 mm.

Pressing **Regenerate markers** later asks first: it replaces **all** markers, including ones you
moved or resized.

#### 4. Adjust the markers

The generator doesn't know where your holds are. Move any marker that lands on a hold.

- **Drag** a marker on the drawing to move it. It snaps to 5 mm.
- **Tap** a marker to open the **Marker** card. It shows the marker's estimated size on your photos
  against its target, and lets you set the **Id**, the **Printed size** (picked from the list of
  sizes), exact **X** / **Y** in mm, and the **Role** (corner or filler).
- **+ Marker**, then tap a surface, adds a marker there.
- **Delete marker #n** removes it.

#### 5. The Check list

**Check** lists what the validator found, errors first. Tap an entry's "Marker #n" or "Surface #n"
link to jump to it. **Errors block Save; warnings don't.** The main codes:

| Code | Severity | Meaning |
|---|---|---|
| `photo-distance`, `photo-fov`, `photo-resolution` | error | Implausible photo setup. |
| `segment-size`, `segment-duplicate-index` | error | A surface has a bad size, or two share an index. |
| `net-no-root`, `net-several-roots`, `attachment-*`, `net-overlap` | error | The net can't be laid out: no root or several, a bad edge or offset, a cycle, or surfaces overlapping in the drawing. |
| `marker-duplicate-id`, `marker-id-range`, `marker-segment`, `marker-size`, `marker-outside`, `marker-overlap` | error | A bad marker: id used twice or outside 0–49, unknown surface, bad size, not fully on its surface, or overlapping another. |
| `too-many-markers` | error | More than 50 markers (the dictionary only has 50 ids). |
| `segment-few-markers` | error | Fewer than 3 markers on a surface. |
| `segment-bunched` | warning | A surface's markers sit in one patch; spread them towards its corners. |
| `marker-too-small` | warning | The marker will come out smaller than its target on your photos. The message names the size to print. |
| `grazing-surface` | warning | The surface is too edge-on for the standing camera: photograph it face-on. |
| `shared-edge-uncovered` | warning | A shared edge doesn't have markers close to it on both sides. |
| `tip-full-frame`, `tip-corner-photos`, `tip-marker-large` | warning | Reminders from the first real capture (full frame, corners in 3+ photos, and similar). |
| `mounting-holes` | error | The mounting-hole sizes are out of range (see "Mounting holes" under step 8). It's checked even while the box is unticked, because the sizes are saved with the plan. |
| `mounting-holes-tight` | warning | The white border the holes leave is too thin for your photos (see step 8). |
| `tip-screw-bias` | warning | The plan prints no mounting holes: screws near the black square can shift detected corners. Use mounting holes or tape. |

When the list is empty it reads *"No problems found — this plan is ready to print."*

#### 6. Start from the measured wall

If the wall already has an active 3D model, **Start from measured wall** (asks first) replaces the
plan in the editor with one built from it: one rectangle per measured surface, overhang and yaw from
the measurement, and each marker where it was found, with its id and size kept. The outlines are
marker spans plus a margin, not the real edges, and triangular pieces come out as rectangles, so
correct the surfaces before you save.

#### 7. Save, download, import

Under **Save & export**:

- **Save plan** stores the plan with the wall. It's disabled while the Check list has errors (*"Fix
  the errors above to save. You can still download to look at a draft."*). After saving the button
  reads **Saved**.
- **Download PDF** and **Download JSON** download the plan **as it is in the editor**, saved or not.
  A PDF of a plan that still has errors is marked as a draft. Markers with ids outside 0–49 can't be
  printed.
- **Import a marker-plan.json** replaces the plan in the editor (at most 1 MB). Nothing is saved until
  you press **Save**. If the file is refused, the reasons are listed, each naming the field.

#### 8. Print

The PDF has an overview page (photo setup, placement map, instructions), a placement table (id,
surface, role, x, y, size and estimated pixels for each marker), then **every marker at true size**
with a white border, a dashed cut line, a **TOP** label and its id, surface, position and size. Markers
go on A4, or A3 when they're too big for A4.

- Print at **100 % / "actual size"**. Turn off **"fit to page"** or any scaling.
- Every page has a **100 mm bar** at the bottom. Measure it, and measure the black square of one
  marker, before you cut. If they're off, fix the print settings and print again.
- Cut on the dashed line and **keep the white border**: it's part of the marker.
- Print on matte paper if you can (see "What they are").

**Mounting holes.** If you screw markers to the wall rather than taping them, let the PDF mark the
screw holes. Screws placed by eye tend to land near the black square, and a screw head there pulls
the detected corners off. The settings are in the planner's **Printing** card and are saved with the
plan (they're part of the JSON):

| Field | Meaning |
|---|---|
| **Mounting holes** | Tick to print four holes around every marker. |
| **Hole size** | The hole you drill or punch: **1, 2, 2.5, 3 or 3.5 mm**. Default 3 mm. |
| **Screw head Ø (mm)** | Your screw head's diameter: at least 0.5 mm wider than the hole, at most 15 mm. Default 6 mm, a typical 3 mm wood screw. A head that no longer clears a bigger hole grows to the smallest allowed size. |
| **Gap to marker (mm)** | White paper between the screw head's rim and the corner of the black square. Default **1 mm**. |
| **Gap to cut edge (mm)** | Paper between the screw head's rim and the cut line. Default **1 mm**. |

Both gaps take 0.5–10 mm. Sizes out of range show up in the Check list as `mounting-holes` errors.

Each hole sits **on the diagonal, outward from one corner** of the black square. It's placed so the
head's rim is exactly the "gap to marker" away from that corner, and the cut line is the "gap to cut
edge" beyond the head. The cut-out stays only a little bigger than the marker. The white border has
the same width whatever the marker's size. The card shows how far out the
holes sit and how big a 125 mm marker's cut-out gets. With both gaps at 1 mm:

| Screw head | Hole centre from the corner (along the diagonal) | White border | Cut-out of a 125 mm marker |
|---|---|---|---|
| 4 mm | 3 mm | 5.1 mm | ≈ 135.2 mm |
| 6 mm (default) | 4 mm | 6.8 mm | ≈ 138.7 mm |
| 8 mm | 5 mm | 8.5 mm | ≈ 142.1 mm |
| 10 mm | 6 mm | 10.2 mm | ≈ 145.5 mm |

The paper grows to fit the border (A4, or A3 when needed).

**The "tight holes" warning.** A thin white border is fine for *finding* the marker, but not for
measuring its corners exactly. In tests, once the white strip came out narrower than **about 3
pixels** in the photo, the detected corners drifted toward the wall by up to 1.5 px. With 3 px or
more they stayed within 0.5 px. The distance between the head and the square made no measurable
difference. So the planner compares each marker's border with the size it will have in *your*
photos: it uses the photo distance and camera from the Photos card, and the marker's surface
(surfaces that lean or turn away come out smaller). If any marker's border falls under ~3 px, the
Check list shows a `mounting-holes-tight` warning. It names the largest affected marker size, its
border in mm and photo pixels, and the gaps that measured clean for every marker, with the cut-out
they give. The cut-edge gap grows first; the holes only move out when the edge gap alone can't reach
the border. If no allowed gap is enough, the warning asks you to keep that much white paper around
the square (cut outside the printed line), or to tape those markers instead.

Roughly, for a face-on surface with a 4032 px photo: the 1× camera at 2.5 m needs about 2.6 mm of
border, and the ultra-wide at 2.5 m needs about 4.8 mm. The default 6.8 mm covers both. The
ultra-wide at 5 m needs about 9.5 mm; the warning then recommends 1 mm to the marker and 4 mm to the
cut edge (a 145 mm cut-out for 125 mm markers). It's only a warning, so it doesn't block Save.

Without mounting holes, the Check list adds the `tip-screw-bias` reminder and the PDF tells you to
tape the markers, or screw well clear of the black square.

**Printing and mounting with holes.** Each marker page shows every hole at its true diameter with a
crosshair to drill or punch, and a **dashed circle** for the screw head. A legend at the top of the
page repeats the hole and head size. Print at 100 % as above, cut on the dashed cut line, and
**screw through the marked holes, keeping the screw heads inside the dashed circles.** A bigger head,
or a screw off its mark, covers the white gap next to the corner.

**Thin white borders are fine.** Marker detection used to need a generous white border, especially
on darker walls. With a thin border, the paper's outline could be read as a second, bigger marker,
and screw heads close to the corners pulled the corners onto themselves. Now the detector keeps the
black square itself: OpenCV's filter that throws away close duplicates is switched off, and copies of
one marker found inside each other are merged into the black square. The corner refinement also runs
again from its own result until the corners settle, so it no longer depends on which outline the
detector returned first. In tests, every marker decoded with any border and any head gap down to
0.5 mm, including the 1 mm / 1 mm default on 50 and 125 mm markers. The only remaining limit is
corner accuracy, which is the ~3 px border above.

#### 9. Place the markers

- Stick or screw each marker **flat** on its surface with its centre at the (x, y) from the placement table,
  measured from the surface's bottom-left corner, y up the surface. Within about 2 cm is fine; the
  photos measure the exact spot.
- The **TOP** edge points up the surface. Never bend a marker over an edge.
- Photograph surfaces marked **SHOOT FACE-ON** square-on, not from the side.
- The placement rules above still apply.
- **Keep the JSON with your photos.** Store `…-marker-plan.json` with every photo dump of this wall. It
  says which marker is where, whatever ids you used.

#### Using the plan with your photos

A capture (section 4) uses the wall's saved plan automatically. You can also attach a plan to one
capture: the upload panel has a **Marker plan (JSON, optional)** field next to the photos. Pick the
`…-marker-plan.json` from the planner there. The panel then says which plan is in use (*"Using the
uploaded marker plan: 3 surfaces, 24 markers."*), and **Don't use it** drops an uploaded plan again.

- **Which plan counts.** An uploaded plan becomes the wall's saved plan **only when the wall has
  none** (*"Saved as this wall's marker plan (it had none)."*). If the wall already has a plan, the
  upload applies to this capture only and the saved plan is left alone. To replace a saved plan, use
  the marker planner. Without an upload, the saved plan is used. Without either, the capture falls
  back to the legacy scheme (see below).
- **Errors refuse the plan.** An uploaded plan is checked like one in the planner. If it can't be
  read or has any errors, it isn't used and the reasons are listed (*"This marker plan can't be
  used:"*). Warnings don't block it.
- **New ids mean a fresh search.** If the plan adds ids that the photos weren't searched for yet,
  detection runs again when you press Compute (*"The photos will be searched for the plan's markers
  again when the computation starts."*).
- **Declarations are pre-filled.** The declarations table gets one row per planned surface seen in
  the photos, with the plan's name and angle, and surfaces within 2° of vertical ticked as gravity
  references. Check them before computing.
- **The solver knows each marker.** The solve request carries every marker's surface and printed
  size (`idScheme: "plan"`), so ids don't have to encode segment or role, and any id 0–49 works. A
  marker stuck on another surface than planned is still moved to the surface it actually lies on.

**Placement check.** After the solve, the capture compares the result with the plan and lists what
to check in **Capture history**, under that capture:

- a marker never seen in any photo: *"Marker 12 (segment 1 (“kickboard”)) was never seen in any
  photo — is it on the wall, and in the photos?"*
- a marker seen but not placed: *"… was seen but could not be placed — make sure it shows fully in
  at least two photos."*
- a marker on another surface than planned: *"Marker 7 was planned on segment 0 (“main wall”) but
  was found on segment 1 (“kickboard”)."*
- a marker more than **100 mm** from its planned spot: *"Marker 3 is 140 mm away from its planned
  spot on segment 0 (“main wall”)."*
- a surface more than **5°** off its planned angle: *"Segment 0 (“main wall”) measured 38.5°, but
  the plan says 45°."*

When nothing is off it reads *"Marker placement matches the plan (… markers compared)."* Positions
are compared after fitting the plan onto each measured surface, so a whole surface drawn a bit off
doesn't flag every marker.

The planned positions only feed this check. The solver doesn't use them as a starting point; it
takes only each marker's surface and size from the plan.

**Walls without a plan** use the legacy scheme: ids 0–35 only (higher ids are ignored), surface =
`id ÷ 6`, and one marker size for the whole wall. The planner's generator numbers markers 0, 1, 2, …
and doesn't follow that scheme, so if you print from the planner, save the plan (or attach it to the
capture) rather than renumbering.

---

## 3. Declaring markers

A wall only uses markers after someone declares that the marker sheets are on it. The question is
asked in three places:

| Where | Label | Notes |
|---|---|---|
| Creating a wall | **Printed markers** → "This wall has printed ArUco markers" | Also asks for the **Marker size** (mm, side of the black square). It's used for every marker unless the wall has a marker plan, which sets each marker's size. |
| Wall Settings → **Wall Shape** → **Printed markers** | "This wall has printed ArUco markers", then **Save marker settings** | Admins only. Not shown on a kiosk tablet. |
| Every wall update and every added panel | "This wall has ArUco markers (applies to the whole wall)" | Defaults to the wall's current setting. |

Declare markers only once the sheets are actually fixed to the wall. The size field appears only
while the box is ticked.

### Turning markers off

The declaration during an update applies to **the whole wall**, not just the photos in that update.
Unticking it shows: *"Unticking this switches markers off for the whole wall, not just these photos.
Saved when you continue."*

With markers off:

- no marker detection runs on new photos,
- marker-only features (millimetre sizes, marker-seeded alignment) stop for new photos,
- everything in the "works on every wall" table keeps working.

Turning it off doesn't delete measured models or captures: the switch only changes the wall's setting. The "View in 3D" link
depends on the wall having an active model, not on the switch.

---

## 4. Capturing the wall

A **capture** is a set of photos taken to build the 3D model. It's separate from the panel photos:
capturing never changes panels, holds or boulders.

Where: **Wall Settings → Wall Shape → Printed markers → Compute from photos** (only shown once the
wall has markers declared). If the section says *"The 3D computation service isn't configured on
this server"*, the server owner needs to set up the `wall-geometry` service (section 9). You can
still import a `wall-geometry.json` by hand.

### How many photos

- **10–40 photos**, JPEG or PNG, up to 20 MB each. The app takes 40 at most; extra files are left
  out.
- Together they must show **every marker, each in at least two photos** (three for corners; see
  section 2).
- Upload the **camera originals**. The app reads the focal length from the photo's EXIF data.
  Photos without it are flagged: *"No focal length in the photo's EXIF — upload the camera
  original."* Screenshots and photos exported from messaging apps usually lack it.

### How to shoot them

- **Use one lens.** Don't switch between the phone's ultra-wide and main camera within a capture.
  Mixing works, but each lens needs its own calibration and gives the solver less to go on.
  iPhones correct most ultra-wide distortion in the saved photo, so the ultra-wide is fine for the
  model.
- **Overlap generously.** Neighbouring photos should share several markers. A photo that shares
  only one marker with the rest is only loosely attached.
- **Stand back and face the wall**, then add angled shots for faces the front photos see at a
  grazing angle (kickboard rails, sides of volumes, side panels under an overhang).
- **Keep every marker fully in frame** in the photos you rely on for it.
- **Good even light, no zoom changes, no people in the frame.**

#### Extra for a good photo-real view

The photo-real view (section 6) needs many more views than the model does. From the feasibility
study:

- walk the wall slowly at **three heights**: crouched and aimed up, chest height, arms up and aimed
  down;
- at each end, walk a **quarter arc** around the corner so the ends get oblique views;
- go **under volumes and overhangs** and look up into them, otherwise they turn into blobs;
- keep markers in some of the frames so the result can be aligned with the model;
- every spot of the wall should appear in 3+ photos taken from different positions.

The study's recipe is a slow video walk. The in-app capture takes at most 40 photos, so for the
photo-real view it also takes **one optional walk-along video**. Once photos are uploaded, the field
**Walk-along video (optional, for the photo-real view)** appears under the photo list. It only
appears when the server has a splat worker configured (`SPLATSERVICE__URL` set), and only for wall
admins, never on a kiosk tablet.

How to film it:

- **30–90 seconds**, MP4 or MOV, up to 1 GB and at most 10 minutes long.
- Use the phone's **1× camera**. The ultra-wide's edges are soft and its distortion hurts matching.
- Walk the whole wall **slowly** at the **three heights** above, the camera roughly square to the
  wall, about 1–2 m away. Good light and a slow pace avoid motion blur.
- At each end, walk a **quarter arc** around the corner, and look under the volumes and overhangs.
- Keep the marker photos too: the video doesn't replace them.

The upload streams to the server with a progress bar, and the draft then shows the video's name,
size and length, with **Remove**. When the capture runs, the server takes up to 120 sharp frames from
the video (about 2.5 per second, the sharpest of every three decoded frames, rotation applied,
≤ 1920 px, all metadata removed) and deletes the video.

**The frames only feed the photo-real view, never the measurement.** They aren't marker photos,
never count against the 40, never go into the solve and can't become panel photos. The photo-real
view is still aligned with the model through the marker photos alone. Without a video, a normal
10–40 photo capture still produces a photo-real view; it's just softer and fails from the side and
from above.

The video walk hasn't been tried with a real wall video yet: how much it improves the side and
below views, and how long the worker takes with 120 extra frames, are still open (to confirm).

### The upload table

After the upload, each photo is listed with its size, focal length and the marker ids found in it.
A photo with *"No markers found — this photo will not be used."* is ignored by the solver. Use
**Remove** to drop a photo.

### The declarations table

Below the photos there is one row per marker segment seen in the photos. With a marker plan, a
segment is a planned surface and the rows are pre-filled from the plan (section 2); without one,
segment `n` is ids `6n`–`6n+5`:

| Column | What to enter |
|---|---|
| **Name** | A name for the piece, e.g. "main wall", "kickboard". |
| **Angle** | Tilt from vertical as you know it: pick **Slab** or **Overhang**, then type the size (e.g. Overhang 45, Slab 12). Leave it at "—" if you don't know it. Optional. |
| **Vertical** → "Use as gravity reference" | Tick **only** for pieces that are truly plumb. |

**Why vertical references matter.** The photos alone don't say which way is up. Two surfaces that
are truly vertical and face different directions pin down gravity exactly. The angles you type in
are then only compared with what was measured; they aren't used as input. With fewer than two
independent references the model still gets millimetres right, but angles show as unknown.

**Leave a row empty for spare markers.** If you reused markers from a segment you don't have (e.g.
segment-4 markers as extra fillers on the main wall), leave that row's name and angle blank. Empty
rows aren't declared, so the solver attaches those markers to whatever surface they sit on. A
declared segment is never merged into another one, so filling in a spare row would split its
markers off into a separate surface.

**Markers at equal height (optional).** Enter pairs of marker ids you know are level with each
other, e.g. `14-15, 8-9`. They're an extra gravity hint. Only enter pairs you have measured: a wrong
pair tilts the whole model (see Troubleshooting).

**Notes (optional)**: free text shown in the capture history, e.g. "capture after the reset".

Then press **Compute 3D model**. **Discard photos** deletes the uploaded photos after a
confirmation. A draft nobody starts is removed after a day.

### What happens on the server

1. **Finding markers**: the app reads each photo and detects and refines the markers.
2. **Computing the 3D model**: the marker observations (numbers only, no photos) go to the
   `wall-geometry` service, which solves the surfaces, their angles, gravity and camera positions.
3. The new model **becomes the wall's active model** at this point.
4. **Rendering textures**: the photos go to the service, which renders one straightened image per
   flat surface for the 3D view.
5. **Photo-real view**, only if a splat worker is configured: with a walk-along video the server
   first extracts its frames (*"Photo-real view: extracting video frames"*); then the photos (and the
   frames) go to the splat worker, which trains the photo-real view. This can take tens of minutes to
   hours. A video that can't be read only costs its frames: the view is then trained from the photos.
6. A **push notification** tells the person who started the capture: *"3D model ready for
   {wall}."* A second one follows when the photo-real view is ready. People can opt out under
   their notification preferences ("A 3D wall model you started is ready").

You can leave the page. While the capture runs, the panel shows the stage and a progress bar, and
refreshes about every two seconds.

### Statuses

| Status shown | Meaning |
|---|---|
| Uploading | Draft: photos are being added; nothing runs yet. |
| Waiting to start | Submitted; waiting for the capture worker (one capture runs at a time). |
| Finding markers | Reading photos and detecting markers. |
| Computing the 3D model | The geometry service is solving. |
| Rendering textures | The model is already active; surface images are being made. |
| Model ready · making the photo-real view | Model and textures are live; the splat worker is training. |
| Model ready | Done. |
| Model ready (no textures) | The model is active, but textures failed. The 3D view works without photos on the surfaces. The error is shown underneath. |
| Model ready (no photo-real view) | The model and textures are live; only the photo-real step failed. |
| Failed | Nothing was activated. The error is shown in plain words in **Capture history**. |

A capture is retried up to 3 times (including after a server restart) before it's marked failed.

### Model history and imports

- **Measured geometry** shows the active model's surfaces and their measured angles.
- **Match segments to markers**: pick which marker segment sits on each of your wall's segments.
  Its measured angle is then shown next to the angle you set; your own angle is kept.
- **Import wall-geometry.json**: upload a model computed elsewhere (e.g. with the command-line
  solver).
- When there's more than one model, the history lists them. **Activate** makes an older one active
  again.

### Retention

Capture photos are stored on disk and deleted **30 days** after the capture ends (by default).
Photos of the capture that produced the wall's **active** model are kept. A sweep runs every six
hours. Change this with `CAPTURE__PHOTORETENTIONDAYS` (0 = keep forever); see section 10.

---

## 5. Panels stay first-class

Captures build the 3D model **only**. Panel photos, which people browse and set boulders on, are
managed exactly as before under Wall Settings → panels. A capture never changes panels, holds or
boulders.

### "Use as panel photo"

A capture photo can become a panel photo. In the upload list (or later, from **Capture history** →
**Use as panel photos…**), open **Use as panel photo** and pick a target for each photo:

- **New panel at (col,row)**: one photo on a free "+" cell. **Stage as new panel** adds it through
  the normal add-panel flow, with its usual review.
- **Full wall update**: photos for the centre and its direct neighbours. **Start wall update with
  these photos** opens the normal full wall update, pre-filled.

The capture itself isn't changed.

### Why panel photos with markers help

Holds are placed on the 3D model and get millimetre sizes **only from panel photos that show
markers**. A panel photo taken before the markers went up has none, so:

- its holds show as "not measured yet" in the 3D view,
- the first update after the markers go up aligns no better than before, because the old photo has
  no markers to match against.

From the next update on, both the old and the new photo show markers, and alignment and matching
improve a lot (see section 7). So once markers are on the wall, make sure the panel photos in your
next update show them, either by retaking the panels or by using capture photos as panel photos.

---

## 6. The 3D view

### Where to find it

- **Wall page**: a **View in 3D** button under the wall stats.
- **Boulder page**: a **3D** button that opens the view with that boulder highlighted.

Both links only appear once the wall has an active 3D model. They work on share links and on kiosk
tablets. The address is `/walls/{id}/3d`, with `?boulder={id}` for a highlighted boulder.

### Using it

- **Camera presets**: **Front**, **Below**, **Left**, **Right**, **Top**, and **Reset**. Drag to
  orbit, pinch or scroll to zoom. A small top-down map shows where you're looking from.
- **Surface labels** don't overlap each other or the on-screen controls. A label that would overlap
  is nudged up or down and gets a thin line back to its surface. One that can't be freed nearby fades
  instead.
- **Tap a hold** to open a card with its colour, hand or foot, its measured size (e.g. "95 × 80 mm",
  or "size not measured yet") and how many live boulders use it.
- **Boulder highlight**: opened from a boulder, its holds are ringed with the usual legend (Start,
  Hand, Foot, Top) and the boulder's name is shown.
- **Photo-real (beta)** switches from the modelled surfaces to the photo-real capture of the wall.
  The boulder rings stay. Press **Facet model** to switch back. The button is disabled when the wall
  has no photo-real capture. On weaker devices you may see *"This device cannot show the photo-real
  view (its graphics are too limited)."* It needs WebGL 2.

The photo-real view looks convincing from the front, from below and from a distance. Seen from the
side or from above it falls apart, unless the capture included those angles (section 4). Undersides
of volumes look "painted on" unless you shot under them.

### "N holds not measured yet"

This note counts live holds the 3D view can't place: holds with no position on a measured surface.
That happens when the panel photo the hold came from shows no markers (typical for photos taken
before the markers went up), or when the hold's surface isn't part of the active model. The fix is
an update with panel photos that show markers.

---

## 7. Wall updates on marker walls

A wall update works the same on every wall. On a marker wall, three things get better once both the
old and the new panel photos show markers.

### Marker-seeded alignment

Normally the app lines up the new photo with the old one by matching visual features. On a marker
wall, photos that share **at least two** markers (or are both mapped onto the same measured surface)
are aligned from the markers instead. That is exact, and it also works on thinly textured walls
where feature matching fails.

### Wall-space matching

With both photos mapped onto the wall in millimetres, the matcher can propose "this is the same
hold" for holds that land close together on the same surface: within about 60 mm, or the hold's own
size if larger, capped at 150 mm. The appearance fingerprint decides; closeness alone never does.
Tolerances are generous because a hold stands out from the wall, so a single photo's estimate of
where it sits can be several centimetres off. These proposals never override a match the photo
itself provides.

In a rehearsal on a real wall, markers turned near-random panel-to-panel overlap links (56 of 59
wrong) into mostly correct ones.

### Marker sheets aren't holds

On a marker wall, auto-detected "holds" that are really the printed marker sheets are filtered out,
so they don't show up as new holds.

### "Possibly moved" suggestions (all walls)

In the carryover review, **Possibly moved (N)** lists holds that vanished from their old spot but
look like a new hold elsewhere. Each row shows the old and the new hold side by side with a match
score. **lower confidence** means the match is based on colour and shape only, and look-alike holds
can score that high. Tap the pair to see both holds on the photos.

| Answer | What it means | Effect on boulders |
|---|---|---|
| **Moved here** | The hold was physically moved to the new spot. | Same as marking the hold "changed": its boulders are **flagged for revision**. |
| **Same hold** | The hold didn't move; only the photo shifted. | Carried onto the new hold like a normal match. **Nothing is flagged.** |
| **Dismiss** | Not the same hold. | No change from this suggestion; the hold goes through the normal review. |

Nothing changes until you answer. An answered row shows its state and an **Undo**. If the new hold
is already taken by another old hold, the buttons are disabled: *"That new hold is already carried
from another old hold."*

### "Couldn't line up this photo" (all walls)

If the new photo can't be lined up with the previous one, the review now says so instead of
silently carrying holds to the wrong place:

> *Couldn't line up this photo with the previous one automatically — its holds are shown where they
> were before. Check them, or retake the photo with more overlap. None of them counts as reviewed
> until you confirm it.*

For other re-photographed panels there's a matching banner naming the panel. Their holds are kept at
their old position and marked **needs review** on the wall after the update. When adding a new panel
there's a similar banner: link the overlapping holds by hand, or retake the photo.

What to do:

1. Check whether the new photo really shows the same area. A photo taken from a very different
   spot, or of a mostly repainted or reset area, can't be lined up automatically.
2. If it's close, confirm or move the carried holds by hand.
3. Otherwise retake the photo with more overlap with the old one. On a marker wall, make sure at
   least two markers the old photo also shows are fully in the frame.

A variant says *"Automatic matching isn't available right now"*: the matching service itself was
unavailable, and the same steps apply.

---

## 8. Hold outlines

### Automatic outlines

When a panel photo is uploaded, each auto-detected hold's real outline is traced. If the tracing
isn't confident, the hold stays a circle. This runs on every wall and can be switched off
server-wide (`HOLDDETECTION__OUTLINES__ENABLED=false`).

### Pocket holds

Pockets and donut holds keep their inner hole and draw it as a hole, so the wall shows through. **A
tap anywhere, including inside the hole, still selects the hold.** It's still one hold.

If you edit a pocket hold's outer shape by hand, its hole is dropped. Re-detecting the hold brings
it back.

### One-click outline upgrade

Existing holds created before outlines existed are circles. To trace them:

1. Open **Wall Settings → Wall Shape → Hold outlines**.
2. Optionally tick **Also trace holds that were placed by hand**.
3. Press **Detect real outlines for existing holds**. This is a preview. It looks at every wall
   photo, can take a minute, and changes nothing yet. The result reads like *"423 of 547 circle
   holds get an outline; 4 have a pocket hole; 124 stay circles."*
4. **Apply** to save it, or **Cancel**.

Positions and boulders aren't changed; only shapes (and fingerprints) are added.

**Revert:** under **Last upgrade**, **Revert this upgrade** (confirm with **Yes, revert**) restores
the circles. Only holds whose outline is still exactly what the upgrade wrote are reverted. A hold
you've edited since then keeps your edit.

If the section says *"Outline detection is switched off on this server."*, the outlines kill switch
is off.

---

## 9. Running the services

Two helper services do the heavy lifting. Both speak the same small job protocol
(`docker/compute-jobs-protocol.md`): the app submits a job over HTTP with a bearer API key, then
**polls** for progress and fetches the results. The app knows each service only as **a URL plus an
API key**.

| Service | Needed for | Hardware | Where it runs |
|---|---|---|---|
| `wall-geometry` | Captures: solve + textures | CPU, a few hundred MB | Next to the app, same compose network |
| `splat-worker` | Photo-real view only (optional) | **GPU** | Usually another machine |

### wall-geometry

- Image: `ghcr.io/zannagh/blocwerk-geometry:latest`, built by `.github/workflows/wall-geometry.yml`.
  The CI workflow hasn't been verified on GitHub yet (to confirm).
- Internal only: no published port. The app reaches it at `http://wall-geometry:8000`.
- **Opt-in.** The service sits behind the compose profile `compute`, and the app's
  `GEOMETRYSERVICE__URL` comes from `GEOMETRYSERVICE_URL` in `docker/.env` (empty by default). With
  neither set, a plain `docker compose up` starts the app without it and the capture section is hidden.
- **`COMPUTE_API_KEY` is required to run it.** The service refuses to listen on anything beyond
  localhost without a key (exit code 2), so it never runs unauthenticated. Generate one with
  `openssl rand -hex 32` and put it in `docker/.env` (never commit that file). The app uses the same
  value as `GEOMETRYSERVICE__APIKEY`.
- Measured on the reference wall (14 photos): solve ≈ 9 s, textures ≈ 4 s.

Setup with the repo's compose file:

1. Copy `docker/.env.example` to `docker/.env` and set `COMPOSE_PROFILES=compute`,
   `GEOMETRYSERVICE_URL=http://wall-geometry:8000` and `COMPUTE_API_KEY`.
2. `docker compose pull wall-geometry && docker compose up -d wall-geometry blocwerk`.
3. `docker compose ps` should show `wall-geometry` as healthy.

**Production with a standalone compose copy (manual step).** If your server runs its own copy of the
compose file rather than the repo's (the IONOS box does), CI publishing the image doesn't deploy it.
On the box:

1. Add the `wall-geometry` service block from `docker/docker-compose.yml` to the standalone file,
   and the `GEOMETRYSERVICE__URL` / `GEOMETRYSERVICE__APIKEY` lines to the `blocwerk` service's
   `environment`.
2. Set `COMPOSE_PROFILES=compute`, `GEOMETRYSERVICE_URL=http://wall-geometry:8000` and
   `COMPUTE_API_KEY` (and optionally `COMPUTE_CALLBACK_SECRET`) in that directory's `.env`.
3. The GHCR package is private by default. Make sure the box's GHCR login can pull it, or make the
   package public.
4. `docker compose pull wall-geometry && docker compose up -d wall-geometry blocwerk`, then check
   it's healthy.
5. The autodeploy cron only redeploys `blocwerk`. To update `wall-geometry` later, pull and `up -d`
   it by hand.

(`docker/wall-geometry/README.md` still mentions *three* `GEOMETRYSERVICE__*` lines. The callback
secret line was removed from the app since the app polls; two are current.)

### splat-worker (optional)

The splat worker turns capture photos into the photo-real view: COLMAP works out the camera
positions, then Brush trains a Gaussian splat on the GPU. It needs a GPU, so it usually runs on a
different machine from the app. **Setting its URL is the opt-in**: once `SPLATSERVICE__URL` is set,
every capture runs a photo-real stage after its textures.

**Memory budget.** During testing, COLMAP's matching grew to a ~25 GB footprint on a 16 GB Mac and
pushed the machine deep into swap. The worker now works out a memory budget at the start of every
job from what the machine can actually give. It reads total and available memory and swap (macOS
`sysctl`/`vm_stat`, Linux `/proc/meminfo`), plus the container's cgroup limit when it runs in
Docker. **Budget = the smaller of what's available and 60 % of total memory**, at least 3 GB, and
never above `SPLAT_MAX_MEMORY_MB` when that is set. A watchdog kills any COLMAP step or Brush run that
goes above the budget. The job's progress label shows it, e.g. *"memory budget 9.6 GB (12.1 GB
available of 16.0 GB); 4 threads"*.

COLMAP is then fitted to the budget: fewer extraction threads when memory is short, and for matching
the best of three tiers that fits (guided matching with as many threads as fit, guided with one
thread, then a cheap unguided match plus an extra triangulation pass). If the watchdog kills a step
anyway, the step runs again one tier down instead of failing the job. The job only fails when the
cheapest tier is killed too, e.g. *"sfm-matching: COLMAP exceeded the 3.9 GB memory limit …"*. The
worker also stops a tool once the system's swap has grown by `SPLAT_MAX_SWAP_GROWTH_MB` while it
ran, and steps down the same way.

| Variable | Default | Effect |
|---|---|---|
| `SPLAT_MAX_MEMORY_MB` | 0 (unset) | Hard upper bound on the budget. Unset = the budget comes from the machine alone. |
| `SPLAT_MAX_SWAP_GROWTH_MB` | 2048 | Stop a tool once system swap grew this much while it ran. `0` = no swap check. |
| `COLMAP_MAX_IMAGE_SIZE` | 2400 | Longest photo edge COLMAP's feature extraction sees. |
| `COLMAP_MAX_FEATURES` | 8192 | Upper bound on features per photo. |
| `COLMAP_MAX_MATCHES` | 8192 | Matches per photo pair. |
| `COLMAP_THREADS` | 4 | Upper bound on threads for extraction, matching and mapping; the budget may use fewer. Raise it on a big machine for speed. |
| `FRAME_NEIGHBOURS` / `FRAME_PHOTO_STRIDE` | 6 / 4 | Only with a walk-along video: each video frame is matched with its next 6 frames, and every 4th frame with every photo, instead of matching every image with every other one. |

The repo's compose file and `docker/.env.example` pass the memory and COLMAP variables through;
`docker/splat-worker/README.md` has the details and the measurements behind the tiers. If a job
still fails on memory, use fewer or smaller photos, or run the worker on a machine with more memory.

**Video uploads through a reverse proxy.** The walk-along video goes to the **app**, not the worker,
at `POST /api/captures/{id}/video`, as one streamed request of up to `CAPTURE__MAXVIDEOMB` (1 GB by
default). If a reverse proxy sits in front of the app, it must allow a request body of about **1 GB**
on that route, or uploads fail partway. Other routes can keep their normal limit.

**On a Mac (native).** Docker on macOS has no GPU access, so on a Mac the worker runs natively and
trains through Metal:

```bash
brew install colmap
COMPUTE_API_KEY=$(openssl rand -hex 32) docker/splat-worker/run-native.sh   # listens on 127.0.0.1:8100
```

Keep the Mac awake while it works (`caffeinate -i` in front of the command). Measured on an M4 with
16 GB, 14 photos, 5000 steps: about 17 minutes. The default of 15000 steps takes roughly 50 minutes.

**On Linux with an NVIDIA GPU (Docker).** Install the NVIDIA Container Toolkit, then either use the
compose file's `gpu` profile (`docker compose --profile gpu up -d splat-worker`, app URL
`http://splat-worker:8100`) or run the image `ghcr.io/zannagh/blocwerk-splat-worker` with
`--gpus all`. Check with `vulkaninfo --summary` inside the container that your GPU is listed, not
only `llvmpipe`. The image is linux/amd64 only. It has **not yet been run on a real GPU host (to
confirm)**. Without a GPU it falls back to CPU rendering, which is far too slow to be useful.

**Reaching a worker on another machine.**

- Always set `COMPUTE_API_KEY` on the worker (it refuses to listen beyond localhost without one) and
  use the **same value** as the app's `SPLAT_API_KEY`.
- Keep the worker bound to `127.0.0.1` and put **HTTPS** in front of it. The app refuses plain
  `http://` to anything but localhost or a single-word internal host name. Options:
  - **Tailscale**: `tailscale serve --https=443 localhost:8100` (use Funnel if the app server isn't
    on your tailnet);
  - **Cloudflare Tunnel**: `cloudflared tunnel` with a named tunnel. The free plan caps uploads at
    100 MB, so use fewer or smaller photos, or Tailscale;
  - a reverse proxy such as Caddy, with a raised body size limit.
- The app must be able to reach the worker. The worker never needs to reach the app.

In `docker/.env` on the app server:

```
SPLATSERVICE_URL=https://your-worker.example.ts.net
SPLAT_API_KEY=<same as the worker's COMPUTE_API_KEY>
SPLAT_MAX_STEPS=            # optional, e.g. 5000 for faster, softer results
```

Without a splat worker, leave all three empty. `SPLAT_API_KEY` is only sent when `SPLATSERVICE_URL`
is set.

### Polling vs callbacks

The app **polls** each job (starting at 1 s, backing off to 10 s). The services can also send
signed callbacks (`COMPUTE_CALLBACK_SECRET`), but the app doesn't use them, so leave the secret empty
unless you need callbacks for another client.

### When a service is down

| Situation | What happens |
|---|---|
| `GEOMETRYSERVICE__URL` not set | The capture section says the service isn't configured. Importing `wall-geometry.json` still works. Everything else is unaffected. |
| `wall-geometry` unreachable during a capture | The app tolerates a run of errors while polling, then fails that attempt. A capture is tried up to 3 times, then marked **Failed** with the reason. |
| Service restarted mid-job | Services keep jobs in memory only, so the job is lost. The app resubmits it (it counts as an attempt). |
| Splat worker down or failing | The capture ends as **Model ready (no photo-real view)**. The model and textures stay live. |
| Any service down during a wall update | No effect. Updates use the active model stored in the app and never call the services. |
| App restarted mid-capture | The capture resumes when the app comes back. |

---

## 10. Settings reference

Every key can be set in `appsettings.json` under `Blocwerk:` (left column) or as an environment
variable (right column). **When both are set, the appsettings value wins.** Remember this when an
environment variable seems to be ignored.

### Kill switches

| appsettings (`Blocwerk:…`) | Environment | Default | Effect |
|---|---|---|---|
| `HoldDetection:Outlines:Enabled` | `HOLDDETECTION__OUTLINES__ENABLED` | `true` | Outline + fingerprint tracing at photo upload, on every wall. Also disables the outline upgrade. Only an explicit `false` turns it off. |
| `HoldDetection:Markers:Enabled` | `HOLDDETECTION__MARKERS__ENABLED` | `true` | Marker detection, marker observations and millimetre hold data at photo upload. Even when on, it only runs for walls that have declared markers. |

There's no per-wall setting for outlines; they're server-wide.

### Compute services (app side)

| appsettings (`Blocwerk:…`) | Environment | Default | Effect |
|---|---|---|---|
| `GeometryService:Url` | `GEOMETRYSERVICE__URL` | empty | The `wall-geometry` base URL. Empty = in-app capture off. Must be `https://`, or `http://` to localhost or a single-word host such as `wall-geometry`. |
| `GeometryService:ApiKey` | `GEOMETRYSERVICE__APIKEY` | empty | Bearer key; must equal the service's `COMPUTE_API_KEY`. |
| `GeometryService:RequestTimeoutSeconds` | `GEOMETRYSERVICE__REQUESTTIMEOUTSECONDS` | 300 | Timeout for a single HTTP request (photo uploads take a while). |
| `GeometryService:JobTimeoutMinutes` | `GEOMETRYSERVICE__JOBTIMEOUTMINUTES` | 30 | How long the app waits for one job. |
| `SplatService:Url` | `SPLATSERVICE__URL` | empty | The splat worker's base URL. **Setting it turns the photo-real view on.** Same URL rules. |
| `SplatService:ApiKey` | `SPLATSERVICE__APIKEY` | empty | Must equal the worker's `COMPUTE_API_KEY`. |
| `SplatService:RequestTimeoutSeconds` | `SPLATSERVICE__REQUESTTIMEOUTSECONDS` | 300 | As above. |
| `SplatService:JobTimeoutMinutes` | `SPLATSERVICE__JOBTIMEOUTMINUTES` | 240 | Training is slow; 4 h by default. |
| `SplatService:MaxSteps` | `SPLATSERVICE__MAXSTEPS` | worker default (15000) | Training steps per job. Lower is faster and softer. |

The compose file maps `docker/.env` values onto these: `GEOMETRYSERVICE_URL` → `GEOMETRYSERVICE__URL`,
`COMPUTE_API_KEY` → `GEOMETRYSERVICE__APIKEY`,
`SPLATSERVICE_URL` → `SPLATSERVICE__URL`, `SPLAT_API_KEY` → `SPLATSERVICE__APIKEY`, `SPLAT_MAX_STEPS`
→ `SPLATSERVICE__MAXSTEPS`.

### Capture

| appsettings (`Blocwerk:…`) | Environment | Default | Effect |
|---|---|---|---|
| `Capture:PhotoRetentionDays` | `CAPTURE__PHOTORETENTIONDAYS` | 30 | Days after a capture ends before its photos are deleted. `0` = keep forever. The active model's capture is always kept. A capture's video frames follow its photos. |
| `Capture:MaxVideoMb` | `CAPTURE__MAXVIDEOMB` | 1024 | Largest walk-along video (streamed to disk, 1–16384). A reverse proxy in front of the app must allow bodies this big on `/api/captures/*/video` (section 9). |
| `Capture:MaxVideoFrames` | `CAPTURE__MAXVIDEOFRAMES` | 120 | Most frames taken from the video (3–400). |
| `Capture:VideoFramesPerSecond` | `CAPTURE__VIDEOFRAMESPERSECOND` | 2.5 | Target frame rate (ffmpeg decodes 3 candidates per kept frame; the sharpest wins); lowered for long videos to stay under the cap. |

Fixed limits (not configurable): 40 photos per capture, 20 MB per photo, a 10-minute video, 3 attempts per capture,
drafts removed after 1 day, sweep every 6 hours. Capture photos are stored under the wall-image
storage path (`WALLIMAGE__STORAGEPATH`), in `captures/`.

### Service side (`docker/.env` and container environment)

| Variable | Service | Default | Effect |
|---|---|---|---|
| `COMPUTE_API_KEY` | both | unset | Bearer key. **Required** by both services to listen beyond localhost (they exit with code 2 without it). Compose doesn't demand it: the services are opt-in profiles (`compute`, `gpu`). |
| `COMPUTE_CALLBACK_SECRET` | both | unset | Signs optional callbacks. The app doesn't use them. |
| `ALLOW_OPEN_BIND` | both | unset | `1` = start without a key. Only on a private network. |
| `MAX_IMAGE_MEGAPIXELS` | both | 100 | Photos above this are refused before decoding. |
| `MAX_PHOTOS` | both | 60 / 400 | Photos per job (geometry / splat). |
| `SOLVE_TIMEOUT_S` / `TEXTURES_TIMEOUT_S` | geometry | 600 / 900 | Per-job limit. |
| `TEXTURES_MAX_MEGAPIXELS` | geometry | 200 | Total output size of one textures job. |
| `SPLAT_TIMEOUT_S` | splat | 14400 | Per-job limit (4 h). |
| `MAX_QUEUED_JOBS` | both | 16 / 4 | Queue length. The splat worker runs one job at a time. |
| `RESULT_TTL_S` | both | 3600 | Finished jobs and their files are deleted after this. |

The service READMEs (`docker/wall-geometry/README.md`, `docker/splat-worker/README.md`) list the
rest.

---

## 11. Troubleshooting

### A marker isn't detected

Look at the **Markers** column of the upload table.

| Cause | Fix |
|---|---|
| **Partly out of frame.** Even a thin strip past the edge drops it silently. | Retake with the marker fully inside and a margin around it. |
| **Grazing angle.** The marker is on a near-horizontal face or seen almost edge-on. | Take a photo aimed straight at that face. If it can't be photographed face-on, move the marker. |
| **Glare** on glossy paper. | Change the angle or light, or reprint on matte paper. Boosting contrast doesn't help; in testing, contrast enhancement (CLAHE) *reduced* detections from 82 to 57. |
| **Too small in the photo.** | Get closer, or use bigger markers. 125 mm is the tested size for corners. |
| **Wrong dictionary, or an id the wall doesn't expect.** | Reprint from `DICT_4X4_50`. With a marker plan, only the plan's ids are read; without one, only ids 0–35. Anything else is ignored on purpose. |

Stray detections in busy textures (ids above 35, the same id twice in one photo) are rejected
automatically.

### The model looks tilted, or the measured angles are off

- **A surface marked "vertical" isn't actually plumb.** Everything is measured against your vertical
  references. Check them with a spirit level, and untick any you're unsure about.
- **"Markers at equal height" pairs conflict with the vertical references.** On the reference wall,
  declaring one pair as level together with a vertical kickboard moved the main wall's angle by
  about 1° and tilted the kickboard 1°: the two statements disagreed by ~2°. Only enter pairs you
  have measured, and if in doubt, leave them out.
- **Fewer than two vertical references** that face different ways. Gravity is then unknown and
  angles show as blank. Millimetres are still correct.
- **A folded surface declared as one piece.** If a segment has a bend, give each side its own
  markers (section 2) so the solver can split it.

### "No focal length in the photo's EXIF"

Upload the original from the camera roll, not a screenshot, a messenger copy or an edited export.

### "Couldn't line up this photo"

See section 7. In short: the new photo doesn't overlap the old one well enough. Check the carried
holds, or retake the photo with more overlap (and, on a marker wall, two shared markers fully in
frame).

### "N holds not measured yet" in the 3D view

The panel photos those holds came from show no markers. Run a wall update with photos that do
(section 5).

### The photo-real view is misaligned with the model

- The photo-real view is aligned to the model using the **capture photos that show markers**. If
  few photos show markers, alignment is weak. Include markers in more frames.
- Don't mix lenses. In testing, a bug that merged two lenses into one camera shifted a splat by
  1.6 m (fixed), but mixed lenses still make alignment harder.
- The capture's status says **Model ready (no photo-real view)**: the error underneath says which
  stage failed. For example, *"sfm-mapping: only 3/14 images registered … shoot more overlap"*
  means the photos don't overlap enough.

### The photo-real view looks smeared from the side or from above

That's expected: it only knows what the photos saw. Follow the capture recipe in section 4 (three
heights, arcs at the ends, under volumes).

### The capture failed

Open **Capture history**; the error is shown in plain words. Common causes: no photo shows markers,
*"None of the declared segments appears in the photos"* (you named a row whose markers aren't in any
photo), the geometry service is unreachable (section 9), or a photo couldn't be read (*"this photo
couldn't be read"*: re-export it as JPEG).

### The splat worker slows the whole machine down

See "Memory budget" in section 9. The worker sizes itself to the memory that's free when a job
starts, so other work started later can still squeeze it. Set `SPLAT_MAX_MEMORY_MB` to cap the
budget below what the machine offers, or lower `COLMAP_THREADS` (default 4), or run the worker on a
machine with more memory.

### Photos with GPS locations

New photos, capture and panel photos alike, are stripped of their location data automatically when
they're uploaded. Photos stored before that can be cleaned under **Photo location data**; see
section 12.

---

## 12. Privacy

Phone photos usually contain the GPS location where they were taken, and often more (camera serial
data, depth maps, embedded "live photo" videos).

**Capture photos.** When a capture photo is uploaded, the app reads the two facts it needs (image
size and focal length), then **removes all metadata before storing it**: EXIF including GPS and
orientation, XMP, ICC profiles, comments, and **everything appended after the image itself**
(iPhone gain and depth maps with their own GPS data, Motion Photo videos, vendor trailers). The image
pixels aren't re-encoded, so marker positions stay exact.

**What's sent to the services:**

| Service | Receives |
|---|---|
| `wall-geometry`, solve | Marker ids, corner pixel coordinates, image sizes, focal lengths, your declarations. **No photos.** |
| `wall-geometry`, textures | The solved model and the capture photos, already stripped. The service strips metadata again on arrival and never writes the uploaded bytes to disk. |
| `splat-worker` | The capture photos (stripped) and the solved model. The worker decodes, re-encodes and strips each photo while it's still uploading, so the original bytes never reach its disk (COLMAP would otherwise read GPS as a position hint). |

Nothing is forwarded anywhere else. The services keep job results only until `RESULT_TTL_S`
(1 hour by default) and lose everything on restart. When the splat worker runs on someone else's
machine, that person's machine sees your wall photos, so only point the app at workers you trust,
and always over HTTPS with an API key.

**Retention.** Capture photos are deleted 30 days after the capture ends, except for the capture
behind the active model (section 4, `CAPTURE__PHOTORETENTIONDAYS`).

**Panel and wall photos.** Every other photo upload is now cleaned before it's stored too: a new
panel (`WallPanelService.StagePanelAsync`), the photos of a full wall update
(`WallBigUpdateService.StageAsync`), the wall photo (`WallService.UploadPhotoAsync`) and gallery
images uploaded through the API, e.g. by a Pi camera (`WallImagesController.Upload`). All of them go through
`StoredPhotoSanitizer`, which removes GPS and all other metadata like the capture path, with two
differences: the EXIF **orientation** is kept (holds are stored in the raw pixel grid, so dropping it
would turn the photo under its holds) and so is the **colour profile**. WebP images (gallery
uploads) are cleaned the same way: their EXIF and XMP go, the colour profile and a non-default
orientation stay. Other formats are stored unchanged. Using a capture photo as a panel photo goes through the same new-panel path,
so it's stored stripped as well.

**Photos stored before this.** Wall Settings → Wall Shape → **Photo location data** →
**Check stored photos** counts the wall's stored photos that still carry a location or other
metadata (wall and panel photos of every generation, gallery snapshots, the copies in the change
history). **Remove location data** then cleans them. Pixels and orientation aren't changed, so holds
stay where they are.

**Beta videos.** Phones write the recording location into the video file. Every uploaded beta video
now goes through the background normalizer, which strips the container and per-stream metadata
(including the location tags), chapters, and data and subtitle tracks, whether it re-encodes the clip
or only remuxes it. The rotation is kept, so portrait clips stay upright. Videos stored before this
still carry their metadata. To clean them, open **Administration** (`/administration`) → **Beta
videos** → **Re-encode all** and confirm with **Yes, re-encode all**. "Re-encode not-ready" only
re-queues clips that aren't playable yet, so it skips most old clips. The clips are processed one at
a time in the background.
