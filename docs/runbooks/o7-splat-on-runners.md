# Runbook: 3D prep on runners, splat-cpu off the VPS (O7 flip)

Moves `splat-prepare` (COLMAP, training bundle) and `splat-finish` (alignment, crop, export) from the `splat-cpu`
container on the production box to the 3D runners, then stops `splat-cpu`. Background: plan O7 (§3, §4), the
compute envelope in `docker/compute-jobs-protocol.md`, the runner side in `docker/splat-worker/README.md`
("The CPU half on a runner").

The switch is one flag, `SPLATSERVICE_ONRUNNERS` in the box's `.env` (the compose file maps it to
`SPLATSERVICE__ONRUNNERS`). No migration is tied to the flip; it is undone by setting the flag back and starting
`splat-cpu` again.

## 0. Rules

- **One SSH login to the box for the whole run.** Every login pings the owner. Open one multiplexed master
  connection (section 2) and run every step through it; close it at the end.
- **`gpu-runner-prod` gets an image tag bump only.** Same env file (`../config/runner-prod.env`), same
  `--server https://blocwerk.app`, same state directory. Never repoint it, never rebuild its current image in place.
- `.env` and `appsettings.json` values never go to the terminal: grep key names, never `cat`, never `source` `.env`
  (it has unquoted shell-special characters).
- Do not flip while a release build is running (a later `:latest` push can roll the app back).

## 1. Preconditions (before the day)

1. **Prod runs an app that has O7 PRs 3-5** (staging-o7 released to main, flag still off). Check the image
   revision and the migrations in step 4 below: `GpuRunners."Capabilities"` must be wider than 64 characters and
   `GpuJobs` must have `NoRunnerNotifiedAt`.
2. **Exp verification passed** on the Windows PC with the runner image you are about to give `gpu-runner-prod`
   (marker capture end to end to a photo-real view; an old runner never claims the new kinds; a killed runner's
   prepare is reclaimed).
3. **`gpu-runner-prod` bumped** (on the PC, not the box; `ssh pc`, PowerShell). The image must contain #245 and #246 (a runner's
   prepare child otherwise honours `SPLAT_PROFILE_OVERRIDE=ultra`, which `gpu-runner-prod` sets, and every view trains
   as ultra; #246: a runner refused captures above 600 images). Use `blocwerk-exp/splat-worker-cuda:o7-4680525` (local on the PC, built from
   staging-o7 750d26ec + #246). Keep the old tag for the rollback:

   ```powershell
   cd C:\Users\patri\blocwerk-data\compose
   $ts = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
   Copy-Item docker-compose.yml "docker-compose.yml.bak-pre-o7-$ts"
   # Edit ONLY the image line of the gpu-runner-prod service, e.g.
   #   image: blocwerk-exp/splat-worker-cuda:main-b3bde06  ->  image: blocwerk-exp/splat-worker-cuda:o7-<sha>
   # (an editor, or Get-Content | ForEach-Object -replace | Set-Content on that exact line; not the gpu-runner line)
   docker compose -f docker-compose.yml --env-file ..\config\exp.env config --images   # check the two runner tags
   # Only when it is not mid-job (prod admin, 3D runners: idle), recreate it alone:
   docker compose -f docker-compose.yml --env-file ..\config\exp.env up -d --no-deps gpu-runner-prod
   docker logs --tail 20 blocwerk-exp-gpu-runner-prod-1    # hello to https://blocwerk.app, no errors
   ```

   On a prod app WITHOUT O7 the new runner is harmless (unknown capabilities are dropped on hello). On a prod app
   WITH O7, its row then lists `splat-prepare,splat-finish` (step 4).

   What changes for the prepare once it runs in `gpu-runner-prod` instead of `splat-cpu`: COLMAP runs with the CUDA
   image's defaults (GPU matching, `COLMAP_MAX_FEATURES=16384`, `COLMAP_MAX_MATCHES=32768`) instead of splat-cpu's
   `.env` values. Since #246 a runner accepts the largest capture any server may send (1000 photos + 400 frames + 15
   anchors = 1415 images) and grows the prepare timeout with it, unless its own `MAX_PHOTOS` / `SPLAT_TIMEOUT_S` say
   otherwise; the server has already checked the capture against `CAPTURE__MAXPHOTOS` / `CAPTURE__MAXVIDEOFRAMES`.
4. **Capture videos keep working**: frames are extracted in the app (ffmpeg, `CaptureVideoFrameExtractor`), not by
   splat-cpu; the upload gate (`IsSplatConfigured`) is true with the flag on as long as `RUNNERS__MODE` is not `off`.
   Verified on exp 2026-10-28 (video upload 200 with the splat worker stopped).
