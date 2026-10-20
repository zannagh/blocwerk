# Automating a wall update and wall preparation

Rollout scripts can run the panel update ("Update panels + 3D") and prepare a wall for marker captures over the REST API with an API key. **Neither flow needs a browser session any more**, so the API-key browser login ([api-key-login.md](api-key-login.md)) can stay switched off on production for them.

Every route calls the same service the page calls, so the rules are the page's rules: the key's owner must be an admin of the wall, kiosk tablets are refused, and the panel update promotes nothing before Apply.

## Keys

| Flow | Personal key with write access | Wall key with write access (for that wall) | Read-only keys, kiosk, installation keys |
| --- | --- | --- | --- |
| Panel update (`/api/walls/{wallId}/refresh`, `/api/refreshes/{id}/files`) | yes | **no** (403) | no (403) |
| Colours for holds without one (`/api/walls/{wallId}/holds/suggest-colors`) | yes | **no** (403) | no (403) |
| Stacks for existing holds (`/api/walls/{wallId}/holds/stacks`, `…/stacks/preview`) | yes | **no** (403) | no (403) |
| Wall preparation (`/api/walls/{wallId}/markers`, `…/marker-plan`, `…/marker-plan/revisions`) | yes | yes | no (403) |
| Change journal export / replay / revert (`/api/v1/admin/journal/…`) | yes, **owner must be an installation admin** | **no** (403) | no (403) |

* "Write access" is the *Allow this key to change walls* box when creating the key under *Settings → API keys*.
* The owner has to be an admin of the wall (owner or member with the Admin role). Being an administrator of the installation is not enough on its own, exactly as in the browser: the request answers 403.
* Wall keys are kept out of the panel update on purpose. They live on devices bolted to the wall and must be assumed to leak, the update's file drop never admitted them, and replacing panel photos and promoting a new hold generation is a person's decision. Marker preparation follows the existing wall-admin API rules (captures, marker-plan revisions), which admit a wall key with write access for its own wall.

## Audit

Every write through these routes is recorded in the change journal, scoped to the wall, as a batch labelled `api:<action> key:<api key id>` with the key's owner as the actor:

