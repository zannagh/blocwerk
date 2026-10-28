# Blocwerk Helm chart

The app as it runs today in `docker/docker-compose.prod.yml`, for Kubernetes: the same images, environment variables
and volumes. Published as `oci://ghcr.io/zannagh/charts/blocwerk` by `.github/workflows/chart.yml` when a release is
published; the chart version equals the app version (the bundle), and the web image defaults to it.

```sh
helm install blocwerk oci://ghcr.io/zannagh/charts/blocwerk --version X.Y.Z -n blocwerk -f my-values.yaml
```

Every value is documented in [values.yaml](values.yaml); [values.schema.json](values.schema.json) enforces the rules.

| Part | Objects | Off switch |
|---|---|---|
| web (Blazor Server, API, background services, in-process ONNX) | Deployment `<release>`, Service :5050, optional Ingress, PVC `<release>-data` | none |
| Postgres 17 (`pg_stat_statements` preloaded) | StatefulSet + Service `<release>-postgres`, PVC, stats-reset CronJob | `postgres.enabled` (then `postgres.external`) |
| wall-geometry | Deployment + Service `<release>-geometry` :8000 | `geometry.enabled` |
| splat-cpu (COLMAP prepare/finish, never trains) | Deployment + Service `<release>-splat-cpu` :8100 | `splatCpu.enabled` (off by default) |
| Aspire dashboard (anonymous: keep the UI private) | Deployment + Service `<release>-aspire` :18888/:18889 | `aspire.enabled` |
| deploy gate | hook Jobs, their ConfigMap, ServiceAccount and Role | `deployGate.enabled` |

Not in the chart, because they are environment-specific: network policies, the legacy-domain redirect, private
ingresses for the dashboard, backups, and every Secret (referenced by name in `secrets.*`).

## Rules

- **One web replica.** The app keeps circuits, edit leases, the feed signal and job queues in its process;
  `web.replicas` is 0 or 1 (0 only for a cutover). The later web/worker split changes this chart, not the rule's reason.
- **Secrets by name only.** `secrets.app` (envFrom), `secrets.postgres`, `secrets.compute`, optional
  `secrets.appsettings` and `secrets.deploy`. `BLOCWERK__ENCRYPTIONKEY` and the key ring in `keys/` on the data volume
  must be carried over verbatim when moving an installation.
- **splat-cpu is optional** and nothing assumes it. `splat.onRunners: true` (`SPLATSERVICE__ONRUNNERS`) sends
  splat-prepare and splat-finish to 3D runners as jobs (needs `web.env.RUNNERS__MODE` other than `off`; the render
  fails otherwise); with splat-cpu off, `SPLATSERVICE__URL`/`__APIKEY` are not set. Rollback: `splat.onRunners: false`
  plus `splatCpu.enabled: true`.
- **Publishing:** a GitHub release `vX.Y.Z` (not a prerelease) of a commit that contains `.github/workflows/chart.yml`
  pushes chart `X.Y.Z`. The package `charts/blocwerk` is private on its first push; make it public once in its
  settings (no API exists for that), then re-run the job, whose last step checks an anonymous pull.

## Deploy gate

The in-cluster form of `docker/prod/autodeploy.sh` (ArgoCD hooks; `helm upgrade` runs them as pre-upgrade hooks):

1. **PreSync, web** (`files/deploy-gate.sh`): reads the running web pods' `blocwerk.app/rollout` fingerprint (a hash of
   the pod spec: image, env, volumes, resources) from the kube API and compares it with this sync's. Unchanged or not
   running: done. Otherwise it waits while `/health/ready-to-deploy` says `busy` (`maxDefer` polls, then on while
   `/health/busy-eta` reports an end, up to `etaMaxDefer`), announces the maintenance, waits `announceGraceSeconds`,
   forces `POST /api/v1/maintenance/flush-edits`, and lets the sync replace the pod.
2. **Sync wave 1, workers** (`files/compute-gate.sh`): for each enabled worker whose fingerprint changed, two
   consecutive idle polls of `/v1/info`, at most `computeMaxDefer` busy polls. The workers are wave 2.

The gate pods are the only ones with a kube API token (get/list pods in the namespace); with a NetworkPolicy, they
need egress to the API server.

## Tests

`tests/chart-test.sh` (lint, render every `ci/*-values.yaml`, kubeconform, the rules above) and
`tests/compare-compose.sh` (environment names and mounts against the compose file; every difference listed with its
reason). Both run in CI.
