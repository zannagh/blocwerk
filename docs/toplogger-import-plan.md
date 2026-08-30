# TopLogger Import — Implementation Plan

Import a user's TopLogger climbing **ticks** (sends / flashes / attempts, logged at external gyms) into
Blocwerk, cluster them into **Activities** exactly like native wall sends, and feed the activities page +
rolling boulder rating. Restarted 2026-08-30 on a **token + refresh-token GraphQL** auth model.

---

## 1. Background & key facts

- **Live TopLogger API is GraphQL** (`https://api.toplogger.nu/graphql`), introspection disabled. The old
  legacy REST API (`/v1/...`, email+password sign-in) is **dead** (returns empty 200s).
- The user has a **working C# POC** at `/Users/patrickweindl/Projects/toplogger-api-scaper`. Its
  `TopLogger.Api.Scraper.Core` project already implements the GraphQL client, token auth (access +
  refresh JWT), auto-refresh-on-401 (one retry), request pacing, and tick reading. **We vendor it.**
- A prior in-app attempt lives on the reverted branch `toplogger-integration`. ~Half is reusable
  (`ExternalAscent` entity, grade mapper, `ActivityGrouping` fix, import/cluster loop); the network/auth
  layer and its AES-GCM `TokenProtector` are discarded.

## 2. Auth model

- User pastes **access token + refresh token** into a Profile "TopLogger Import" section (grabbed from
  app.toplogger.nu devtools — POC's `ConnectPanel.razor` shows the devtools fetch-hook trick we can reuse
  as copy).
- Access token → `Authorization: Bearer` on GraphQL calls. On 401/`UNAUTHENTICATED`, refresh via
  `authSigninRefreshToken($refreshToken: JWT!)` (refresh token itself sent as Bearer), retry once, and
  **rotate** the stored refresh token if a new one comes back.
- On refresh failure (stale/rotated-out refresh token) → mark connection `NeedsReauth`, surface the
  **"Reset TopLogger Tokens"** toast.

## 3. Encryption & the password gate  ⚠️ design decision

- **Tokens are encrypted at rest with the app's ASP.NET DataProtection key ring**
  (`CreateProtector("blocwerk.toplogger")`), mirroring how TOTP secrets are stored today. NOT the old
  `TokenProtector`/`BLOCWERK__ENCRYPTIONKEY` (absent on current main).
- **The user's password CANNOT be the encryption key**: the 3-hourly background sync must decrypt tokens
  with no user present, so a server-held key is mandatory. Therefore **"user must have set a password" is
  an account-security precondition (gate), not the cryptographic source.** Enforced via existing
  `User.HasPassword`. → *Confirm this framing is acceptable.*

## 4. Data model

New entities in `Blocwerk.Core`:

- **`TopLoggerConnection`** (1 per user; unique on `UserId`):
  `AccessTokenProtected`, `RefreshTokenProtected`, `AccessExpiresAt`, `RefreshExpiresAt`, `TopLoggerUserId`,
  `LastSyncAt`, `LastError`, `NeedsReauth` (bool), `CreatedAt`. Creatable only when `User.HasPassword`.
- **`ExternalGym`** (global, one row per real gym; unique on `(Source, ExternalId)`): `Name`, `Slug`,
  `CreatedAt`. **Lazily created** the first time any user ticks there. Chosen over a fake `Wall` row so we
  don't pollute the heavy, per-user-filtered `Wall` table / big-wall logic / wall pickers.
- **`ExternalAscent`** (ported + extended; dedupe unique `(UserId, Source, ExternalId)`):
  `ClimbName`, `ExternalGymId` (FK), `LoggedAt` (from `climbedAtDate`), `Type` (Flash/Send/Attempt),
  `Points`, `RawGrade` (scaled int/string as returned), `MappedGrade` (Font, nullable),
  `NeedsGradeMapping` (bool), `ActivityId` (FK, SetNull).
- **`UserGradeMapping`** (per user): `RawGradeKey` (points/level or raw grade token) → `FontGrade`. Lets a
  one-time resolution apply to all matching unmapped ascents.

Changes to existing:

- **`Activity`**: add nullable `ExternalGymId` FK (in practice mutually exclusive with `WallId`).
  `DurationMinutes` override already exists — reused for imported sessions.
- **`ActivityGrouping`**: reuse the branch's `db.Activities.Local` batch-matching fix so a sync that adds
  many activities before `SaveChanges` still clusters.