* The batch row is written **before** the write runs, with status *Pending*, so no write happens without one. A row left *Pending* means the outcome could not be recorded (the server was stopped mid-write, or the journal was unavailable afterwards; the latter is also logged as an error, and the write is still answered as the success it was).
* A successful write (2xx) turns it into *Recorded*. Rows the write changes that the journal tracks (the wall's marker switch and size) are recorded in that batch with their before and after values.
* A refused or failed write removes its row again. If it had already journalled rows, the batch is kept as *Failed* so those rows stay explained.
* All the files of one upload go into one batch per run (and key): `api:refresh.upload run:<id> key:<id>`.
* Every write, refused or not, is also in the server log with the key id.

Actions: `refresh.begin`, `refresh.upload run:<id>`, `refresh.sort`, `refresh.recheck`, `refresh.start`, `refresh.check`, `refresh.checks`, `refresh.review`, `refresh.review.accept`, `refresh.apply`, `refresh.discard`, `lookover.answer`, `lookover.undo`, `lookover.finish`, `holds.suggest-colors`, `markers.set`, `marker-plan.save`, `marker-plan.effective rev:<n>`. The answers' own changes (a removed or merged hold in the look-over) are journalled in the look-over's own batch, as from the page; the `api:` batch records who asked.

## Panel update, end to end

```sh
BASE=https://blocwerk.example
WALL=<wall id>
AUTH="Authorization: Bearer $BLOCWERK_PERSONAL_API_KEY"   # personal key with write access

# 1. Open a run (or get the open one).
RUN=$(curl -fsS -X POST -H "$AUTH" "$BASE/api/walls/$WALL/refresh" | jq -r .id)

# 2. Drop the photos and the walk-along video, one file per request, as the raw body.
#    A refused file answers 200 with its reason in "problem".
for f in photos/*.HEIC walk.mov; do
  curl -fsS -X POST -H "$AUTH" -H 'Content-Type: application/octet-stream' --data-binary @"$f" \
    "$BASE/api/refreshes/$RUN/files?name=$(basename "$f")" | jq -c .
done

# 3. Done uploading: sort the photos to the panels, wait for status 2 (ready to start).
curl -fsS -X POST -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN/sort"
until [ "$(curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN" | jq .status)" = 2 ]; do sleep 5; done

# 4. Accept the quick defaults: start with the proposed photos. Wait for the confirm screen.
curl -fsS -X POST -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN/start"
until curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN/summary" | jq -e .canApply >/dev/null; do sleep 10; done

# 5. Summary only (a dry run): what Apply would do, and the version that stands for it.
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN/summary" | tee summary.json | jq .summary
VERSION=$(jq -r .decisionsVersion summary.json)

# 6. Apply exactly that summary. 409 when it changed since: read the summary again.
curl -fsS -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"decisionsVersion\":\"$VERSION\"}" "$BASE/api/walls/$WALL/refresh/$RUN/apply"

# 7. Wait until it is done (6). Back at 4 means the decisions changed before the apply ran:
#    nothing was promoted, and the summary (with a new version and "error" saying why) must be checked again.
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN" | jq '{status, error}'

# Or throw the run away instead (not while a step is running):
# curl -fsS -X DELETE -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN"
```

Statuses: 0 uploading, 1 sorting, 2 ready to start, 3 running, 4 ready to apply, 5 applying, 6 done, 7 failed, 8 discarded.

`start` answers 409 before anything runs (no capture is started, the run stays at status 2 with its photos) while another update is open on the wall, typically a forgotten *Update step by step*: the error names who started it and when. Finish or discard that one first. A step-by-step update nobody has touched for 7 days is discarded by the server (its staged photos and choices; nothing live changes).

`start` takes an optional body `{"choices":[{"col":0,"row":0,"photoId":"<id>"}]}` to override the proposal per panel (`photoId: null` keeps that panel's current photo). Without it the run uses the sorter's picks and the quick review's decisions (every old hold carried, only sure overlap links), the same as pressing *Start* and *Apply* on the page without changing anything. Anything finer (moved holds, removals) remains the full review in the browser.

The quick review never takes a hold off a boulder. What it does on its own, and what the summary says about it before anything is applied:

* An old hold is carried onto the detection at its spot only when that detection still looks like it. When another hold sits there now, the old hold is kept where it was, the detection becomes a new hold, and a card asks (`replacedHolds` counts them). A pair that is merely unclear is carried and also gets a card; a pair that cannot be measured is carried without one. Cards are answered on the page or over the API (*Answering the cards*, below); unanswered ones mark the hold for review and stay with the wall for the look-over (below).
* `notFoundHolds` is the to-do list for after the update: every old hold used by a boulder that was not found again and confidently no longer shows at its spot (`outcome` 4, `"not found again on the new photo"`) or was replaced (`outcome` 5, `"another hold is here now"`), with `oldHoldId`, `holdName` (name, else colour), `text` and `boulders` (names). `notFoundOnBoulders` is its length and `bouldersMarkedNotFound` the number of boulders Apply marks for review because of it. Holds that were not detected but may still be there (the spot is unclear, cannot be measured, or another panel still shows the hold) are kept silently and are not listed.
* A hold can sit on another hold (a hold on a volume): a hold that lies mostly (70 % of its area) inside the outline of a much larger one (at least 4 times its area; a plain circle is never a base) is stacked on it when it is detected or added, and the two are two holds everywhere. An old hold is never carried onto a detection of a clearly different size (the matcher takes the nearest detection of its own size; `otherSize` in the `[Matcher]` log line counts them), and a pair of clearly different size that does not look alike is taken apart without `"another hold is here now"`: the old hold is then judged by its own spot. A carried hold keeps what it sat on (the successor of its old base). Holds that were on the wall before are never stacked by themselves: `POST /api/walls/$WALL/holds/stacks/preview` lists what the rule would stack (`stacks`: `holdId`, `baseHoldId`, `panelId`, `insidePercent`, `areaRatio`, both colours; writes nothing), and `POST /api/walls/$WALL/holds/stacks` stacks them (all, or only the `holdIds` of the body) as one change-journal batch (`hold-stacks`). A hold that already sits on a hold is left as it is, so a second run changes nothing.
* `movedHolds` lists the carried holds that physically moved, in the same shape plus `newHoldId`, `distanceMm` and `source`. The summary's moves are worked out from exactly what Apply then promotes (the matcher's warped outlines included), so an Apply of a summary nobody changed is never refused as "moved holds changed"; a re-outlined hold (the outline grew or a circle became a traced outline, the hold did not turn, and the size change explains the shift) counts as stayed.
* Unpaired detections are triaged before they become new holds; each one left out keeps its `discardReason` and is listed in the look-over (finding kind 8, "Add as a hold"). Known misses, from The Attic's update of 2026-10-20 (`.me/mid-oct-update/exp`), and why the rules stay:
  * `UnchangedSinceOldPhoto` (two small new holds on bare wall, 16–18 px): a detection the detector or the outliner was not sure of (under 0.9, or no traced outline) is compared with the old photo on a fixed patch larger than the hold, and a small hold on plain wall changes too little of it (scores 0.86–0.88, discarded from 0.6). The same detections with a confident outline (the run of 2026-10-19) were kept. Lowering the bar keeps the dozens of screw heads and bolt holes the rule drops.
  * `InsideCarriedHold` (a red hold bolted in front of the wood hold): it lies mostly inside the wood hold's warped outline, which was not found again and so is kept in place. It is neither a hold on a volume (the wood hold is not 4 times its area) nor a swap (it covers less than 40 % of the wood hold), the two exceptions that keep a detection there. Telling a hold in front of a hold from a part of it needs more than outlines.
  * `SeenOnNeighbourPanel` (on the right panel): a detection that the overlap maps onto a spot the centre photo shows and settled is the centre's. A confident detection (detector sure, outline traced) is no longer dropped where the centre only threw its own detection away: the hold would otherwise be on neither photo (an existing beige hold at the right panel's edge was lost so after the 3D re-check). Where the centre carries a hold (the lilac ball maps onto one with the 3D-seeded overlap) it is still the centre's; the look-over lists it.
