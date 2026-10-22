# Production deploy (blocwerk.app)

Production runs `docker/docker-compose.prod.yml` from `/home/patrickweindl/blocwerk` on the box, where
it is saved as `docker-compose.yml` next to its `.env` (template: `docker/prod.env.example`) and
`appsettings.json`. Nothing on the box is deployed by CI: the files in this repo are the source of
truth, and you copy them over by hand.

- `autodeploy.sh`: run by cron every minute (and at boot). First the app, exactly as before: `blocwerk`
  is recreated behind the app's `/health/ready-to-deploy` gate (30 min cap) and a maintenance notice.
  Past the cap it keeps waiting while `/health/busy-eta` (seconds; 0 when none) reports an end: long
  background work that said when it ends (a wall enhancement's hold search, capped at 6 h by the app),
  or an editor whose user is present (real input within 30 min), which reports its lease's cutoff: the
  first midnight Europe/Berlin after THAT lease started. Every lease open at midnight lets go then; a lease
  opened after midnight can hold until the following one, so a deploy pending across midnight can still
  wait for an editor who started after it. Safety cap 1440 polls (24 h). Right before `compose up` (after
  the announcement's grace) it calls `POST /api/v1/maintenance/flush-edits` (same `BLOCWERK_DEPLOY_API_KEY`)
  so open editors save or keep their unsaved state; a 404 from an older app is logged and the deploy goes ahead.
  The app part runs under its own `flock -n` (`.autodeploy-app.lock`): a run that overlaps a slower one
  (announce grace plus a flush can outlast the cron minute) exits quietly and leaves the deploy to it.
  `test-autodeploy.sh` is a dry-run harness for these decisions (fake docker, no host access).
  **The live copy on the IONOS box is separate**: until `autodeploy.sh` is reinstalled there (see
  "Install / update on the box" or `rollout.sh`), the box keeps the plain 30 min cap. The search
  checkpoints per photo, so a deploy that still cuts it costs only the photo in hand.
  Then, from an EXIT trap so it never delays the app, the compute services: `wall-geometry` and
  `splat-cpu` are recreated when their image changed, but only after two consecutive idle polls of
  their authenticated `/v1/info` (no queued or running job). A failed probe counts as busy unless docker
  reports the container unhealthy; a busy service is never overridden. After 240 busy polls it deploys
  anyway (job counts only, no start times, so this can cut a job that just started; see the script).
  Services that are not running are left alone. From the same trap, before the compute pass, it resets `pg_stat_statements` every
  48 h (see Diagnostics). Exec/pull/up are bounded by `timeout`, one compute pass
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

## Diagnostics

What the app logs, how long it is kept and how to read it. Everything is bounded: nothing here grows without limit.

**What is logged** (Serilog, console only: docker captures it, and the host's Grafana Alloy ships the container log to Loki):

- Circuit lifecycle at Information, a few lines per circuit: `Circuit <id8> opened (phone|tablet|desktop|kiosk)`, `connected`,
  `connection down ... after N s up`, `reconnected ... after N ms down, reconnect K`, `closed ... after N s, K reconnects,
  closed while connected|disconnected`. Phone reconnect churn shows as many down/reconnected pairs on one circuit id.
- The framework's `CircuitRegistry` at Debug (evictions, reconnect attempts, disconnects that became permanent), the rest of
  `Microsoft.AspNetCore.Components.Server.Circuits` at Warning (that namespace logs per render batch at Debug).
- Warnings: `Slow request: <method> <path> -> <status> in N ms` (over 1 s; websockets, `/_blazor` and uploads over 1 MB are
  left out) and `Slow database command: N ms: <sql>` (over 500 ms; SQL text only, never parameter values).
- Levels come from `Logging__LogLevel__<Category>` (compose passes `LOG_LEVEL` and `LOG_LEVEL_CIRCUIT_REGISTRY` from `.env`; any
  other category can be added to the compose `environment`). Defaults live in `LoggingLevels.cs`.

**Retention** (about two days, size-bounded): docker's `json-file` driver rotates by size, not by age, so the limits are sized
from the measured volume (app about 25 KB/h without circuit logs; postgres 4 KB/h, wall-geometry 7 KB/h, splat-cpu 5 KB/h):

| service | `max-size` x `max-file` | why |
| --- | --- | --- |
| blocwerk | 10m x 3 (30 MB) | two days of circuit logs at several hundred KB/h still fit |
| postgres, wall-geometry, splat-cpu | 5m x 2 (10 MB) | days of normal volume |
| otel-dashboard | 2m x 2 (4 MB) | its telemetry is in memory only (lost on restart); this is just its stdout |

The docker log goes away with the container, so every redeploy empties it. Loki does not: Grafana Alloy on the box reads
every container's docker log and pushes it to Loki (labels `host="vps"`, `container`, `compose_project`, `compose_service`,
`stream`), which keeps 30 days and survives redeploys. The app writes no log files (it did until this change, to `./logs`).

**Proxy access log** (Caddy, `/home/patrickweindl/caddy/Caddyfile`, not in this repo): the `blocwerk.app` site writes one JSON
line per request to `/data/access-logs/blocwerk.app.log` in the `caddy_data` volume, 10 MB per file, 5 old files, none older
than 7 days (at most 60 MB). Request and response headers are dropped and the `id`, `code`, `state` and `access_token` query
values are replaced, so a line has the time, client IP, method, host, path, status, size and `duration`. A websocket is one line
written when it closes, with `duration` the time it was open: lay those next to the app's `connection down ... after N s up`
lines to see whether the proxy saw the connection end at the same moment. The block inside `blocwerk.app { ... }`:

```caddyfile
	log {
		output file /data/access-logs/blocwerk.app.log {
			roll_size 10MiB
			roll_keep 5
			roll_keep_for 168h
		}
		format filter {
			request>headers delete
			request>tls delete
			resp_headers delete
			request>uri query {
				replace id REDACTED
				replace code REDACTED
				replace state REDACTED
				replace access_token REDACTED
			}
		}
	}
```

**Reading the logs**

```sh
ssh ionos 'cd /home/patrickweindl/blocwerk && docker compose logs --since 6h blocwerk | grep -E "Circuit |Slow "'
# also before the last redeploy, in Grafana (Explore, Loki):  {host="vps", compose_service="blocwerk"} |~ "Circuit |Slow "
ssh ionos 'docker exec caddy-caddy-1 sh -c "grep -h /_blazor /data/access-logs/blocwerk.app*.log"'   # websockets, with duration
```

**Database timings** (`pg_stat_statements`): Postgres preloads the library (`command:` in the compose file, needs a postgres
restart), the migration `EnablePgStatStatements` creates the extension (only when the server preloads it, so dev and CI
databases are left alone), and `autodeploy.sh` resets the counters once the last reset is 48 h old (epoch in
`.autodeploy-pgstats-reset`; delete that file to reset on the next minute). Read it either as an admin at
`/administration/db-stats` (top 20 statements by total time and by mean time, read-only) or with psql:

```sh
ssh ionos 'cd /home/patrickweindl/blocwerk && docker compose exec -T postgres psql -U postgres -d blocwerk -c "
select calls, round(total_exec_time) as total_ms, round(mean_exec_time::numeric, 1) as mean_ms, rows, left(query, 120) as query
from pg_stat_statements order by total_exec_time desc limit 20"'
```

If the migration ran before Postgres had the preload (the app updated first), create the extension by hand once:
`docker compose exec -T postgres psql -U postgres -d blocwerk -c "create extension if not exists pg_stat_statements"`.

## Renew the GHCR token before it expires

`GHCR_TOKEN` in the box's `.env` is a GitHub token with `read:packages` and it **expires**. When it does,
every pull fails (`pull failed even after re-authenticating`) and nothing deploys; this already happened
once mid-rollout. Note the expiry date when you create it and renew a week early:

1. GitHub, Settings, Developer settings, Personal access tokens: create a new one with `read:packages`
   (it must be able to read `blocwerk`, `blocwerk-geometry` and `blocwerk-splat-worker`).
2. On the box, edit `.env` and replace the `GHCR_TOKEN=` value (never paste it into a shell command).
3. `docker logout ghcr.io`; the next cron run logs in again with the new token. Check the log.

## Scripted rollout

`rollout.sh` runs the manual rollout runbook as one guarded script, from your machine over ssh (host alias
`ROLLOUT_HOST`, default `ionos`; deploy dir `ROLLOUT_DIR`). Run it from a checkout at the commit you are rolling
out: the compose file, `autodeploy.sh`, `prod.env.example` and the latest migration id come from the checkout.

```sh
docker/prod/rollout.sh --dry-run --sha <git-sha>             # default; read-only, changes nothing
docker/prod/rollout.sh --apply --sha <git-sha> --compute     # the rollout
docker/prod/rollout.sh --apply --skip-wait                   # use whatever :latest is on GHCR now
docker/prod/rollout.sh --resume-only                         # re-enable the autodeploy cron
docker/prod/rollout.sh --rollback <backup-ts> [--apply [--restore-db]]
```

Steps of `--apply`:

1. **Preflight** (the whole dry run): ssh works; free disk; every key of `prod.env.example` is in the box's `.env`
   (names only; a key the compose file reads with a default is only noted); the new compose passes
   `docker compose config -q` against the box's `.env` (fed over stdin, nothing staged); a diff of old vs new compose
   rendered with `--no-interpolate` so no value can appear; whether `autodeploy.sh` differs; the running image's
   revision, the latest migration (repo vs database) and the app's `/health`.
2. **Lock** (`.rollout.lock` on the box), then **pause autodeploy**: every `autodeploy.sh` crontab line is commented
   with the marker `#PAUSED-ROLLOUT# ` (idempotent; the crontab is never replaced by an empty one) and a running
   autodeploy pass is awaited.
3. **Backup** to `backups/<ts>/` (mode 600): `pg_dump -Fc` (verified with `pg_restore -l`), `docker-compose.yml`,
   `env`, `autodeploy.sh`, `appsettings.json`, the crontab; `--images-tar` adds `wall-images.tar.gz`.
4. **Wait for the image**: pulls until `ghcr.io/zannagh/blocwerk:latest` carries the OCI revision label equal to
   `--sha` (overall limit `ROLLOUT_WAIT_MIN`, default 30; fails when it runs out), or `--skip-wait`. Three
   unauthorized pulls in a row (`ROLLOUT_AUTH_FAILS`) fail fast: the box's `GHCR_TOKEN` expired, see
   "Renew the GHCR token" above.
5. **Install** compose and `autodeploy.sh` as `.new`, validate, `mv`. `.env` is never copied.
6. **Pull and `up -d`** the app, plus `wall-geometry` and `splat-cpu` with `--compute`.
7. **Verify**: `/health` answers 200 (inside the `edge` network, up to 3 min) and the database's latest
   `__EFMigrationsHistory` id equals the repo's newest migration.
8. **Resume autodeploy.** Post-steps (UI checks, flags) stay manual.

Safety: on any failure after the pause, and on Ctrl-C (INT), TERM or HUP (exit 130/143/129), a trap re-enables
autodeploy (and releases the lock) exactly once unless `--keep-paused`; it prints the `--rollback` command for the backup. Everything is logged with timestamps to
`~/.local/state/blocwerk-rollout/rollout-<ts>.log` (`ROLLOUT_LOG_DIR`) and copied to `backups/<ts>/rollout.log` on the
box. `.env` values are never read into the script or printed. (A shell that starts the script in the background
may hand it SIGINT ignored, which bash cannot trap; use TERM there.) Tests against a fake box:
`docker/prod/tests/rollout-test.sh`.

`--rollback <ts>` without `--apply` only checks the backup and prints the plan. With `--apply` it pauses autodeploy and
restores compose, `.env` (600), `autodeploy.sh` and `appsettings.json`, then `up -d`. The images are `:latest`, so this
restores configuration, not the old image, and autodeploy **stays paused** (it would pull the new image again): resume
with `--resume-only` once you are done. `--restore-db` additionally does `pg_restore --clean` of the dump, which
destroys data written since the backup; it needs `--apply` and you must type `restore-db <ts>`.

CI runs shellcheck on every `docker/prod/*.sh`, `rollout.sh` included.
