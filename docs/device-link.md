# Linking the companion app (device link)

The Blocwerk iPhone app signs in by linking itself to an account: the user confirms in the browser, and the app gets
a personal API key it keeps in the Keychain. It uses OAuth's authorization-code flow with PKCE, cut down to one client.

## Flow

1. The app opens a browser sheet (`ASWebAuthenticationSession`) on

   ```
   GET /device-link?state=<s>&code_challenge=<c>&code_challenge_method=S256&device=<name>&redirect_uri=blocwerk-companion://auth
   ```

   * `redirect_uri` must be exactly `blocwerk-companion://auth`. Anything else, a `plain` challenge, a challenge that
     is not 43 URL-safe base64 characters or a `state` outside 16 to 128 of them shows an error page. The server never
     redirects such a request anywhere.
   * `device` is shown on the page and names the key. It is cleaned (control characters dropped) and cut to 64.
   * Signed out, the normal sign-in runs first (any provider, the second factor included) and comes back to the page.
2. The page asks *Link this iPhone (<name>) to your Blocwerk account?*. **Link** posts the form (antiforgery, to `/device-link/confirm`) and
   answers `302 blocwerk-companion://auth?code=<code>&state=<s>`. **Cancel** answers
   `302 blocwerk-companion://auth?error=access_denied&state=<s>`.
   * The code is 32 random bytes, lives **60 seconds**, works **once**, and is stored only as its SHA-256, bound to the
     user, the challenge and the device name.
   * A session that was itself signed in with an API key cannot link a device, and a kiosk tablet never reaches the page.
3. The app exchanges the code:

   ```
   POST /api/device-link/token
   {"code":"…","code_verifier":"…","device":"<same name>"}
   ```

   * `200 {"token":"bwk_…","keyId":"…","name":"iPhone: <name>"}`. The token is in this answer only.
   * `400 {"error":"invalid_grant"}` for an unknown, expired or used code, a verifier that does not match the challenge,
     or another device name. The first attempt uses the code whatever the outcome, so a wrong verifier burns it.
   * Rate limited to **10 attempts a minute per client address** (429).

## The key

A personal key (scope `User`) **with write access**, because the app records wall captures, which need it. It is marked
as a device key (`ApiKeys.IsDevice`, claim `blocwerk:apikey_device`, `ClaimsPrincipal.IsDeviceKey()`), so a route can
admit linked devices without admitting every personal key. Otherwise it behaves exactly like a personal key made under
*Settings → API keys*, where it is listed as *Linked device* and can be revoked.

## For the app

All with `Authorization: Bearer bwk_…`:

* `GET /api/v1/me/account`: `{"id","displayName","email","avatarUrl","keyName"}`; `avatarUrl` is null without an avatar.
* `GET /api/v1/me/avatar`: the avatar bytes, 404 without one.
* `DELETE /api/v1/me/api-key`: revokes the key the request came with (the app's sign out). 204.

A 401 on any of them means the key is revoked or expired: the app signs out.
