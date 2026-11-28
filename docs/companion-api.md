# Companion app: what a device key may read

The iPhone app links to an account through `/device-link` and gets a **device key**: a personal `bwk_` key marked as a device (`ApiKeys.IsDevice`, claim `blocwerk:apikey_device`). It is listed as "iPhone: <device>" under *Settings → API keys* and can be revoked there.

With that key the app reads exactly what the offline web client (`/v/`) reads, as the key's owner. A personal key made by hand on the settings page still cannot read any of it.

## Routes

Send `Authorization: Bearer bwk_…`. No cookie and no antiforgery token. All routes are `GET` (the image and document routes take `HEAD` too).

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

## Rules

- **Who gets in.** The sync routes use the `WallSync` policy and the `DenyApiKeyPrincipalsExceptDevices()` filter. Both admit a device key and refuse every other key with the same bare 404 as before: a plain personal key, a wall key, a kiosk key or an installation key. The `/api/v1/me` routes (walls, changes, mine) use the `DeviceApiKey` policy, so a browser cookie does not work there either.
- **What it sees.** The grant is unchanged. `FeedAccessGate`, `DocumentAudienceGate`, `AssetAccessGate` and `MineAccessGate` decide for the key's owner exactly as for that owner's cookie: the walls they belong to, their role's audiences (drafts only for setters), a share token as in the browser. A wall they cannot see answers 403 on the document and feed routes and 404 on the image and manifest routes.
- **Kiosk and visitor links.** These are unchanged. Both are cookie sessions, and a request authenticated by a key is never a kiosk.
- **Rate limits.** The feed's per-address token bucket covers the wall catch-up, the stream and the three `/api/v1/me` routes, whoever the caller is. As before, the document, image and manifest routes have no limiter for anyone.
- **Revocation.** A revoked key is refused on its next request. An open stream ends within its 5-minute lifetime, and the reconnect is refused.
- **Never open to a device key.** `/api/offline/*`, Blazor pages, the gallery, the panel and photo routes, 3D geometry, and the other `/api/me/*` routes.
