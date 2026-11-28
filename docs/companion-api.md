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
| `/api/v1/me/walls` | did not exist | **device key only**: the web My Walls list (the walls the owner is a member of, same order), with `id`, `name`, `memberCount`, `hasPhoto` and `photoSha` (read the card photo as `/a/{photoSha}/640`); rate limited |
| `/api/v1/me/changes` | did not exist | **device key only**, same handler as `/api/me/changes`, rate limited |
| `/api/v1/me/walls/{w}/mine` | did not exist | **device key only**, same handler as `/api/me/walls/{w}/mine`, rate limited |

`/api/me` stays cookie-only. The app uses the `/api/v1/me` aliases, because a key only authenticates on the API-key surface (`ApiKeySurface`).

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

## Rules

- **Who gets in.** The `/api/v1/me` write routes use the `DeviceApiKey` policy and answer any other caller 404. The sync routes use the `WallSync` policy and the `DenyApiKeyPrincipalsExceptDevices()` filter. Both admit a device key and refuse every other key with the same bare 404 as before: a plain personal key, a wall key, a kiosk key or an installation key. The `/api/v1/me` routes (walls, changes, mine) use the `DeviceApiKey` policy, so a browser cookie does not work there either.
- **What it sees.** The grant is unchanged. `FeedAccessGate`, `DocumentAudienceGate`, `AssetAccessGate` and `MineAccessGate` decide for the key's owner exactly as for that owner's cookie: the walls they belong to, their role's audiences (drafts only for setters), a share token as in the browser. A wall they cannot see answers 403 on the document and feed routes and 404 on the image and manifest routes.
- **Kiosk and visitor links.** These are unchanged. Both are cookie sessions, and a request authenticated by a key is never a kiosk.
- **Rate limits.** The feed's per-address token bucket covers the wall catch-up, the stream and the three `/api/v1/me` routes, whoever the caller is. As before, the document, image and manifest routes have no limiter for anyone.
- **Revocation.** A revoked key is refused on its next request. An open stream ends within its 5-minute lifetime, and the reconnect is refused.
- **Never open to a device key.** `/api/offline/*`, Blazor pages, the gallery, the panel and photo routes, 3D geometry, and the other `/api/me/*` routes.