## 5. Import & clustering flow (per sync)

1. Fetch ticks page-by-page via vendored Core (`climbLogs`/`climbDaysPaginated`).
2. For each tick, dedupe by `(UserId, TopLogger, tick.id)` against existing + in-batch set.
3. Lazily upsert `ExternalGym` for `tick.gymId`.
4. Grade: run scaled-int → Font mapping (POC `GradeFormatter`, cross-checked vs branch `TopLoggerGradeMapper`).
   Success → `MappedGrade` set (feeds `GradeScoring`). Fail (V_SCALE/unknown) → `NeedsGradeMapping=true`,
   keep `Points`/`RawGrade` for display.
5. `ResolveActivityIdAsync(userId, tick.climbedAtDate, wallId:null)` → cluster into an Activity; set the
   Activity's `ExternalGymId`. (4h-gap / per-UTC-day rule, same as native.)
6. Set `LastSyncAt`; on auth error set `NeedsReauth`+`LastError`.

## 6. Grade resolution UX

- Import never blocks on grade (user decision). Unmapped ascents show **level/points**.
- A prompt ("N sends need a grade") on Profile + Activity view → user maps a level/points value once via
  `GradePicker` (Font/V) → stored in `UserGradeMapping` → re-applied to all matching ascents + re-scored.

## 7. Session duration

- Derived `LastEventAt − StartedAt` (existing). Imported sessions can be adjusted via the existing
  `UpdateActivityDurationAsync` + `ActivityView.razor` duration editor — verify it renders for
  external-gym activities (WallId null) and wire if gated.

## 8. Background sync service

- `TopLoggerSyncService : BackgroundService`, `IDbContextFactory` + per-tick `db.CurrentUserId`,
  `PeriodicTimer` (check ~every 15–30 min). For each connection: if **within the user's local 08:00–22:00**
  window AND `LastSyncAt` > 3h ago → sync. Registered via `AddHostedService` in `CoreServices.cs`.
- **User timezone: RESOLVED — must be persisted (net-new).** The app localizes time 100% in the browser
  (`localtime.js` via `Intl.DateTimeFormat`); nothing zone-related reaches the server, and `User` has no
  TZ field. So Phase 3 must: add nullable `User.TimeZoneId` (IANA, e.g. `Europe/Berlin`; DST-correct via
  `TimeZoneInfo.FindSystemTimeZoneById`), capture it client-side once
  (`Intl.DateTimeFormat().resolvedOptions().timeZone`) and push to the server (small JS-interop/endpoint —
  does not exist yet). Fallback when null: use a configured default zone (e.g. Europe/Berlin) or sync in a
  server-day window.

## 9. Session-start sync

- On login / first authenticated Blazor circuit of a browser session, fire-and-forget a sync if
  `LastSyncAt` > ~1h. Hook in `CurrentUserService` / a layout `OnInitializedAsync`.

## 10. Reauth toast

- `NeedsReauth` flag on the connection. A layout-level check shows the **"Reset TopLogger Tokens"** toast
  (per-component `_toast` pattern; no global toast service exists) with a link to /profile, where the
  section shows a reconnect (re-paste tokens) form.

## 11. ⚠️ Deploy hazard — DB reconciliation