* The thresholds are the `HoldMoves` settings `SameLookMinScore` (0.5: at or above, the pair is the same hold), `ReplacedBelowScore` (0.28: below, another hold is there; in between, an unsure-match card) and `GoneMaxScore` (0.5: a hold not found again marks its boulders only at or below). What was measured is logged per panel as `[Looks] panel=…` (a score histogram), and per pair and spot at Debug.

### The wall was fully reset: start over

When every hold was taken off and the wall was set again, matching the old holds makes no sense. Start the run with `fullRevision`:

```sh
curl -fsS -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"fullRevision": true}' "$BASE/api/walls/$WALL/refresh/$RUN/start"
```

* Nothing is matched, looked at or carried over. Every detection that passes the usual checks (not on a marker) becomes a new hold with a suggested colour. A hold the centre photo and a neighbouring photo both show is never dropped: the two are linked, and where the overlap matcher was not sure of the pair the look-over asks "Same hold on both photos?" (finding kind 9: "Yes" keeps the link, "No, two holds" takes it apart and both holds stay).
* The old holds are not copied forward. They stay as the history of their generation.
* Every boulder of the wall that is not archived yet is archived (live and historic ones alike). Archived boulders keep their sends and still open on the old photo with their old holds.
* It needs a new photo for **every** panel: a panel left out would keep its old holds on a wall that starts over. Otherwise `start` answers 409 and the run stays at status 2.
* `keepModel` works as usual. The holds are placed on the 3D model after the apply either way.
* The summary then carries `fullRevision`: `panels` (`panel`, `detected` per new photo), `holdsCreated`, `oldHoldsRetired` and `bouldersArchived` (names). `refound`, `keptInPlace`, `removed`, `movedHolds` and `notFoundHolds` are empty. Apply takes the `decisionsVersion` as always.
* Nothing is left for the look-over about old holds; the wall is flagged when an unsure link (or a new-hold finding) remains.
* It is one change-journal batch like any other update, but there is no undo in the app. Until Apply, `DELETE …/refresh/{id}` discards it and the wall stays as it is.

### Answering the cards

The confirm screen's cards (the holds whose evidence needs a person's look) can be listed and answered while the run is at status 4. Each answer is the page's own: written to the run's update session as the full review's decision, after which the summary is made again in the background, so read `…/summary` again and apply its new `decisionsVersion`.

```sh
# The cards and how each is answered now:
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN/checks" | jq -c '.[] | {id, kind, status, holdName, boulders, canRemove}'

# Answer one (keep | remove | undo | notSame, or 0 | 1 | 2 | 3):
curl -fsS -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"answer":"keep"}' "$BASE/api/walls/$WALL/refresh/$RUN/checks/$CARD/answer"

# Answer several in one go, in order:
curl -fsS -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"answers":[{"checkId":"<id>","answer":"keep"},{"checkId":"<id>","answer":"remove"}]}' \
  "$BASE/api/walls/$WALL/refresh/$RUN/checks/answers"
```

| Method | Route | Body | Answers |
|---|---|---|---|
| GET | `…/refresh/{refreshId}/checks` | — | 200 `[{id, kind, status, hasOld, hasModel, boulders, holdName, confidence, canRemove}]` (empty unless status 4) |
| POST | `…/refresh/{refreshId}/checks/{checkId}/answer` | `{"answer": "keep"\|"remove"\|"undo"\|"notSame"}` | 202 `{"answered": 1}` |
| POST | `…/refresh/{refreshId}/checks/answers` | `{"answers": [{"checkId", "answer"}]}` | 202 `{"answered": n}` |

