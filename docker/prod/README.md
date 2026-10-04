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

**What is logged** (Serilog: console, which docker captures, plus `logs/blocwerk<day>.log` inside the app container):

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

The app's own files keep today plus two days (`retainedFileCountLimit: 3`). They live in the container's writable layer, so a
redeploy empties them; use the docker logs. If the volume turns out larger than expected, the app's logs rotate earlier than two
days: raise `max-size`, not the log level, first.

**Reading the logs**

```sh
ssh ionos 'cd /home/patrickweindl/blocwerk && docker compose logs --since 6h blocwerk | grep -E "Circuit |Slow "'
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
   `--sha` (up to `ROLLOUT_WAIT_MIN`, 30), or `--skip-wait`.
5. **Install** compose and `autodeploy.sh` as `.new`, validate, `mv`. `.env` is never copied.
6. **Pull and `up -d`** the app, plus `wall-geometry` and `splat-cpu` with `--compute`.
7. **Verify**: `/health` answers 200 (inside the `edge` network, up to 3 min) and the database's latest
   `__EFMigrationsHistory` id equals the repo's newest migration.
8. **Resume autodeploy.** Post-steps (UI checks, flags) stay manual.

Safety: on any failure after the pause, a trap re-enables autodeploy (and releases the lock) unless `--keep-paused`;
it prints the `--rollback` command for the backup. Everything is logged with timestamps to
`~/.local/state/blocwerk-rollout/rollout-<ts>.log` (`ROLLOUT_LOG_DIR`) and copied to `backups/<ts>/rollout.log` on the
box. `.env` values are never read into the script or printed.

`--rollback <ts>` without `--apply` only checks the backup and prints the plan. With `--apply` it pauses autodeploy and
restores compose, `.env` (600), `autodeploy.sh` and `appsettings.json`, then `up -d`. The images are `:latest`, so this
restores configuration, not the old image, and autodeploy **stays paused** (it would pull the new image again): resume
with `--resume-only` once you are done. `--restore-db` additionally does `pg_restore --clean` of the dump, which
destroys data written since the backup; it needs `--apply` and you must type `restore-db <ts>`.

CI runs shellcheck on every `docker/prod/*.sh`, `rollout.sh` included.