Prod Postgres still contains **stale, empty `ExternalAscents` / `TopLoggerConnections` tables** from the
old migration `20260807200833_AddTopLoggerIntegration` (its `__EFMigrationsHistory` row is present; the
migration is NOT in the current code's history). Our new migration `20260830102901_AddTopLoggerImport`
`CREATE TABLE`s those names fresh → on prod deploy `db.Database.Migrate()` (CoreServices.cs:94) will fail
("table already exists"). **The old tables also have the WRONG shape** (old `TopLoggerConnection` had
Email/Backend/TokenEncrypted; new has AccessTokenProtected/RefreshTokenProtected) so they can't be reused.

**⚠️ FOOTGUN CONFIRMED:** `src/Blocwerk.Web/appsettings.json` hard-codes `Postgres.Host = 192.168.178.19`
(**prod**). The local dev DB (`127.0.0.1:5051`) exists only via the machine-specific launchSettings
override. So a bare `dotnet ef database update` / any un-overridden EF command hits PROD. Always prefix EF
commands with local env overrides. Migration was *generated* with those overrides as a safety belt
(`migrations add` does not connect, but startup migrates on host-build).

**Pre-deploy reconciliation (run against prod ONCE, after VERIFYING the tables are still empty):**
```sql
-- 1. verify empty first:  SELECT count(*) FROM "ExternalAscents"; SELECT count(*) FROM "TopLoggerConnections";
DROP TABLE IF EXISTS "ExternalAscents";
DROP TABLE IF EXISTS "TopLoggerConnections";
DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260807200833_AddTopLoggerIntegration';
```
Then deploy — `Migrate()` applies `20260830102901_AddTopLoggerImport` cleanly (creates ExternalGyms,
TopLoggerConnections, UserGradeMappings, ExternalAscents + the Activity.ExternalGymId column). NOT executed
against prod yet — gated on user sign-off.

**✅ VALIDATED LOCALLY (2026-08-30):** pulled the prod DB (49 MB) into a local `postgres:17` container,
confirmed both stale tables were EMPTY (0 rows) and the old history row present, ran the three SQL
statements above, then started the app — `Migrate()` applied `20260830102901_AddTopLoggerImport` cleanly
and all four new tables exist. The prod procedure is the same SQL; just re-verify emptiness on prod first.

## 12. Phasing

- **Phase 1 — Connect + manual import.** Vendor POC Core; `TopLoggerConnection` + encrypted store;
  Profile section (paste tokens, password-gated) + "Sync now"; `ExternalAscent` import, `ExternalGym`
  lazy-create, activity clustering. **Validate live with the user's real tokens.**
- **Phase 2 — Grade resolution.** `UserGradeMapping` + bulk-resolve UI + re-score.
- **Phase 3 — Automation.** Background 3h/daytime sync (+ persist user TZ if needed) + session-start sync
  + reauth toast.
- **Phase 4 — Polish.** Duration-adjust wiring for imported activities, activity/gym display, tests.

## 15. LIVE VALIDATION RESULT (2026-08-30 pm) — ✅ works, with follow-ups

First real sync (user zannagh, gym "Blockhaus") after fixing a crash: **2248 ascents, 319 sessions,
1 gym, 2239/2248 grades mapped** (the 9 unmapped are RawGrade "0" = ungraded in TopLogger). Grade
mapping + gym lazy-create + clustering all correct.

**Bug fixed during validation:** import crashed post-fetch — TopLogger tick timestamps carry a local
(+02:00) offset, Npgsql timestamptz is UTC-only → normalize to UTC (done in TopLoggerImportHelpers +
service); AND the import phase ran outside try/catch so it failed silently and lost the whole pull →
wrapped + records LastError on a fresh context (done).

**Open TopLogger follow-ups (queued):**
- [ ] **TL-classification bug**: everything imported as `Send`. `AttemptType` is `Attempt=0/Send=1/Flash=2`
      but mapping is only `isFlash ? Flash : Send` → (a) 468 not-topped ticks (`Ticked=false`) mislabeled
      Send, should be **Attempt**; (b) 0 flashes across 2248 → `tickType` isn't the literal `"flash"` we
      test. Fix: `!Ticked/!Topped → Attempt`; determine real flash signal (check POC api-samples for raw
      `tickType`/`tryIndex` values — flash likely = topped on first try).
- [ ] **Skip-if-nothing-new** (user request): before a re-sync, one cheap query for the newest climb-day;
      if not newer than `LastSyncAt`, return early (0 fetches). Keeps TopLogger traffic tiny. Applies to
      both manual "Sync now" and the future background sync.
- [ ] **Timestamps are DATE-ONLY** (all midnight local) → every imported session has duration 0; auto
      duration inference impossible for TopLogger. Manual duration adjust is the fallback (already planned).
      Investigate whether any finer per-ascent time field exists; otherwise document the limitation.
- [ ] **Resilience**: batched SaveChanges during import (progress persists, count grows live) + background
      the initial full sync (user floated this; strongly indicated by the ~4–10 min pull).
- [x] **Activities + rating (#1) DONE**: imported sessions render on the activities list + detail (gym
      label, per-ascent grade/result badges), duration editor works on them, and imported sends feed the
      rolling 60-day rating via the SAME `GradeScoring`. Deduped by **`ClimbId`** (TopLogger climbs are
      usually UNNAMED — "Unknown climb" — so name-based dedup was wrong; captured `climbId` end-to-end +
      new migration `AddExternalAscentClimbId`; unnamed climbs labeled by grade in the UI). Validated:
      769 recent ascents → 600 distinct climbs, 0 null climbid.
- [x] **PACING LESSON**: 500ms test override got TopLogger **429 ThrottlerException** → import silently
      TRUNCATED (769/2248) but reported success. Default **1500ms** is required; do NOT override low. #5
      is NOT a non-issue.
- [ ] **THROTTLE ROBUSTNESS (in progress)**: add 429 backoff-retry in the GraphQL client + make a
      persistent throttle/error PROPAGATE (fail the sync) instead of silent partial success.
- [ ] Restore pacing to default before ship (stop passing `TopLogger__MinRequestInterval` env override).
- [ ] (still) Wire imported ascents onto activities page + rating; grade-resolution UI for the unmapped.

## 16. Separate account/profile batch (user asked to fold in, 2026-08-30 pm) — NOT TopLogger
**STATUS: built + merge fix VALIDATED on local prod-mirror.** Startup reconciliation
(`LegacyIdentityReconciliation`, called in `ConfigureCoreApplication` after Migrate) merged exactly Patrick
W + Patrick Weindl → zannagh; The Attic 5→4 members; Jana/O O correctly untouched (self-owned identity);
2248 ascents intact. Link path now legacy-aware (`LegacyIdentityResolver` shared by login+link). TL
classification (Attempt/Send/Flash) + skip-if-nothing-new also built. Full solution builds 0 errors.
Pending: user visual check of profile UI + TL re-sync to validate classification (flash heuristic
`TryIndex==1` unverified on live data) + skip-check. **#4a caveat: legacy account's original provider is
NOT stored anywhere, so a provider used only on a legacy account (zannagh's GitHub) still shows as
"Link"-able until the user logs in/links via it (then it registers). Not fully fixable without re-auth.**

Delegated agents implemented (profile UI all in `Profile.razor`):
- Avatar: pencil-edit + trash-delete icons (drop file-picker/remove-button); no 4MB limit, server-side
  downscale to 512px.
- Password sign-in + TOTP: collapsible cards, "(all set)" when configured.
- Linked accounts: omit already-linked provider from link options; default-collapsed card.
- Home wall: move to first section under profile name.
- API Keys link: style to match app.
- **BUG (diagnosis running):** account merge left duplicate wall members — user's two accounts show as 2 of
  5 members; expected fold into one. Merge/link may not re-point + dedupe wall memberships (composite PK).

## 14. Build progress (branch `feat/toplogger-import`)

- [x] **Branch** `feat/toplogger-import` off main.
- [x] **Vendored client** → `src/Blocwerk.Core/Services/TopLogger/` (auth+refresh, GraphQL client, pacing,
      tick reader, grade formatter). Interfaces: `ITopLoggerApiClient.GetTicksAsync(userId, since, ct)`,
      `ITopLoggerTokenStore` (Load/Save/Clear by Guid), `TopLoggerTick` DTO, `TopLoggerAuthException`
      (=needs-reauth signal). Added `Microsoft.Extensions.Http` ref.
- [x] **Data model** → entities `TopLoggerConnection`, `ExternalGym` (global, lazy), `ExternalAscent`
      (dedupe (UserId,Source,ExternalId)), `UserGradeMapping`; `Activity.ExternalGymId`; DbContext config;
      `ActivityGrouping` Local batch-match fix ported. **Caller must set `Activity.ExternalGymId` after
      `ResolveActivityIdAsync` (returns only the id).**
- [x] **Compiles clean** (Blocwerk.Core, 0 errors; new code warning-clean).
- [x] **Backend wiring**: DataProtection token store `blocwerk.toplogger` (in Blocwerk.Authentication,
      reviewed by lead — encryption/password-gate/reauth correct; `LoadAsync` now catches
      `CryptographicException`), import service (Connect/Sync/Disconnect/Status), DI in CoreServices +
      AuthenticationServices.
- [x] **Profile UI**: password-gated "TopLogger Import" section in Profile.razor (paste access+refresh
      token, Sync now, status w/ ascent + unmapped counts, Disconnect+delete option, "Reset TopLogger
      Tokens" reauth banner/toast + reconnect). `.tl-*` CSS in pages.css.
- [x] **Migration** `20260830102901_AddTopLoggerImport` generated (safe env-guarded). Applies to LOCAL dev
      DB automatically on next `dotnet run` (startup Migrate). **Prod deploy still gated — §11 reconciliation.**
- [x] **Full solution builds, 0 errors.** Phase 1 CODE-COMPLETE.
- [ ] **LIVE VALIDATION** with the user's real tokens (NEXT — user action) — the vendored client has
      unverified guesses (TL-user-id query, `climbedAtDate` arg format, pagination, grade cutoff, tokens
      lacking expiries so first call relies on refresh). Validate the fetch works end-to-end BEFORE building
      the later phases.

### Review follow-ups (from backend code review — non-blocking, address before ship)
- [x] `LoadAsync` now catches `CryptographicException` (lost/rotated key ring) → returns null → clean
      reconnect prompt instead of a crash.
- [ ] Incremental `since` uses sync wall-clock vs. tick `climbedAtDate` (different clocks) — a backfilled
      or late tick dated before the last sync could be missed. Add a lookback window or periodic full
      resync.
- [ ] `DisconnectAsync(deleteImportedAscents:true)` leaves orphan `Activity` rows (ExternalAscent.ActivityId
      is SetNull) — empty external-gym activities linger. Clean up when wiring activities display.

### ALL 5 REMAINING ITEMS DONE 2026-08-30 pm (uncommitted working tree; builds 0 errors)
- [x] #1 activities+rating+duration (climbId dedup).
- [x] #2 grade-resolution UI (map unmapped RawGrade → Font, ExecuteUpdate retro-apply).
- [x] #3 DESCOPED by user (rate-limiting fear): DROPPED the background service + the whole per-user
      timezone/daytime-window (no migration). KEPT only session-start sync (MainLayout
      OnAfterRenderAsync firstRender, once/circuit, fire-and-forget via IServiceScopeFactory+Task.Run, only
      if connected & !NeedsReauth & LastSyncAt>1h) + app-wide "Reset TopLogger Tokens" toast.
- [x] #4 duration adjust — folded into #1 (existing editor works on imported activities).
- [x] #5 pacing — default 1500ms is correct; the 500ms test override caused the 429s.
- [x] Throttle handling: 429 detect + exp backoff (Retry-After honored) + persistent throttle/error now
      PROPAGATES (fails the sync) instead of silent partial import.
- [x] Flash = explicit tickType (flash/onsight) OR points > base-grade (score-system bonus). Dropped the
      TryIndex first-try heuristic (over-counted).
- **RATE-LIMITED (user's TopLogger account) from repeated test syncs — 429 ThrottlerException, NOT a ban.
  PAUSED all syncing. App held DOWN so session-start sync can't auto-full-pull (data is reset, LastSyncAt
  null → next app-open would full-sync). Bring app up only after cooldown + user ready. Then user does one
  clean full sync.**
- Nothing committed since the merge (`8ed4742`); offer to checkpoint once user is happy.

### Dropped/never-needed phases
- [ ] Surface imported ascents on the activities page + feed the rolling rating (ProgressionService /
      ActivityView read `ExternalAscent` by ActivityId, not just `Attempt`) — **essential to the ask,
      deferred until the fetch is proven**.
- [ ] Grade-resolution UI ("N sends need a grade" → map level/points → Font/V, re-score) — Phase 2.
- [ ] Background sync (3h / 08:00–22:00 user-local; needs persisted `User.TimeZoneId`) + session-start
      sync + layout-level reauth toast — Phase 3.
- [ ] Duration-adjust wiring for imported activities (reuse `ActivityView` editor) — Phase 4.

## 13. Decisions (locked 2026-08-30)

1. **Encryption (§3): LOCKED** — tokens encrypted with the server DataProtection key; `User.HasPassword`
   is the account-security gate. (Password-derived encryption rejected — incompatible with unattended sync.)
2. **Vendoring: LOCKED** — copy & adapt the POC `TopLogger.Api.Scraper.Core` into
   `src/Blocwerk.Core/Services/TopLogger`, swapping token persistence for the encrypted DB store. No
   cross-repo reference.
3. **Gym location (§4): LOCKED** — dedicated lightweight `ExternalGym` entity, lazily created; not a `Wall`.
4. **Grades (§6): LOCKED** — never block import; "N sends need a grade" bulk-resolve.
5. **Sync window (§8): LOCKED** — every 3h within 08:00–22:00 user-local (needs persisted `User.TimeZoneId`).
6. **Session-start sync (§9):** app login / new authenticated circuit, fire-and-forget if last sync >1h
   (default reading; flag if a *climbing* session was meant).