* `kind`: 0 possibly removed, 1 unsure match, 2 new hold with conflicting evidence, 3 matched to a hand-placed hold, 4 hand-placed hold with several detections, 5 another hold is here now. `status`: 0 open, 1 kept, 2 removed, 3 not the same hold.
* `keep` keeps the old hold (carried where it was), confirms the match, or keeps the detection as a new hold; `remove` removes the old hold, rejects a hand-placed merge, or leaves the detection out; `undo` puts the update's default back. Kinds 1 and 4 take no `remove`.
* `notSame` says the detection is not the same hold, for an unsure match (kind 1) or a hand-placed merge (kind 3: the merge is undone as with `remove`, which keeps the old hold confirmed where it was): the pair is taken apart as the full review's unlink does. The old hold is carried where the matcher predicts it, unconfirmed and marked for review (an unsure match answered so goes live like an unanswered card, minus the look-over finding), and the detection becomes a new hold. This holds on every panel: unlike `remove`, a `notSame` about a hold off the centre is applied as answered. Any other kind refuses it with 409. The card then reads `status` 3; `undo` (or `keep`) puts the pair back, the detection's own decision included.
* `canRemove` is false for a hold on a panel other than the centre (kinds 0 and 5): such a removal would be undone at the apply, so it is refused with 409; the hold is carried over and the look-over asks about it again.
* 400 without an answer; 409 when an answer is refused (the card is no longer part of the update, the update was replaced or discarded in the full review, the 3D check is running, the run is not at status 4). Several answers (at most 500 per call, 400 above) are applied in order and the first refusal stops the rest: those before it stay answered, and the answer says where it stopped, `{"answered": n, "failedCheckId": "<id>", "error": "…"}` (202 when some were saved, 409 when none were), so a script resumes after `failedCheckId`.

### The one review

Everything the page's review shows, in one list: the cards above, every old hold that was not found again, every new
detection that goes live and every detection the triage left out, and the "this hold may have moved" suggestions. It is
what the look-over would otherwise ask about after Apply, so a script can answer it all before Apply. Read it while the
run is at status 4.

```sh
# Counts, panels and items (groups and answers by name):
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN/review" | jq '{counts, items: [.items[] | {key, group, answers, suggested, answer, leavesFinding}]}'

# Answer items by key, in order:
curl -fsS -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"answers":[{"key":"k-<oldHoldId>","answer":"stillThere"},{"key":"d-<detectionId>","answer":"addAsHold"}]}' \
  "$BASE/api/walls/$WALL/refresh/$RUN/review/answers"

# Answer every unanswered item of a group (of one panel, optionally) with its suggested answer:
curl -fsS -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"group":"leftOut","panelId":null}' "$BASE/api/walls/$WALL/refresh/$RUN/review/accept"
```

| Method | Route | Body | Answers |
|---|---|---|---|
| GET | `…/refresh/{refreshId}/review` | — | 200 `{refreshId, sessionId, decisionsVersion, summaryUpdating, panels, items, counts}`; 404 unless status 4 |
| POST | `…/refresh/{refreshId}/review/answers` | `{"answers": [{"key", "answer"}]}` | 202 `{"answered": n, "counts"}` |
| POST | `…/refresh/{refreshId}/review/accept` | `{"group", "panelId"?}` | 202 `{"answered": n, "counts"}` |

* `key` names what the item is about: `c-<card id>` (a card, as in `…/checks`), `k-<old hold id>` (not found again),
  `n-<detection id>` (a new hold), `d-<detection id>` (left out by the triage), `r-<suggestion id>` (may have moved).
* `group`: `unsure`, `handPlaced`, `replaced`, `possiblyRemoved`, `conflicting3D`, `notFound`, `leftOut`, `newHold`, `moved`.
  Each item lists the `answers` it takes and the one Apply uses when nobody answers (`suggested`); `undo` takes an answer back.
  Cards: `keep`, `remove`, `notSame` (as above). Not found: `stillThere` (kept, confirmed: no look-over question) or `gone`
  (removed, on any panel; its boulders become history). New: `keepNew` or `notAHold`. Left out: `leaveOut` (kept out, its
  look-over finding is stored settled) or `addAsHold`. May have moved: `sameHold`, `moved`, `dismiss`.
* `counts.open` is the number of items that become look-over findings after Apply (unanswered cards, holds not
  found, left-out detections that look like a missed hold, and matches answered `notSame`, whose old hold the look-over still places): the finished run's `lookOver.openFindings` is that number, plus
  any finding the 3D model adds later. `counts.toCheck` counts every unanswered item; `counts.groups` the same per group.
* Answers are applied in order (at most 2000 per call); the first refusal stops the rest and answers
  `{"answered": n, "failedKey", "error"}` (202 when some were saved, 409 when none were). After answers the summary is made
  again in the background (`summaryUpdating`), without matching again; read `…/summary` for the new `decisionsVersion`
  before Apply.

### Consistency

