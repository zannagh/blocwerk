# Production deploy (IONOS box, blocwerk.app)

Production runs `docker/docker-compose.prod.yml` from `/home/patrickweindl/blocwerk` on the box, where
it is saved as `docker-compose.yml` next to its `.env` (template: `docker/prod.env.example`) and
`appsettings.json`. Nothing on the box is deployed by CI: the files in this repo are the source of
truth, and you copy them over by hand.

- `autodeploy.sh`: run by cron every minute (and at boot). Pulls the images and recreates
  - `wall-geometry` and `splat-cpu` when their image changed, but only after two consecutive idle polls
    of their authenticated `/v1/info` (no queued or running job); a broken probe counts as busy unless
    docker reports the container unhealthy; after 240 busy minutes it deploys anyway. Not-running
    services are left alone.
  - `blocwerk` behind the app's `/health/ready-to-deploy` gate (30 min cap) and a maintenance notice,
    exactly as before.
- `check-prod-compose.sh`: CI (`.github/workflows/prod-compose.yml`) renders the prod file with the
  example env and fails when a service or environment key in `docker-compose.yml` is missing from prod
  (unless listed there as intentional). Run it locally the same way.

## Install / update on the box

```sh
# from a checkout of the repo on your machine
scp docker/docker-compose.prod.yml ionos:/home/patrickweindl/blocwerk/docker-compose.yml.new
scp docker/prod/autodeploy.sh    ionos:/home/patrickweindl/blocwerk/autodeploy.sh.new

ssh ionos
cd /home/patrickweindl/blocwerk
cp docker-compose.yml docker-compose.yml.bak-$(date -u +%Y%m%dT%H%M%SZ)
cp autodeploy.sh autodeploy.sh.bak-$(date -u +%Y%m%dT%H%M%SZ)
diff docker-compose.yml docker-compose.yml.new     # expect comment-only changes unless you meant more
docker compose -f docker-compose.yml.new config -q # must print nothing
mv docker-compose.yml.new docker-compose.yml
chmod +x autodeploy.sh.new && mv autodeploy.sh.new autodeploy.sh   # atomic: cron may be mid-run
```

`.env` is never copied: add new variables to it by hand (names in `prod.env.example`). The crontab
stays as it is:

```
@reboot sleep 60 && /home/patrickweindl/blocwerk/autodeploy.sh >> /home/patrickweindl/blocwerk/autodeploy.log 2>&1
* * * * * /home/patrickweindl/blocwerk/autodeploy.sh >> /home/patrickweindl/blocwerk/autodeploy.log 2>&1
```

Watch the first runs with `tail -f autodeploy.log`.

## Renew the GHCR token before it expires

`GHCR_TOKEN` in the box's `.env` is a GitHub token with `read:packages` and it **expires**. When it does,
every pull fails (`pull failed even after re-authenticating`) and nothing deploys; this already happened
once mid-rollout. Note the expiry date when you create it and renew a week early:

1. GitHub, Settings, Developer settings, Personal access tokens: create a new one with `read:packages`
   (it must be able to read `blocwerk`, `blocwerk-geometry` and `blocwerk-splat-worker`).
2. On the box, edit `.env` and replace the `GHCR_TOKEN=` value (never paste it into a shell command).
3. `docker logout ghcr.io`; the next cron run logs in again with the new token. Check the log.
