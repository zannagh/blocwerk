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
| OAuth (`GITHUB__*`, `GOOGLE__*`, `MICROSOFT__*`) | see [OAuth sign-in](#oauth-sign-in-on-staging) |
| Admins (`Blocwerk:AdminIdentifiers`) | OAuth admins by their production identifier `{name}__{subject}` (the check compares the sign-in claim, not the stored value) |

## Signing in on staging

Password hashes are kept: use the production password. Users whose login name was an e-mail address now log in as
`user-<id>@staging.invalid`; other login names are unchanged. OAuth users sign in with their provider as on production
(next section) and land on their own account. API keys, kiosk registrations, TOTP, TopLogger links, share links and runner keys are gone and
must be created again.

## OAuth sign-in on staging

Providers: GitHub, Google and Microsoft, each enabled by its own settings. An OAuth login resolves the account by the
provider's stable subject: `UserIdentities (Provider, ProviderUserId)`, else, for accounts that never got an identity
row, the `__<subject>` suffix of `Users.Identifier`. The script keeps both: `UserIdentities` is untouched (it holds only
provider, subject and creation time; the app stores no provider tokens, e-mail or avatar URL there), and `Identifier`
becomes `staging__<subject>`, dropping the provider-name part. Google and Microsoft issue subjects per client / app
registration, so staging must use the same client as production to receive the same subjects:

| Provider | Setting names (env, or `Blocwerk:<Name>OAuth:<Key>`) |
| --- | --- |
| GitHub | `GITHUB__ENABLED=true`, `GITHUB__CLIENTID`, `GITHUB__CLIENTSECRET` |
| Google | `GOOGLE__ENABLED=true`, `GOOGLE__CLIENTID`, `GOOGLE__CLIENTSECRET` |
| Microsoft | `MICROSOFT__ENABLED=true`, `MICROSOFT__CLIENTID`, `MICROSOFT__CLIENTSECRET` |

(`*__OAUTHURL` overrides the authorize URL; the defaults are right.) Register this **redirect URI** at each provider:

```
https://experimental.blocwerk.app/oauth-callback
```

It is built from the request host (`{scheme}://{host}/oauth-callback`), so the reverse proxy must forward the public host
and scheme (the app's trusted-proxy setting). `/account/callback` is internal and is not registered at a provider.

**Recommended:** add the staging redirect URI to the **existing production OAuth apps** (GitHub and Microsoft allow
several; Google lists several authorised redirect URIs) and reuse their client id and secret as staging secrets. A
separate staging app is only an option for GitHub (its user ids are global); a separate Google client or Microsoft app
registration issues different subjects, so nobody would land on their own account (they would get a new, empty one). Whichever you pick, the secret lives only in the staging secret store.

The anonymise is tested against the real schema (`StagingAnonymiseTests`, runs where `BLOCWERK_TEST_PG` is set).