* Reads (`GET …/refresh`, `…/{id}`, `…/summary`) change nothing: polling is side-effect free. `canApply` is true once the summary is ready and no check against this visit's new 3D model is pending.
* When the visit's 3D model becomes ready, the summary is worked out again with it before Apply is taken (`check3DPending: true`). The page starts that check by itself; a script starts it with `POST …/refresh/{id}/recheck`, then polls the summary again. It answers 202 `{"pending": true}` when it queued the check now (the only case recorded in the change journal), and 200 `{"pending": …}` when the check is already running or nothing is due.
* The visit's 3D capture runs on its own, beside the panel update, and its model **becomes the wall's active 3D model as soon as it is solved**, before Apply and whatever Apply does. That is by design: the 3D re-check above needs it, and the capture's follow-ups (placing the live holds on it, refining their 3D shapes) run on the holds that are live then, the previous generation's, as for any capture. Discarding the update does not undo the capture; to stay on the previous model, activate it again in the wall's model history (**Activate**); to update the panels without a new model, start the run with `keepModel` (no capture).
* `apply` needs the `decisionsVersion` of the summary that was checked (400 without one). The server refuses with 409 `{"error", "status", "currentDecisionsVersion"}` when the summary changed since, when there is nothing to apply yet, or while the 3D check runs.
* The accepted version is stored with the run, and the background apply compares what it would promote against **that** version, never against a summary written later. If anything changed in between (the full review, a re-check), nothing is promoted and the run is back at status 4 with the new summary.
* Apply and the background steps of a wall (sorting, preparing, the 3D re-check, applying) never overlap: they share one lock per wall. Apply waits a few seconds for a running step; while a 3D re-check (or another step) still holds the wall after that, Apply answers **409** with *"The update is being checked against the new 3D model. Try again in a moment."* and nothing changes. Scripts should treat that 409 as transient: read the summary again, and retry Apply with the (possibly new) `decisionsVersion` with backoff (for example 5 s, 10 s, 20 s, … capped at a minute), giving up after a few minutes.

### After the apply: the wall look-over

An update applied from a script without answering its cards goes live with all of them open. They are not lost: every finding nobody answered is kept with the wall, and the wall is flagged as waiting for a look-over until a wall admin or moderator has finished it (on the page, *Look over the wall*, `/walls/{wallId}/look-over`, or over the API below). The flag is set by every apply that leaves a finding (from a script, from the page, from the full review) and by nothing else; an update that leaves none does not flag the wall.

```sh
# The finished run says what it left behind (null when the wall is not flagged):
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/refresh/$RUN" | jq '.lookOver'   # { "pendingSince": "…", "openFindings": 118 }

# The wall's flag at any time, and the findings:
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/look-over"
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/look-over/findings" \
  | jq '.findings | group_by(.kind) | map({kind: .[0].kind, open: (map(select(.status == 0)) | length)})'
```