5. **Every wall that captures needs a runner bound to it** (`GpuRunnerWalls`, the wall's "3D runners" panel) or a
   shared runner, otherwise its prepare waits ("none online") like a training does today.

## 2. Open the one SSH connection

```sh
HOST=ionos                         # the ssh alias; add -J mbp below if Tailscale SSH asks for a check
CP=~/.ssh/cm-blocwerk-o7-%C
ssh -o ControlMaster=yes -o ControlPath="$CP" -o ControlPersist=3h -fN "$HOST"   # the ONLY login
ssh -o ControlPath="$CP" -O check "$HOST"                                          # "Master running"
r()  { ssh -o ControlMaster=no -o ControlPath="$CP" "$HOST" "cd /home/patrickweindl/blocwerk && $*"; }
rcp() { scp -o ControlMaster=no -o ControlPath="$CP" "$@"; }
psqlq() { r "docker compose exec -T postgres psql -U postgres -d blocwerk -At -c \"$1\""; }
```

Every command below goes through `r`, `rcp` or `psqlq`. If `-O check` ever fails, stop and re-open the master
rather than letting plain `ssh` log in again.

## 3. Install the compose file (splat-cpu behind its own profile)

From a checkout at the commit being rolled out. This changes nothing that runs: `splat-cpu` keeps running, it
only stops being part of `COMPOSE_PROFILES=compute`.

```sh
ts=$(date -u +%Y%m%dT%H%M%SZ)
rcp docker/docker-compose.prod.yml "$HOST:/home/patrickweindl/blocwerk/docker-compose.yml.new"
r "cp -p docker-compose.yml docker-compose.yml.bak-pre-o7-$ts && cp -p .env env.bak-pre-o7-$ts && chmod 600 env.bak-pre-o7-$ts"
r "diff <(docker compose config --no-interpolate) <(docker compose -f docker-compose.yml.new config --no-interpolate)"
r "docker compose -f docker-compose.yml.new config -q && mv docker-compose.yml.new docker-compose.yml"
r "grep -c '^SPLATSERVICE_ONRUNNERS=' .env; grep '^COMPOSE_PROFILES=' .env | cut -d= -f1"   # names only
```

Expect the diff to show the `splat-cpu` profile and the new `SPLATSERVICE__ONRUNNERS` (default `false`).
`docker/prod/rollout.sh --compute` no longer names `splat-cpu` (naming a service starts it whatever its profile).

## 4. Checks right before the flip

```sh
# a) The app is the O7 build and the schema is there.
r "docker compose images blocwerk"
psqlq "select max(\"MigrationId\") from \"__EFMigrationsHistory\""
psqlq "select character_maximum_length from information_schema.columns where table_name='GpuRunners' and column_name='Capabilities'"
psqlq "select count(*) from information_schema.columns where table_name='GpuJobs' and column_name='NoRunnerNotifiedAt'"   # 1

# b) gpu-runner-prod advertises the new kinds and has the memory for a prepare (default 6144 MB).
psqlq "select \"Name\", \"Capabilities\", \"MemoryBudgetMb\", \"RunnerVersion\", \"LastSeenAt\", \"Paused\" from \"GpuRunners\" where \"RevokedAt\" is null"

# c) splat-cpu is idle: no queued or running job (the same probe autodeploy uses; the key stays in the container).
r "docker compose --profile splat-cpu exec -T splat-cpu python -c '
import json, os, urllib.request as u
req = u.Request(\"http://127.0.0.1:%s/v1/info\" % os.environ.get(\"PORT\", \"8100\"),
                headers={\"Authorization\": \"Bearer \" + os.environ.get(\"COMPUTE_API_KEY\", \"\")})
print(json.load(u.urlopen(req, timeout=5))[\"jobs\"])'"                                    # {'queued': 0, 'running': 0}

# d) No capture is mid-prepare on splat-cpu (Status 8 = Splatting, a "prepare:" id = a splat-cpu prepare).
psqlq "select \"Id\", \"Status\", \"SplatJobId\" from \"WallCaptures\" where \"Status\" = 8 and \"SplatJobId\" like 'prepare:%'"   # no rows

# e) Advisory (time, not data): a markerless reconstruction or a finish still on splat-cpu is resubmitted once to a
#    runner after the flip. Prefer zero rows here too.
psqlq "select \"Id\", \"Status\", \"SfmJobId\" from \"WallCaptures\" where \"Status\" = 3 and \"SfmJobId\" is not null and \"SfmJobId\" not like 'runner:%'"
psqlq "select \"Id\", \"Kind\", \"Status\", \"FinishJobId\" from \"GpuJobs\" where \"FinishJobId\" is not null and \"FinishJobId\" not like 'runner:%' and \"InstalledAt\" is null and \"Status\" < 4"

# f) The app's busy gate (recreating it mid-edit costs the editor a reload).
r "docker run --rm --network edge curlimages/curl:latest -s --max-time 5 http://blocwerk:5050/health/ready-to-deploy"   # idle

# g) appsettings.json has no SplatService block (it would win over the environment variable).
r "jq -r 'paths | join(\".\")' appsettings.json | grep -i -c splat"                         # 0
```

Go only when a), b), c), d) and g) hold. Repeat c) and d) right before step 5 if time passed.

