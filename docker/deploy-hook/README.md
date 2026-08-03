# deploy-hook

A tiny [`webhook`](https://github.com/adnanh/webhook) receiver that auto-deploys blocwerk when a
new image is published to GHCR. It verifies GitHub's HMAC signature, then runs
`docker compose pull blocwerk && docker compose up -d blocwerk` against the host docker daemon.

## Flow

```
GitHub release/package published
  └─ POST  https://blocwerk.zannagh.me/ci/released   (X-Hub-Signature-256, JSON body)
       └─ Caddy routes /ci/released → <host>:9000
            └─ deploy-hook container (this service, serving /ci/{id} via -urlprefix ci)
                 ├─ verify HMAC against $WEBHOOK_SECRET   → reject if it doesn't match
                 ├─ require payload.action == "published" → ignore pings / edits
                 └─ docker compose pull blocwerk && up -d blocwerk   (host daemon via socket)
```

The receiver serves hooks at `/ci/<id>` (`-urlprefix ci`); this hook's id is `released`, so the
path is **`/ci/released`** — matching the configured GitHub Payload URL.

## Setup (4 things only you can do)

1. **Secret.** `openssl rand -hex 32`, put it in `.env` as `WEBHOOK_SECRET=…`, and paste the
   **same** value into the webhook's *Secret* field on GitHub (repo → Settings → Webhooks).
2. **GHCR token.** The image is private and the host's login lives in the macOS keychain (unusable
   in a Linux container), so create a token with `read:packages` (github.com/settings/tokens) and
   set `GHCR_TOKEN=` in `.env`. *(Or make the package public and leave it empty.)*
3. **Host path.** Set `COMPOSE_DIR` in `.env` to the absolute host path of the stack
   (here: `/Volumes/RaidSSD/Services/blocwerk`).
4. **Route the path.** Caddy blanket-forwards `blocwerk.zannagh.me` to the app (`:5050`), so add a
   rule that sends the webhook path to this receiver (`:9000`) instead:

   ```caddy
   blocwerk.zannagh.me {
       handle /ci/released {
           reverse_proxy <host>:9000
       }
       handle {
           reverse_proxy <host>:5050
       }
   }
   ```
   Then `caddy reload`. The GitHub Payload URL stays `https://blocwerk.zannagh.me/ci/released`
   (content type `application/json`).

Then: `docker compose up -d --build deploy-hook`

## Verify

- `docker logs -f blocwerk-deploy-hook-1` — receiver + deploy output.
- GitHub → the webhook → **Recent Deliveries**: a green ✓ with body `deploy queued`. Use
  **Redeliver** to test a past event without a new push.

## Security

This container mounts `/var/run/docker.sock`, i.e. it is effectively root on the host. Its only
trigger is a request whose HMAC matches `WEBHOOK_SECRET`, so keep that secret strong and never
expose `/ci/released` without it. TLS terminates at Caddy.

## Notes

- The GHCR package is private, so the receiver runs `docker login ghcr.io` with `GHCR_TOKEN` before
  pulling (the host's keychain login can't be reused inside a Linux container).
- A release fires both a `release` and a `package` delivery; `run-deploy.sh` takes a `flock` so the
  two don't race. Pulling `:latest` when nothing changed is a no-op and won't recreate the container.
