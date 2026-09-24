# Signing in with a personal API key

Automation (Playwright, scripts) can open a browser session as a user without going through OAuth, by presenting one of that user's **personal** API keys. The feature is **off by default**. When it is off, the route is not mapped and answers 404 like any other unknown path.

## Enabling it

Two settings, and both are needed:

| Setting | appsettings.json | Environment | Short environment form |
| --- | --- | --- | --- |
| Switch | `Blocwerk:Auth:ApiKeyLogin:Enabled` = `true` | `BLOCWERK__AUTH__APIKEYLOGIN__ENABLED=true` | `AUTH__APIKEYLOGIN__ENABLED=true` |
| Allow-list | `Blocwerk:Auth:ApiKeyLogin:AllowedUserIds` = `["<user id>", …]` | `BLOCWERK__AUTH__APIKEYLOGIN__ALLOWEDUSERIDS__0=<user id>`, `__1`, … | `AUTH__APIKEYLOGIN__ALLOWEDUSERIDS=<id>,<id>` |

* **An empty allow-list means nobody**, even with the switch on. Only the users listed may sign in with one of their keys. An entry that isn't a user id stops the app from starting.
* **A listed user skips their second factor.** If they have TOTP switched on, a key login still works: listing them is that decision, so only list accounts whose keys you trust like a full login.
* As with every other setting, the JSON section wins over the short environment name.

## The endpoint

`POST /account/api-key-login`

* The key is read **only** from the `Authorization: Bearer bwk_…` header. A key in the query string or a form field is ignored, so it can't end up in URLs, proxy logs or browser history.
* On success it answers **204** with a session cookie. With `?redirect=1` it answers **302** to `returnUrl` if that is a local URL, and to `/walls` otherwise. An off-site `returnUrl` is never followed.
* Any failure answers **401** `{"error":"Invalid API key."}`. The response never says why; the server log does.
* It is rate limited to **10 attempts per minute per client IP**, successes included. Over the limit it answers **429**. See *Trusted proxies* below for what "client IP" means behind a reverse proxy.

### Which keys are accepted

Only a key created under *Settings → API keys* (scope `User`, no wall) that is neither revoked nor expired, whose owner is a live account on the allow-list. The endpoint refuses:

* wall, kiosk and installation keys,
* keys of a deleted account (its tombstone) and of the Ghost system user,
* owners who are currently locked out after failed password or TOTP attempts,
* any request from a registered kiosk tablet (the kiosk gate answers 403).

Every attempt is logged with the key's id and display prefix, never the key itself. The key's *last used* time only moves when a login actually succeeds.

### What the session can and can't do

A key session is a normal signed-in session for browsing and using walls, with these limits:

* **It ends with the key.** It lasts at most 8 hours, never longer than the key itself, and doesn't slide. About every 5 minutes the key is checked again: revoking it, letting it expire, deleting or locking the account, taking the user off the allow-list or switching the feature off ends the session on its next request.
* **No account-security changes.** Creating or revoking API keys, setting or changing the password, turning the second factor on or off, changing the e-mail address, linking or merging another login, and deleting the account all answer *"Not available in a session signed in with an API key."*
* **Open Blazor pages aren't cut off.** A page that is already open keeps its live connection until it's reloaded. The re-check applies to every new request and reload, not to a circuit that is already running.

## Playwright

`context.request` shares its cookie jar with the browser pages of the same context, so one call signs the whole context in:

```js
await context.request.post(`${base}/account/api-key-login`, {
  headers: { Authorization: `Bearer ${process.env.BLOCWERK_PERSONAL_API_KEY}` },
});
await page.goto(`${base}/walls`);
```

Keep the key in an environment variable or secret store, never in the test source.

## Personal keys on the wall-update and capture API

Separately from the login above, and not affected by its switch or allow-list, a personal key can call these routes directly with `Authorization: Bearer bwk_…`, **but only if it was created with "Allow this key to change walls (wall updates, captures)" ticked**:

* `/api/walls/{wallId}/update/shapes/*`. A wall key still works there for its own wall only.
* `POST /api/captures/{captureId}/video`. Only signed-in users and personal keys with write access are accepted.

In both cases the key's owner has to pass the same checks as in the browser: admin of the wall, not a kiosk, and for the video, their own open capture draft. Keys that existed before this option all start without write access. Wall keys can't be used on the capture route. Kiosk and installation keys can't be used on either.

## Trusted proxies

`Blocwerk:Server:TrustedProxies` lists the reverse proxies whose `X-Forwarded-*` headers are believed, as IP addresses or CIDR networks (env `BLOCWERK__SERVER__TRUSTEDPROXIES__0`, …, or `SERVER__TRUSTEDPROXIES=10.0.0.5,172.18.0.0/16`).

Empty (the default) keeps the old behaviour: any sender is trusted, which is only safe while the app can be reached solely through the proxy. With the proxy listed, the client IP used by the rate limit and the logs can't be spoofed even if the app's port is exposed. A malformed entry stops the app from starting.

## Development only: `/dev/login`

In the Development environment, `GET /dev/login` accepts the same `Authorization: Bearer bwk_…` header as an alternative to `?userId=`, `?email=` or `?wallId=`. It validates the key exactly as above, including the switch and the allow-list, and signs in the same limited key session. This route is never mapped outside Development.