## 5. Flip

```sh
r "grep -q '^SPLATSERVICE_ONRUNNERS=' .env && sed -i 's/^SPLATSERVICE_ONRUNNERS=.*/SPLATSERVICE_ONRUNNERS=true/' .env || printf '\nSPLATSERVICE_ONRUNNERS=true\n' >> .env"
r "grep -c '^SPLATSERVICE_ONRUNNERS=true$' .env"                                             # 1
# Recreate the app under autodeploy's own app lock, so a cron run cannot recreate it at the same moment.
r "flock .autodeploy-app.lock docker compose up -d --no-deps blocwerk"
r "docker compose exec -T blocwerk printenv SPLATSERVICE__ONRUNNERS RUNNERS__MODE"           # true / always
# Health from inside the edge network (up to ~2 min after the recreate).
r "docker run --rm --network edge curlimages/curl:latest -s -o /dev/null -w '%{http_code}\n' --max-time 5 http://blocwerk:5050/health"   # 200
r "docker compose logs --since 5m blocwerk | grep -iE 'ONRUNNERS|RUNNERS__MODE is off|fail|exception' | tail -20"
```

`.env` is not a bind mount, so `sed -i` is fine here (unlike the Caddyfile).

## 6. Stop splat-cpu

Stop, do not remove: the container stays for a quick rollback, and `restart: unless-stopped` keeps it stopped
across reboots. Autodeploy leaves a stopped compute service alone.

```sh
r "docker compose --profile splat-cpu stop splat-cpu"
r "docker compose --profile splat-cpu ps -a splat-cpu"                                      # Exited
```

## 7. After the flip

```sh
r "docker stats --no-stream --format 'table {{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}'"         # no splat-cpu; app + geometry within budget
psqlq "select \"Kind\", \"Status\", \"ClaimedByRunnerId\" is not null as claimed, \"CreatedAt\", \"Error\" from \"GpuJobs\" where \"Kind\" in (4, 5) order by \"CreatedAt\" desc limit 10"   # 4 = prepare, 5 = finish
r "tail -5 autodeploy.log"
ssh -o ControlPath="$CP" -O exit "$HOST"                                                       # close the one connection
```

Then, within the day: one real photo-real job (a refinish of an existing view, or a small capture) must produce
`Kind 4` / `5` rows that `gpu-runner-prod` claims and that end `Succeeded` (3), and the view must update. While no
runner is online the capture shows "waiting for a 3D runner"; that is the expected state, not a failure (plan §3).
Soak a week, including one real capture, before removing the `splat-cpu` container or its compose entry.

## 8. Rollback

Any time, in this order (one SSH master again, section 2):

```sh
r "sed -i 's/^SPLATSERVICE_ONRUNNERS=.*/SPLATSERVICE_ONRUNNERS=false/' .env && grep -c '^SPLATSERVICE_ONRUNNERS=false$' .env"
r "docker compose --profile splat-cpu up -d splat-cpu"
r "flock .autodeploy-app.lock docker compose up -d --no-deps blocwerk"
r "docker compose exec -T blocwerk printenv SPLATSERVICE__ONRUNNERS"                         # false
```

- In-flight runner prepare/finish ids (`runner:...`) are unknown to splat-cpu: the app resubmits them once to
  splat-cpu (time, not data). The sweep cancels queued prepare/finish jobs while the flag is off.
- To keep `splat-cpu` through a later `docker compose up -d`, also add it to `COMPOSE_PROFILES` (`compute,splat-cpu`)
  in `.env`; otherwise the stopped/started container is left exactly as it is.
- Full config rollback: `cp -p docker-compose.yml.bak-pre-o7-<ts> docker-compose.yml` and
  `cp -p env.bak-pre-o7-<ts> .env`, then the two `up -d` lines above.
- `gpu-runner-prod` can stay on the new tag (it still trains and does textures, holds and panels as before); to go
  back, restore `docker-compose.yml.bak-pre-o7-<ts>` on the PC and `up -d --no-deps gpu-runner-prod` there.
