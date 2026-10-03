# Production deploy (blocwerk.app)

Production runs `docker/docker-compose.prod.yml` from `/home/patrickweindl/blocwerk` on the box, where
it is saved as `docker-compose.yml` next to its `.env` (template: `docker/prod.env.example`) and
`appsettings.json`. Nothing on the box is deployed by CI: the files in this repo are the source of
truth, and you copy them over by hand.

- `autodeploy.sh`: run by cron every minute (and at boot). First the app, exactly as before: `blocwerk`
  is recreated behind the app's `/health/ready-to-deploy` gate (30 min cap) and a maintenance notice.
  Then, from an EXIT trap so it never delays the app, the compute services: `wall-geometry` and
  `splat-cpu` are recreated when their image changed, but only after two consecutive idle polls of
  their authenticated `/v1/info` (no queued or running job). A failed probe counts as busy unless docker
  reports the container unhealthy; a busy service is never overridden. After 240 busy polls it deploys
  anyway (job counts only, no start times, so this can cut a job that just started; see the script).
  Services that are not running are left alone. Exec/pull/up are bounded by `timeout`, one compute pass
  runs at a time (`flock`), and persistent failures (pull, lock) are logged once, not every minute.
- `check-prod-compose.sh`: CI (`.github/workflows/prod-compose.yml`) renders the prod file with the
  example env and fails when shared services differ in image, environment keys, mount targets,
  published ports or healthcheck presence, when a base service is missing from prod, or when prod
  publishes anything beyond 127.0.0.1, unless the difference is listed there as intentional.
  Run it locally the same way.

## Install / update on the box

Before installing, check that the box's `.env` has the keys this file needs (names only, no values):

```sh
ssh ionos 'cd /home/patrickweindl/blocwerk && for k in COMPOSE_PROFILES POSTGRES_HOST_PORT OTEL_UI_HOST_PORT OTEL_OTLP_HOST_PORT; do grep -q "^$k=" .env && echo "$k present" || echo "$k MISSING"; done; grep -c "^COMPOSE_PROFILES=.*compute" .env'
```

`COMPOSE_PROFILES` must include `compute` (the last line prints 1), or the compute services are not
started and autodeploy leaves them alone. The three `*_HOST_PORT` keys are new: set them in `.env` to the
loopback ports in the box's current `docker-compose.yml` before swapping the file in, otherwise
`config` fails on them.

```sh
# from a checkout of the repo on your machine
scp docker/docker-compose.prod.yml ionos:/home/patrickweindl/blocwerk/docker-compose.yml.new
scp docker/prod/autodeploy.sh    ionos:/home/patrickweindl/blocwerk/autodeploy.sh.new

ssh ionos
cd /home/patrickweindl/blocwerk
cp docker-compose.yml docker-compose.yml.bak-$(date -u +%Y%m%dT%H%M%SZ)
cp autodeploy.sh autodeploy.sh.bak-$(date -u +%Y%m%dT%H%M%SZ)
diff docker-compose.yml docker-compose.yml.new     # expect comment changes and the three port variables
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
