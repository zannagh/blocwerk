# Companion app: what a device key may read

The iPhone app links to an account through `/device-link` and gets a **device key**: a personal `bwk_` key marked as a device (`ApiKeys.IsDevice`, claim `blocwerk:apikey_device`). It is listed as "iPhone: <device>" under *Settings → API keys* and can be revoked there.

With that key the app reads exactly what the offline web client (`/v/`) reads, as the key's owner. A personal key made by hand on the settings page still cannot read any of it.

## Routes

Send `Authorization: Bearer bwk_…`. No cookie and no antiforgery token. The routes in this table are `GET` (the image and document routes take `HEAD` too); the writes are listed under *Your marks* below.

| Route | Before | Now |
| --- | --- | --- |
| `/api/walls/{w}/docs` | human, share token, kiosk; every key 404 | the same, plus a device key |
| `/api/walls/{w}/d/{kind}/{sha}` | the same as above | plus a device key |
| `/api/walls/{w}/a/{sha}`, `/a/{sha}/{width}` | the same as above | plus a device key |
| `/api/walls/{w}/manifest` | the same as above | plus a device key |
| `/api/walls/{w}/changes` | the same as above, rate limited per address | plus a device key, same limit |
| `/api/walls/{w}/feed/stream` (SSE) | the same as above, rate limited per address | plus a device key, same limit |
| `/api/me/changes` | signed-in human only (outside the API-key surface) | unchanged |
| `/api/me/walls/{w}/mine` | signed-in human only (outside the API-key surface) | unchanged |
| `/api/v1/me/walls` | did not exist | **device key only**: the web My Walls list (the walls the owner is a member of, same order), with `id`, `name`, `memberCount`, `hasPhoto`, `photoSha`, `role` (Admin, Moderator or Member; the owner is Admin) and `isOwner` (read the card photo as `/a/{photoSha}/640`); rate limited |
| `/api/v1/me/changes` | did not exist | **device key only**, same handler as `/api/me/changes`, rate limited |
| `/api/v1/me/walls/{w}/mine` | did not exist | **device key only**, same handler as `/api/me/walls/{w}/mine`, rate limited |
| `/api/v1/walls/{w}/boulders/{b}/attempts` | did not exist | **device key only**: everyone's log on the boulder, newest first, as the web boulder page's History (see *Boulder reads*) |

`/api/me` stays cookie-only. The app uses the `/api/v1/me` aliases, because a key only authenticates on the API-key surface (`ApiKeySurface`).

## Boulder reads

- **Your rating.** `/api/v1/me/walls/{w}/mine` now carries `ratings: [{ boulderId, stars }]`, the caller's own star rating per boulder of the wall (absent from an older server's document). It lives there rather than behind a new `/api/v1/me/ratings?wallId=` read because the mine document is already the per-reader overlay the boulder list and page read, with one fetch per wall and the same gate (`MineAccessGate`). The feed already writes a `rating.mine` row to `/api/v1/me/changes` on every rating change, which tells the app to read the document again, exactly as for ticks and favourites.
- **Everyone's log.** `GET /api/v1/walls/{w}/boulders/{b}/attempts` answers `[{ id, userId, userName, type, timestamp, mine }]`, newest first, where `type` is `Attempt`, `Send` or `Flash` and `mine` marks the caller's own rows (the ones the web lets them delete). It calls the web page's own services (`IBoulderService.GetBoulderAsync`, then `IAttemptService.GetAttemptsForBoulderAsync`), so the visibility is the web's: members of the wall only, drafts included for them. A wall the owner is not a member of, an unknown boulder and a boulder of another wall all answer the same bare 404. Counted in the key's sync bucket (the one the documents and images share), `no-store`.

## Your marks

The app writes the owner's marks on a boulder with the offline web client's bodies, service calls and answers (`/api/offline/*`), so a write from the app lands exactly as one from the browser. **Device key only**, `no-store`, and limited per key (not per address): a bucket of 60 writes, refilled 1 a second, shared by every device write; beyond it 429 with `Retry-After: 1`.