* `GET …/look-over` answers `pending`, `pendingSince` (the first update that left a finding since the last finished look-over; later updates do not move it), `generation` (the wall generation of the latest update that left one), `openFindings`, `totalFindings` (the open ones plus those answered since `pendingSince`), `completedAt` and `completedBy` (the last finished look-over, kept while the next one is pending).
* While `pending`, `bouldersMarked` is the number of current boulders marked for review and `moreBoulders` the number that are not marked but use a hold an open finding is about (an unsure match marks the hold, not its boulders). `missingHolds` is the number of open findings about a hold the update could not find (see below).
* The flag never clears by itself: with `openFindings` 0 (everything answered, or a later update superseded what was open) the wall stays `pending` until somebody presses *Finish look-over*.
* All look-over routes take the key of the panel update (a personal key with write access); its owner must be the wall's owner, an admin or a moderator of it, else 403. The reads change nothing.
* Answering, taking an answer back and finishing (each answers 200 with the look-over's state, 409 with the reason when refused, 400 without an answer):

| Method | Route | Body |
|---|---|---|
| POST | `…/look-over/findings/{findingId}/answer` | `{"answer": "Settle"\|"DifferentHold"\|"RemoveHold"\|"UseNewHold"\|"Reassign"\|"Moved"\|"AddHold"\|"Unlink" (or 0 … 7), "expectedBoulderIds": ["<id>", …], "newHoldBoulderIds": ["<id>", …] (optional, `UseNewHold` only), "targetHoldId": "<id>" (`Reassign`, optional with `DifferentHold`), "spot": {"panelId", "x", "y", "r"} (`Moved`)}` |
| POST | `…/look-over/findings/{findingId}/undo` | — |
| POST | `…/look-over/finish` | `{"leaveOpen": false}` |

* An answer must be one of the finding's `answers`. `expectedBoulderIds` are the ids of the finding's `boulders` as read; they are required for every answer but `Settle`, and when other boulders use the hold by now the answer is refused (409): read the findings again. `UseNewHold` can be answered per boulder ("Should this boulder use the new hold in that place?"): `newHoldBoulderIds` lists the boulders that should, a subset of `expectedBoulderIds` (400 otherwise, and 400 with any other answer). Left out or null, every boulder moves (as the merge does); a mix moves those boulders onto the new hold and removes the old hold, the other boulders becoming historic with it. Either is one journal batch, review marks included, that Wall settings › Possible duplicate holds undoes; an empty list is `RemoveHold`. A boulder whose review mark only this replacement explained loses the mark (the finding's `clearedBoulders`); a mixed answer reports `newHoldBoulders`. `undo` takes back an answer that changed nothing on the wall (a removed or merged hold is not undone). `finish` is refused while findings are open unless `leaveOpen` is true.
* The answers of a hold the update could not find (`holdKept`, the page's deck), with the page's checks:
  * "It is this hold": `Reassign` (4) with `targetHoldId`, a hold of the finding's photo that is not `kept` (list them with `GET …/look-over/holds?panelId=`). The kept copy is merged into it (its boulders move over, its name fills what the hold lacks; the hold keeps its own position and outline, and its own colour unless the old hold's colour was set by a person: a corrected colour moves over, a detector's proposal never overrides the hold's own); wall admins only (`canReassign`), undone in Wall settings › Possible duplicate holds. Needs `expectedBoulderIds`.
  * "It is here": `Moved` (5) with `spot` `{panelId, x, y, r}` (`x`, `y` 0..1 on the photo; `panelId` optional, refused when it is another photo; `r`, the radius in photo widths, optional). The hold is moved there, confirmed and marked for review so its outline can be refined. Only for a finding whose `answers` include `RemoveHold`, and only on bare wall: a spot inside the outline of a hold the update found (or within 3 px of the photo outside it) is that hold (409, answer `Reassign`). A spot right beside another hold is fine. `AddHold` with a `spot` means the same.
* "Different hold, and THIS hold replaced it": an unsure match (kind 1) answered `DifferentHold` may name `targetHoldId`, a hold of its photo that is not `kept` and not the hold asked about. The pair is taken apart as with plain `DifferentHold` (the detection the update paired it with stays a new hold of its own), and the named hold becomes the finding's `otherHoldId`. The finding then asks on as kind 5 with the named hold as the new one, so `UseNewHold` (with `newHoldBoulderIds` for a mix) moves the boulders that should onto it. Example: the matcher paired the old orange hold with a brown hold next to it while a new purple hold sits on the orange's spot; `DifferentHold` + `targetHoldId` = the purple hold, then `UseNewHold` + `newHoldBoulderIds` = the boulders that should use the purple hold.
* A finding has `kind` (0 possibly removed, 1 unsure match, 2 new hold with conflicting evidence, 3 matched to a hand-placed hold, 4 hand-placed hold with several detections, 5 another hold is here now, 6 not found again on a boulder, 7 an old hold kept in place with a new hold on the same spot, 8 a discarded detection, 9 two holds a full revision linked across two photos without being sure), `status` (0 open, 1 answered, 2 superseded by a later update of its panel, 3 closed because the hold was dealt with in the hold editor: `closedReason` 0 removed there, 1 merged in the duplicate review, 2 edited or confirmed there, 3 a hold was added on the spot), `holdId` (the live hold; null for kind 2 and once the hold was deleted), `oldHoldId`, `otherHoldId` (the new hold on the spot, kinds 0, 5, 6, 7), `boulders` (`id`, `name`, `needsReview`) and `answers`, the answers the page offers for it now (0 it is fine as it is, 1 different hold, 2 remove the hold, 3 the boulders should use the new hold). A recorded `answer` can also be 4 (the old hold is one of the holds on the new photo: merged into it) or 5 (the old hold was moved to where it is). An answered finding also says `answeredBy`, `answerBoulders` (boulders on the hold when it was answered), `clearedBoulders` and `canUndo`.
* On the page, "Still there" (kind 6) and "Same hold" (kind 1) say the update was wrong about the hold: a boulder that this update marked only for that hold loses its mark. No other answer clears a boulder, and a boulder that was marked before the update is never cleared. An answer that changed nothing on the wall can be undone on its card; a removed or merged hold cannot.
* A hold the update could not find again is kept as a copy, but as "kept, not confirmed": it is not drawn on the wall, in the hold pickers or in the 3D view (a boulder that already uses it still shows it, faded), it is not counted among the wall's holds, and creating or changing a boulder so that it newly uses such a hold is refused with 400 (*"One of the holds was not found again in the last update…"*). Each of them is a finding (`holdKept` true while it is unconfirmed, `keptAtUpdate` true for good; `covered` when it lies under a hold the update found on the new photo), with or without boulders. A person confirms it on the page ("Still here", "It is this hold", "It is here"), by editing it in the hold editor, or removes it.
* `pictures` lists the pictures of a finding (`view` 0 before the update, 1 now, 2 3D texture) with the `outlines` of the holds on each, in the picture's own 0..1 square; the picture itself is `GET …/look-over/findings/{id}/{old|new|model}` (`?plain=true` without the ring).
* What is changed in the hold editor is picked up whenever the look-over is read: a finding whose hold was removed, merged, edited or confirmed there is closed (`status` 3) and no longer counted as open.
* What counts as a finding: every card that was still unanswered when the update was applied; every old hold that was not found again and kept as a copy (on a boulder or not); and every old hold kept in place that has a new hold on its spot. A hold a person decided before the apply (an answered card, a confirmed verdict in the full review) is not a finding.
* A later update of the same panel supersedes what was still open on it (it has findings of its own). Finishing the look-over clears the flag and nothing else: boulders marked for review stay marked until their setter has checked them.

### Colours for new holds

The detector finds holds, not their colour. When an update is applied, every hold that is new in it gets the colour its new panel photo shows, when that can be told: clear yellow, red, pink, orange, blue, green, purple, white or black. A hold the estimate is not sure about (chalked over, between two colours, grey, wooden, or looking like the wall) stays without a colour. Carried holds keep what they had. A proposed colour is drawn like any other; in the hold editor it says *Suggested* until somebody keeps or changes it. The same hold on two panels gets one colour.

For holds that are already live without a colour (an update applied before this existed, or older holds), run the same estimate on the wall's live panel photos:

```sh
curl -fsS -X POST -H "$AUTH" "$BASE/api/walls/$WALL/holds/suggest-colors" | jq
# { "considered": 124, "proposed": 71, "fromLinked": 3, "leftUnset": 50, "changedMeanwhile": 0,
#   "proposedByColor": { "blue": 14, "yellow": 19, … }, "leftUnsetByReason": { "BetweenColors": 12, "MostlyChalk": 9, … } }
```

* The key of the panel update (a personal key with write access, owner an admin of the wall). On the page it is *Wall settings → Suggest hold colours*.
* It never touches a hold that has a colour, and it can be repeated: what was left unset stays unset until the photo changes.
* The photos are read without holding the wall up; each hold is looked at again just before it is written, and one that got a colour in between is left alone (`changedMeanwhile`). A wooden hold never gets a plastic colour.
* `fromLinked` are holds that took the colour of the same hold on another panel. One change-journal batch per run (`hold-color-proposals`).

## Wall preparation, end to end

```sh
AUTH="Authorization: Bearer $BLOCWERK_API_KEY"   # personal key, or this wall's key, with write access

# Current state: markers on/off, size, plan revisions (current, on the wall).
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/markers" | jq

# Switch the markers on with 125 mm squares (omit markerSizeMm to keep the stored size).
curl -fsS -X PUT -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"enabled":true,"markerSizeMm":125}' "$BASE/api/walls/$WALL/markers"

# Upload the planner's marker-plan.json (at most 2 MB, else 413) as the next revision. 422 with the issues when it
# has errors; 409 with "unchanged": true and the current revision when it is identical to it (no revision is added).
REV=$(curl -fsS -X PUT -H "$AUTH" -H 'Content-Type: application/json' \
  --data-binary @marker-plan.json "$BASE/api/walls/$WALL/marker-plan" | jq .revision)

# Once the printed markers are up: record it ("Markers swapped on the wall"). effectiveFrom defaults to now.
curl -fsS -X PUT -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{}' "$BASE/api/walls/$WALL/marker-plan/revisions/$REV/effective"

# Download the current plan again.
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/marker-plan" > marker-plan.current.json
```

A plan can also be attached to a single capture (`PUT /api/walls/{wallId}/captures/{captureId}/plan`), which saves it as a revision the same way.

## Change journal: export a wall update and replay it on another instance

Run the update once locally, check it, then carry exactly what it wrote to production. These routes replace the Development-only `/dev/journal/*` harness. They take a personal key with write access whose owner is an **administrator of the installation** (anything else is 403, no key is 401), exactly like the capture package routes.

| Method and route | What it does |
| --- | --- |
| `GET /api/v1/admin/journal/walls/{wallId}/batches?includeUnscoped=false&since=<iso time>` | The wall's batches, oldest first: `id`, `label`, `scopeKind`, `scopeId`, `status`, `actor`, `createdAt`, `sealedAt`, `entryCount`, `wallUpdateBatchId` (the wall update the batch belongs to: the `wall-update` batch itself, or the one that was open when the batch was created; null otherwise). `includeUnscoped=true` adds the unscoped `adhoc` batches since `since` (default: the last 7 days) that touched the wall. |
| `GET /api/v1/admin/journal/export?updateBatchId=<id>` | **A whole wall update**: the `wall-update` batch and every batch of the wall created while it was open (nested and `adhoc` ones), as gzip-compressed JSON (`application/gzip`), panel photos inside, in the source's write order. 404 when the id is not a `wall-update` batch. |
| `GET /api/v1/admin/journal/export?wallId=<id>&since=<iso>&until=<iso>&includeUnscoped=true` | The wall's batches created in that window (adhoc ones included unless `includeUnscoped=false`). Replays, reverts and reverted batches are left out of both modes above. |
| `GET /api/v1/admin/journal/export?batchId=<id>&batchId=<id>…` | Exactly the batches named. Name exactly one of `updateBatchId`, `wallId` or `batchId` (else 400). |
| `POST /api/v1/admin/journal/replay?dryRun=true&force=false` | Body: the package, gzip or plain JSON, up to 1 GB. **A dry run unless `dryRun=false`.** Returns the report: `applied`, `dryRun`, `rows[]` (`seq`, `batchId`, `entityType`, `keyJson`, `outcome` 0 applied / 1 already applied / 2 conflict, `detail`), `fatalErrors[]`, `replayBatchId` (also in the `X-Replay-Batch-Id` header; it is the batch to revert to undo the replay, listed under the package's wall). 200 for a dry run or a committed run; 409 with the report when a real run was refused; 409 with a problem body (`title`, `detail` naming the constraint) when the database refused a write while applying (nothing committed); 499 when the client went away (rolled back). |
| `POST /api/v1/admin/journal/batches/{batchId}/revert?dryRun=true&force=false` | Reverts one batch on this instance. **A dry run unless `dryRun=false`.** 200 with `reverted`, `dryRun`, `conflicts[]`, `revertBatchId`; 404 for an unknown batch; 409 when a real revert was refused. |

```sh
AUTH="Authorization: Bearer $BLOCWERK_PERSONAL_API_KEY"   # personal key with write access, owner is an installation admin

# Locally: find the update's batch and export it.
curl -fsS -H "$AUTH" "$LOCAL/api/v1/admin/journal/walls/$WALL/batches?includeUnscoped=true" | jq '.[] | {id, label, sealedAt, entryCount, wallUpdateBatchId}'
curl -fsS -H "$AUTH" -o update.json.gz "$LOCAL/api/v1/admin/journal/export?updateBatchId=$UPDATE"

# On production: dry run first (the default), read the conflicts, then apply.
curl -sS -X POST -H "$AUTH" --data-binary @update.json.gz "$BASE/api/v1/admin/journal/replay" | jq '{dryRun, fatalErrors, conflicts: [.rows[] | select(.outcome == 2)]}'
curl -fsS -X POST -H "$AUTH" --data-binary @update.json.gz "$BASE/api/v1/admin/journal/replay?dryRun=false" | jq '{applied, replayBatchId}'
```

* **Schema parity is never forced.** The package records the source's last EF migration. A target on another migration refuses the replay with a fatal error, even with `force=true`. `force` only pushes past row conflicts. Use it only when you have read every conflict.
* **Replaying is idempotent.** Rows already at the package's result report `outcome` 1 and are left alone, so a package can be sent again after a partial failure.
* **One wall update is one batch.** Staging, every touch-up of the staged holds (the Detected and Touchup steps, and the full review's editor) and the promote all record into the wall's `wall-update` batch, so exporting that batch alone carries the whole update. So is the triage's discard reason on each staged detection. Updates recorded before this change have their touch-ups in separate `adhoc` batches: `export?updateBatchId=` picks them up (they were written while the update was open), and the package is ordered by write time, so they replay in the order they were made.
* Later batches on the wall (look-over answers, colour proposals) can go in the same package or in their own, sent afterwards. Do not export placement or remeasure batches: production recomputes those when the capture is imported (replay the journal first, then import the capture).
* Not in the journal, so not carried: look-over findings, update sessions, refresh runs and captures, and the look-over's "missing" `NeedsReview` flag.
* Memory: the replay holds the whole package in memory. Expect a peak of roughly 3 to 4 times the uncompressed package size (the Base64 text as .NET strings, plus the decoded photos the applied rows need), so about 1 to 1.2 GB for a 300 MB package. Blobs no row needs are never decoded.

## Still in the browser

* Looking over the wall after an update: "It is this hold" and "It is here" (the other answers, undo and finish have routes).
* The full step-by-step review of a panel update (moved holds, removals, overlap links below the threshold, touch-up).
* Generating or editing a marker plan and printing its PDF (the API stores a plan made in the planner).
* Wall segments' marker bindings and importing a `wall-geometry.json` by hand.
