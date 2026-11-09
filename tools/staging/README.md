# Staging copy (experimental.blocwerk.app)

An anonymised copy of production, to try releases against real data. `anonymise.sql` strips personal data and
everything that grants access; the app's `Blocwerk:Staging` flag keeps the copy visibly marked, out of search engines
and silent.

## Order

1. **Restore** a `pg_dump` of production into a separate database in its own namespace (photos are `bytea` and come along).
2. **Anonymise**, while no app is running:

   ```sh
   psql -v ON_ERROR_STOP=1 -f tools/staging/anonymise.sql "host=… dbname=… user=…"
   ```

   One transaction, safe to run again. It ends with a table of counts: every cleared table must show `0`.
3. **Start the app.** Its migrations run on start. The script names tables and columns of the current model, so the
   dump must come from a release that has them all (`ApiKeys`, `GpuRunners`, `TopLoggerConnections`, kiosk PIN
   columns, …); production is normally current. If the dump is older, start the app once to migrate, stop it, then anonymise
   and start it again, and keep it unreachable until the script has run.

## What infra configures (own values, never production's)

| Setting | Value |
| --- | --- |
| `BLOCWERK__STAGING` | `true` (banner, `noindex`, no e-mail, no push) |
| Postgres (`POSTGRES__*`) | the staging database only |
| DataProtection key ring | its own, fresh (ends every restored cookie session; TOTP secrets are gone anyway) |
| `JWT__KEY` | its own |
| `VAPID__*` | its own keys or unset (push is suppressed on staging regardless) |
| `SMTP__*` | the Mailpit catcher or unset (mail is suppressed on staging regardless, so Mailpit stays empty) |
| `RECOGNITION__INSTANCEKEY` | its own; `RECOGNITION__MODE=queue`; `RUNNERS__MODE` off |
| OAuth client ids/secrets | unset, or staging-only apps (`UserIdentities` are deleted) |
| Admins (`Blocwerk:AdminIdentifiers`) | by the new identifiers `staging__<user id>` |

## Signing in on staging

Password hashes are kept: use the production password. Users whose login name was an e-mail address now log in as
`user-<id>@staging.invalid`; other login names are unchanged. OAuth-only users have no password and cannot sign in
(an admin can set one). API keys, kiosk registrations, TOTP, TopLogger links, share links and runner keys are gone and
must be created again.

The anonymise is tested against the real schema (`StagingAnonymiseTests`, runs where `BLOCWERK_TEST_PG` is set).