| Route | Body | Answer |
| --- | --- | --- |
| `POST /api/v1/me/ratings` | `{ boulderId, stars: 1-5 }` | `{ applied: true, clientRequestId, result: { stars } }` |
| `DELETE /api/v1/me/ratings/{boulderId}` | none | `result: { stars: null }`; no rating is a no-op |
| `POST /api/v1/me/favorites` | `{ boulderId, favorite: bool }` | `result: { favorite }` (absolute, never a toggle) |
| `DELETE /api/v1/me/favorites/{boulderId}` | none | `result: { favorite: false }` |
| `POST /api/v1/me/projects` | `{ boulderId, project: bool }` | `result: { project }` |
| `DELETE /api/v1/me/projects/{boulderId}` | none | `result: { project: false }` |
| `POST /api/v1/me/comments` | `{ boulderId, text, clientRequestId? }` | `result: { commentId }`; the same `clientRequestId` returns the stored comment |
| `DELETE /api/v1/me/comments/{commentId}` | none | `result: { commentId }`; the caller's own comments only |
| `POST /api/v1/me/attempts`, `DELETE /api/v1/me/attempts/{id}` | unchanged (any personal key) | ticks and attempts, with `clientRequestId` and `timestamp` |

A refusal is `{ message, permanent }`. `permanent: true` means a retry can never succeed, so a queue drops the entry: 400 for a bad body (no boulder id, stars outside 1-5, empty text or more than 1024 characters), 404 for an unknown boulder, a boulder on a wall the owner is not a member of (the same answer, so it does not tell the boulder exists), or a comment that is not the owner's or is already gone, 403 for a draft (attempts). A `clientRequestId` is a replay key for its own sender only: another user's write with the same id is their own new write. The empty id is a 400, and so is a comment id reused on another boulder. `permanent: false` (401, 500) means keep it queued and retry.

## Profile

**Device key only**, rate limited like the feed, `no-store`. The fields are what the web profile page keeps on the account. Appearance, zoom lens and editor toolbar are stored in a browser cookie, so they are not here. Avatar changes and account security (e-mail, password, two-factor, linked accounts) stay on the web.

| Route | Body / query | Answer |
| --- | --- | --- |
| `GET /api/v1/me/profile` | none | `{ id, name, accountName, customDisplayName, role, memberSince, avatarUrl, homeWallId, gradeScale: "font"\|"v", notifications: { SessionStarted: true, … } }` |
| `PATCH /api/v1/me/profile` | merge patch: `{ displayName?, homeWallId?, gradeScale?, notifications?: { BoulderAdded: false } }` | the whole profile, as `GET` |
| `GET /api/v1/users/{id}` | `?windowDays=90&groupBy=Week` | `{ id, name, role, memberSince, avatarUrl, progression }` (`progression` as `/api/v1/me/progression`) |

