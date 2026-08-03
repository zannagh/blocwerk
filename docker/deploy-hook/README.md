# deploy-hook

A tiny [`webhook`](https://github.com/adnanh/webhook) receiver that auto-deploys blocwerk when a
new image is published to GHCR. It verifies GitHub's HMAC signature, then runs
`docker compose pull blocwerk && docker compose up -d blocwerk` against the host docker daemon.

## Flow

```
GitHub release/package published
  └─ POST  https://<public-host>/hooks/deploy   (X-Hub-Signature-256, JSON body)
       └─ deploy-hook container (this service)
            ├─ verify HMAC against $WEBHOOK_SECRET   → reject if it doesn't match
            ├─ require payload.action == "published" → ignore pings / edits
            └─ docker compose pull blocwerk && up -d blocwerk   (host daemon via socket)
```

The receiver serves each hook at `/hooks/<id>`; this one's id is `deploy`, so the path is
`/hooks/deploy`.

## Setup (4 things only you can do)

1. **Secret.** `openssl rand -hex 32`, put it in `.env` as `WEBHOOK_SECRET=…`, and paste the
   **same** value into the webhook's *Secret* field on GitHub (repo → Settings → Webhooks).
2. **GHCR token.** The image is private and the host's login lives in the macOS keychain (unusable
   in a Linux container), so create a token with `read:packages`
   (github.com/settings/tokens) and set `GHCR_TOKEN=` in `.env`. *(Or make the package public and
   leave it empty.)*
3. **Host path.** Set `COMPOSE_DIR` in `.env` to the absolute host path of the stack
   (here: `/Volumes/RaidSSD/Services/blocwerk`).
4. **Public URL.** GitHub is on the internet and the host is on a LAN, so route a public URL to this
   container's port `9000` (via the same reverse proxy that fronts `blocwerk.zannagh.me`), and set
   the webhook's *Payload URL* to `https://<that-host>/hooks/deploy`, content type
   `application/json`.

Then: `docker compose up -d --build deploy-hook`

## Verify

- `docker logs -f blocwerk-deploy-hook-1` — receiver + deploy output.
- GitHub → the webhook → **Recent Deliveries**: a green ✓ with body `deploy queued`. Use
  **Redeliver** to test.

## Security

This container mounts `/var/run/docker.sock`, i.e. it is effectively root on the host. Its only
trigger is a request whose HMAC matches `WEBHOOK_SECRET`, so keep that secret strong and never
expose `/hooks/deploy` without it. Prefer terminating TLS at your reverse proxy.

## Notes

- The GHCR image is pulled with the host's docker login (mounted `~/.docker`). If the package is
  public you can drop that volume from `docker-compose.yml`.
- A release fires both a `release` and a `package` delivery; `run-deploy.sh` takes a `flock` so the
  two don't race. Pulling `:latest` when nothing changed is a no-op and won't recreate the container.
