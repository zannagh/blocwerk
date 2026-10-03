# Automating a wall update and wall preparation

Rollout scripts can run the panel update ("Update panels + 3D") and prepare a wall for marker captures over the REST API with an API key. **Neither flow needs a browser session any more**, so the API-key browser login ([api-key-login.md](api-key-login.md)) can stay switched off on production for them.

Every route calls the same service the page calls, so the rules are the page's rules: the key's owner must be an admin of the wall, kiosk tablets are refused, and the panel update promotes nothing before Apply.

## Keys

| Flow | Personal key with write access | Wall key with write access (for that wall) | Read-only keys, kiosk, installation keys |
| --- | --- | --- | --- |
| Panel update (`/api/walls/{wallId}/refresh`, `/api/refreshes/{id}/files`) | yes | **no** (403) | no (403) |
| Wall preparation (`/api/walls/{wallId}/markers`, `…/marker-plan`, `…/marker-plan/revisions`) | yes | yes | no (403) |

* "Write access" is the *Allow this key to change walls* box when creating the key under *Settings → API keys*.
* The owner has to be an admin of the wall (owner or member with the Admin role). Being an administrator of the installation is not enough on its own, exactly as in the browser: the request answers 403.
* Wall keys are kept out of the panel update on purpose. They live on devices bolted to the wall and must be assumed to leak, the update's file drop never admitted them, and replacing panel photos and promoting a new hold generation is a person's decision. Marker preparation follows the existing wall-admin API rules (captures, marker-plan revisions), which admit a wall key with write access for its own wall.

## Audit

Every successful write through these routes is recorded in the change journal as a batch labelled `api:<action> key:<api key id>`, scoped to the wall (the file upload: to no aggregate, with the run id in the label), with the key's owner as the actor. Rows the write changes that the journal tracks (the wall's marker switch and size) are recorded in that batch with their before and after values. Refused writes record nothing; they and every write are also in the server log with the key id.

Actions: `refresh.begin`, `refresh.upload run:<id>`, `refresh.sort`, `refresh.start`, `refresh.apply`, `refresh.discard`, `markers.set`, `marker-plan.save`, `marker-plan.effective rev:<n>`.

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

`start` takes an optional body `{"choices":[{"col":0,"row":0,"photoId":"<id>"}]}` to override the proposal per panel (`photoId: null` keeps that panel's current photo). Without it the run uses the sorter's picks and the quick review's decisions (every old hold carried, only sure overlap links), the same as pressing *Start* and *Apply* on the page without changing anything. Anything finer (moved holds, removals) remains the full review in the browser.

### Consistency

* `GET …/summary` changes nothing. `canApply` is true once the summary is ready and no check against this visit's new 3D model is pending.
* `apply` needs the `decisionsVersion` of the summary that was checked (400 without one). The server refuses with 409 `{"error", "status", "currentDecisionsVersion"}` when the summary changed since, when there is nothing to apply yet, or while the 3D check runs.
* The background apply compares the version once more against what it would promote. If an admin changed the update in the full review in between, nothing is promoted and the run is back at status 4 with the new summary.

## Wall preparation, end to end

```sh
AUTH="Authorization: Bearer $BLOCWERK_API_KEY"   # personal key, or this wall's key, with write access

# Current state: markers on/off, size, plan revisions (current, on the wall).
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/markers" | jq

# Switch the markers on with 125 mm squares (omit markerSizeMm to keep the stored size).
curl -fsS -X PUT -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"enabled":true,"markerSizeMm":125}' "$BASE/api/walls/$WALL/markers"

# Upload the planner's marker-plan.json as the next revision. 422 with the issues when it has errors;
# the same plan again adds no revision ("unchanged": true).
REV=$(curl -fsS -X PUT -H "$AUTH" -H 'Content-Type: application/json' \
  --data-binary @marker-plan.json "$BASE/api/walls/$WALL/marker-plan" | jq .revision)

# Once the printed markers are up: record it ("Markers swapped on the wall"). effectiveFrom defaults to now.
curl -fsS -X PUT -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{}' "$BASE/api/walls/$WALL/marker-plan/revisions/$REV/effective"

# Download the current plan again.
curl -fsS -H "$AUTH" "$BASE/api/walls/$WALL/marker-plan" > marker-plan.current.json
```

A plan can also be attached to a single capture (`PUT /api/walls/{wallId}/captures/{captureId}/plan`), which saves it as a revision the same way.

## Still in the browser

* The full step-by-step review of a panel update (moved holds, removals, overlap links below the threshold, touch-up).
* Generating or editing a marker plan and printing its PDF (the API stores a plan made in the planner).
* Wall segments' marker bindings and importing a `wall-geometry.json` by hand.