- **PATCH.** A field you leave out stays as it is. `displayName: null` (or blank) resets it to the account name, and `homeWallId: null` clears the home wall. The answer is 400 for an unknown field, a `gradeScale` other than `font` or `v`, an unknown notification type, a non-boolean value, a display name over 256 characters or with invisible or text-direction characters (bidi overrides and isolates, direction marks, zero-width characters; a zero-width joiner between two emoji, as in the woman climbing, is fine; the web's save follows the same rule), or a home wall you are not a member of. A refused body changes nothing. The patch takes the per-key write limit (60, then 1 a second); the reads take the feed's.
- **Other members.** The web page's rule applies: you see a profile when you share a wall with that person (or it is your own). Anyone else, and an unknown id, gets 404. `windowDays` is clamped to 7–365. `avatarUrl` is the web's `/api/users/{id}/avatar`, which needs no key.

## Walls: members, links, joining

**Device key only**, `no-store`. The two reads take the feed's rate limit and the four `POST` routes the per-key write limit. These are the web wall page's member list and buttons, through the same service calls and so under the same rules.

| Route | Who | Answer |
| --- | --- | --- |
| `GET /api/v1/walls/{w}/members` | members | `[ { userId, name, avatarUrl, role, isOwner, joinedAt } ]` in join order |
| `GET /api/v1/walls/{w}/hold-links` | admins and moderators | `{ panelCount, linkCount, linksFinalized }`; the web's "link holds across panels" reminder shows while `panelCount >= 2` and not `linksFinalized` |
| `POST /api/v1/walls/{w}/invite-link` | members (the Invite button) | `{ token, url: ".../join/{token}", expiresAt: null }`; the same link each time, made on first use |
| `POST /api/v1/walls/{w}/invite-link/rotate` | admins (Generate Share Link) | a new `{ token, url }`; the old link stops working |
| `POST /api/v1/walls/{w}/visit-links` | members (the Visit button) | `{ token, url: ".../visit/{token}", expiresAt }`; one-time, read-only; at most 20 an hour, then 429 |
| `GET /api/v1/join/{token}` | anyone with an invite token | `{ wallId, name, memberCount }` without joining, for "Join <wall>?"; 404 for an unknown token, 403 on a kiosk |
| `DELETE /api/v1/walls/{w}/members/{userId}` | admins (not the owner, not themselves) | 204; 404 for someone not on the wall, 403 for the owner or oneself, 409 when the membership changed at the same moment |
| `POST /api/v1/walls/{w}/leave?deleteBetaVideos=true` | members but the owner | 204; 403 for the owner, 409 when the membership changed at the same moment; `deleteBetaVideos` (default `true`) also deletes the caller's beta videos on this wall |
| `POST /api/v1/join/{token}` | anyone with an invite token | `{ wallId, name }`; joins as Member, joining again is a no-op; 404 for an unknown or replaced token |

A wall the caller is not a member of answers 404 on every wall route, as an unknown wall does. Links start with the configured server address (`Blocwerk:Server:Url`, env `SERVER__URL`) when it is set to a public address, else with the request's origin, as the web pages' own links do. A plain member gets 403 on `hold-links` and `rotate`. A visitor link is redeemed in a browser (`/visit/{token}`), not by the app.

## Activity

**Device key only**, `no-store`. These are the Activity page's own edits and its day panel, through the same `IProgressionService` calls. The edits take the per-key write limit, the day read the feed's.

| Route | Body | Answer |
| --- | --- | --- |
| `PATCH /api/v1/me/progression` | `{ windowDays?: 7-365, groupBy?: "Day"\|"Week"\|"Month" }` | the progression under the saved preference, as `/api/v1/me/progression` |
| `PATCH /api/v1/me/activities/{id}` | `{ durationMinutes: 0-1440 }` | the activity, as `/api/v1/me/activities/{id}`; 404 for someone else's |
| `GET /api/v1/me/days/{yyyy-MM-dd}` | none | `{ date, boulders, hangboard, pullups, sessionDurationMinutes }` for any day, also outside the window |
| `PUT /api/v1/me/avatar` | the image bytes, `Content-Type: image/jpeg`, `image/png` or `image/webp`, at most 30 MB | the profile, as `GET /api/v1/me/profile`; stored scaled to 512 px as WebP, like the web |
| `DELETE /api/v1/me/avatar` | none | the profile without an avatar |

Added to existing routes (any personal key, as before): `wallName` on `/api/v1/me/sessions` answers, `gymName` (the TopLogger gym) on `/api/v1/me/activities` and `/activities/{id}`, and an optional `clientRequestId` on `POST /api/v1/me/training/hangboard`, `/training/pullups` and `/sessions`. The same key from the same user returns the row already saved; a replayed session start ends nothing; another user's key is their own new row; the empty id is a 400. Ending a session is idempotent already and takes no key.

### Removing and leaving

Removal and leaving end a membership the same way, following what account deletion does for a person who leaves every wall, narrowed to this wall:
- **Gone:** the membership (and with it the kiosk PIN and consent), the person's favourites and projects on this wall's boulders, their home-wall choice if it was this wall, and the visitor links they made for this wall (a visit already under way ends too). An open session on this wall is ended.
- **Beta videos** the person uploaded to this wall: deleted on a removal, as account deletion deletes them (they show the person, who can no longer reach the wall to take them down); on leaving, deleted unless `deleteBetaVideos=false`.
- **Moved:** wall keys (tablet, sensor) the person minted for this wall go to the owner under a neutral name.
- **Kept:** what other members see stays, with the person's name on it: boulders, setter credits, comments, ratings, attempts and ticks and the wall's history. Their training history is not wall data and is untouched.
- **Invite:** the invite token is replaced, so an old invite link no longer joins. The public share link (`/walls/{w}/shared/{token}`, a separate token) keeps working.
- **Activity:** the wall's log gets "removed X" or "X left".
- **Feed:** `member.changed` goes to the wall; `access.revoked` and `walls.changed` go to the person's own devices; `video.deleted` per deleted clip.

## Gallery and beta videos

The app adds and removes media through the web's own upload code, as the key's owner. **Device key only**, `no-store`, and every write counts in the per-key write bucket above.

| Route | Who | What |
| --- | --- | --- |
| `POST /api/v1/walls/{w}/gallery?clientRequestId=` | wall admins (owner or Admin) | one photo, multipart with a `file` part or the raw image body, as the wall camera sends it |
| `DELETE /api/v1/walls/{w}/gallery/{imageId}` | wall admins | an uploaded photo of that wall (the wall photo and retired photos are not deletable) |
| `GET /api/v1/walls/{w}/boulders/{b}/beta-videos` | members | the boulder page's beta carousel, newest first: `[{ id, uploadedByUserId, uploaderName, createdAt, contentType, sizeBytes, encodingStatus, thumbnailSha, videoUrl, hlsUrl, thumbnailUrl, mine }]` |
| `POST /api/v1/walls/{w}/boulders/{b}/beta-videos?clientRequestId=` | members | one clip, multipart with a `file` part (a video type) and an optional `thumbnail` part, as the boulder page sends it |
| `DELETE /api/v1/walls/{w}/boulders/{b}/beta-videos/{videoId}` | the uploader | their own clip |
| `GET /api/v1/beta-videos/{id}`, `/thumbnail`, `/hls/{*path}` | members | the web's own playback handlers for `/api/beta-videos/{id}…`: the file with range requests, the poster frame, the HLS ladder |

- **The same pipeline.** A gallery photo goes through the camera's pipeline (`WallImageIntake`, shared with `/api/walls/{w}/images`):
  - at most 20 MB, JPEG, PNG or WebP only (415 otherwise);
  - the bytes must be the declared format: a HEIC or TIFF sent as `image/jpeg` is a 415 (`GalleryImageCheck`);
  - the header must name at most the app's pixel limit (400 otherwise), read without decoding, because the resized copies decode it whole;
  - location and every other piece of metadata are stripped before the file is stored (`StoredPhotoSanitizer`).
- **Beta clips** go through the browser's streamed upload (`BetaVideoUploadEndpoint.UploadAsync`, with its body read by `BetaVideoMultipart`):
  - exactly one `file` part of a `video/*` type (a second one is a 400);
  - a `thumbnail` part of at most 512 KB and a `fileName` of at most 1 KB, refused beyond that without reading further;
  - then the background normalizer and HLS ladder, as for a clip from the web.
  No exit leaves a temp file behind, and a scheduled sweep (`UploadHousekeepingWorker`, hourly) deletes upload temp files older than 12 hours.
- **Quota.** Each user may upload at most 10 GB of beta clips within 30 days, counted over all walls and the web too (`BetaVideo:QuotaBytes`, `BetaVideo:QuotaWindowDays`; 0 turns it off). It is checked before the body is read (with the declared length) and again with the real size. Over it, the answer is 409 with a message saying how much was used. On the app's route that 409 is `permanent: true`, because only time or a deleted clip frees space.
- **Who.** The web lets nobody but a wall key add gallery photos, and lets wall admins delete them. The app takes the admin rule for both (`IWallImageService.EnsureCanUploadAsync`, the same `WallAdminGuard` as the delete). Beta clips keep the web's rule: members upload, the uploader deletes. A wall the owner is not a member of, an unknown boulder and a boulder of another wall answer the same 404; a member who is not an admin gets 403 on the gallery.
- **Gallery per boulder.** The web has no photo gallery per boulder (a boulder's media are its beta clips), so there is none here either. The app reads the wall gallery through the `gallery` document; a new or deleted photo reaches it through the feed (`gallery.changed`, `gallery.deleted`), a clip through `video.added`, `video.status` and `video.deleted`.
- **Size and resumption.** Neither web upload is chunked or resumable: each is one streamed request. So the app's are too, with a cap: 20 MB a photo (the web's) and 1 GiB a clip (the web allows up to its configured 4 GB, which is not sane for one request over a phone connection). A clip needs a `Content-Length`, so it is refused before a byte is read: 411 without one, 413 over the cap.
- **Replay.** `clientRequestId` goes in the query string, so a retry is recognised before its body is read. The first request takes the id in a receipt (`DeviceWriteReceipts`, unique on user and id) before it writes. Then:
  - A retry after the write returns the stored photo or clip, with no second row.
  - A retry while the write is still running gets 409 `permanent: false`.
  - A write that failed frees the id again.
  - Every app upload runs under a 30-minute deadline. A receipt left unfinished for 35 minutes, past that deadline, belongs to a request that died, so the next retry takes it over.
  - Receipts are deleted after 30 days by the same hourly sweep.
  - The same id on another wall, boulder or kind of write is a permanent 400.
- **Playback.** `hlsUrl` is null until the clip has a ladder; play `videoUrl` then. Pending and Processing clips answer 404 until they are normalized, as on the web. The HLS playlists use relative URIs, so a player that sends the key on every request of the asset (AVURLAsset with HTTP header fields) follows them under `/api/v1/beta-videos/{id}/hls/`. Playback counts in the key's sync bucket.

## Setting boulders

The setter flow, through the web's own service calls (`IBoulderService.CreateBoulderAsync`, `ReviseBoulderAsync`). **Device key only**, `no-store`, per-key write limit. `clientRequestId` is **required** on both routes (400 without). Answers are `{ applied, clientRequestId, result: { boulderId, wallId, name, grade, isDraft, holdCount } }` or `{ message, permanent }`.

### Create: `POST /api/v1/walls/{w}/boulders`

Members only. The body is the offline client's create:

```json
{ "clientRequestId": "<guid>", "name": "Crimp line", "grade": "6B+",
  "holds": [ { "holdId": "<guid>", "type": "Start", "usage": "HandAndFoot" } ],
  "isDraft": false, "kickboardFootholdsOn": true, "handsFollowFeet": true, "footColorOnly": null,
  "noMatch": false, "customRule": null, "styles": 0, "setters": [], "variantOfBoulderId": null, "id": null }
```

- With `variantOfBoulderId` it makes a variant. An origin that is gone, a draft, or on a wall you are not a member of gives the same answer: the boulder is created without the link. An origin on another wall you can see is a 400.
- `id` is optional and client-minted, as in the offline queue. Your own boulder's id on another wall is a permanent 409 (`This boulder id is already used on another wall.`), not a replay.

### Revise: `PUT /api/v1/walls/{w}/boulders/{b}`

The creator, a setter of the boulder, or a wall admin. The body has its own shape, and **every field is optional: a field left out (or null) keeps the boulder's current value**.

```json
{ "clientRequestId": "<guid>",
  "name": "Crimp line", "grade": "6C", "kickboardFootholdsOn": true, "handsFollowFeet": true,
  "footColorOnly": "red", "noMatch": false, "customRule": "No dyno", "styles": 1,
  "holdTypes": [ "Crimp" ], "holds": [ { "holdId": "<guid>", "type": "Normal", "usage": "HandAndFoot" } ],
  "setters": [ "<user id>" ] }
```

- **Unchanged vs cleared:**

  | Field | Absent or `null` | Clears it |
  | --- | --- | --- |
  | `setters` | unchanged | `[]` |
  | `customRule` | unchanged | `""` |
  | `grade`, `footColorOnly` | unchanged | `""` |
  | `holdTypes` | unchanged | `[]` |
  | `styles` | unchanged | `0` |
  | `name`, `kickboardFootholdsOn`, `handsFollowFeet`, `noMatch` | unchanged | (a value only) |
- **Holds:** `holds` replaces the holds and is never empty. Left out, a published boulder keeps its holds and its review state, and a draft keeps its own. A historic boulder (from an earlier wall generation) must be sent with holds; without them it is a 400, as on the web.

### Rules for both

- **The web's rules.** The service decides as for the browser:
  - any member may set;
  - only the creator, a setter or a wall admin may revise (403);
  - a hold the last update did not find again must be confirmed first (400);
  - a variant stays on its parent's wall (400).
- **Holds on the wall.** A hold that is not on the boulder's wall, or does not exist, is a 400 (`One of the holds is not on this wall.`). This is checked in the service, so it covers the web and the offline queue too.
- **Body checks.** Every check below is a permanent 400, made before the service runs, so a bad body is never a retryable 500:
  - a name, at most 256 characters;
  - a grade from the pickers (Font or V);
  - known style bits and hold-type names (at most one of each type);
  - a custom rule of at most 200 characters;
  - a foot colour of at most 32 characters;
  - 1 to 200 holds, each listed once;
  - at most 20 setters, each listed once;
  - a `wallId` in a create body must match the route.
- **Errors.** A wall the owner is not a member of, an unknown boulder and a boulder of another wall answer 404.
- **Replay.** A retry of a create returns the boulder it made. A retry of a revise returns the boulder as it is now, and never applies an old snapshot over a newer revision.
- **Feed.** The save writes the `boulder.changed` rows (and `activity.added` for a published boulder) that other devices and the web update from.

## Wall settings, photo and roles

The native wall settings pages call the web settings card's own service calls. **Device key only**, `no-store`; reads count in the key's sync bucket, writes in its write bucket. A refusal is `{ message }`: 400 for a bad body, 403 for a member without the role, 409 for a state that does not allow it. A wall the caller is not a member of is a bare 404, the same as an unknown wall.

| Route | Who | Body | Answer |
| --- | --- | --- | --- |
| `GET /api/v1/walls/{w}/settings` | members | none | `{ name, description, angle, underMaintenance, myRole: "Member"\|"Moderator"\|"Admin", ownerId, photoSha, panelCount, allowAnonymousKiosk, allowKioskKeyboardShortcuts }` |
| `PATCH /api/v1/walls/{w}/settings` | admins (the owner included) | any of `{ name, description, angle, underMaintenance, allowAnonymousKiosk, allowKioskKeyboardShortcuts }` | the settings, as `GET` |
| `PUT /api/v1/walls/{w}/photo?clientRequestId=` | admins | the raw photo: `image/jpeg`, `image/png` or `image/heic` | `{ photoSha }` |
| `PUT /api/v1/walls/{w}/members/{userId}/role` | admins | `{ role: "Member"\|"Moderator"\|"Admin" }` | `{ userId, role }` |

- **PATCH.** It is a merge patch: a field that is absent (or `null`) stays as it is, and `description: ""` clears the description.
  - Each change runs through the web's own call: name, description and angle through `UpdateWallAsync`; update mode through `SetMaintenanceAsync`; the kiosk switches through `SetAnonymousKioskSettingAsync` and `SetKioskKeyboardShortcutsAsync`.
  - These values are refused with 400: an unknown field, a value of the wrong type, a blank name or one over 256 characters, a description over 1024, an angle outside 0–90.
  - Every value is absolute, so a retry is harmless and the route needs no `clientRequestId`.
- **Photo.** This is the web's upload for a wall that has **no photo and no panels yet** (`UploadPhotoAsync`: it strips the metadata, makes the centre panel and detects the holds).
  - A wall that already has a photo or panels answers 409; it is updated through the wall update.
  - The body goes through the gallery's checks:
    - at most 20 MB (413);
    - the bytes must be the declared type (415);
    - a HEIC is converted to an upright JPEG with no metadata, or refused (415) where the server has no converter;
    - the picture must stay under the pixel limit (400).
  - Every upload detects holds again, so it is not idempotent: `clientRequestId` is **required** (query string). A retry returns the stored photo's sha.
- **Role.** Admins set any member's role through `SetMemberRoleAsync`. The owner's role can never be changed (403, now refused in the service for the web too). An unknown member is a 404 `{ message: "Member not found" }`.

## Rules

- **Who gets in.** The `/api/v1/me` write routes use the `DeviceApiKey` policy and answer any other caller 404. The sync routes use the `WallSync` policy and the `DenyApiKeyPrincipalsExceptDevices()` filter. Both admit a device key and refuse every other key with the same bare 404 as before: a plain personal key, a wall key, a kiosk key or an installation key. The `/api/v1/me` routes (walls, changes, mine) use the `DeviceApiKey` policy, so a browser cookie does not work there either.
- **What it sees.** The grant is unchanged. `FeedAccessGate`, `DocumentAudienceGate`, `AssetAccessGate` and `MineAccessGate` decide for the key's owner exactly as for that owner's cookie: the walls they belong to, their role's audiences (drafts only for setters), a share token as in the browser. A wall they cannot see answers 403 on the document and feed routes and 404 on the image and manifest routes.
- **Kiosk and visitor links.** These are unchanged. Both are cookie sessions, and a request authenticated by a key is never a kiosk.
- **Rate limits.** The feed's per-address token bucket covers the wall catch-up, the stream and the three `/api/v1/me` routes, whoever the caller is. The boulder log counts in the device key's own sync bucket. As before, the document, image and manifest routes have no limiter for anyone.
- **Revocation.** A revoked key is refused on its next request. An open stream ends within its 5-minute lifetime, and the reconnect is refused.
- **Never open to a device key.** `/api/offline/*`, Blazor pages, the browser's gallery byte route (`/media/…`; the app reads gallery images as `/a/{sha}`), the panel and photo routes, 3D geometry, `/api/beta-videos/*` (the app has its `/api/v1` aliases), and the other `/api/me/*` routes.
