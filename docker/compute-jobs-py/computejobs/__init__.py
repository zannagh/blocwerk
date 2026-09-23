"""Shared machinery for Blocwerk compute workers (protocol `blocwerk-compute/1`).

One copy, consumed by docker/wall-geometry and docker/splat-worker (each Dockerfile builds from the
repo root and installs this directory). A service supplies only its kinds (request parsing) and a
child-process runner; everything protocol-shaped lives here:

- settings   env configuration (COMPUTE_API_KEY, COMPUTE_CALLBACK_SECRET scrubbed from os.environ, limits)
- security   bearer auth (constant time) + X-Blocwerk-Signature signing/verification
- callbacks  best-effort signed status callbacks from a background thread
- jobs       in-memory queue, one job at a time, each job in its own process group (hard kill)
- limits     request-body limit middleware (413)
- service    FastAPI app factory: /health, /v1/info, POST/GET/DELETE /v1/jobs/..., file download
- child      helpers for the job body in the child process (sanitized errors, minimal tool env)
- bind       refuse to listen beyond localhost without an API key
- serve      the services' `python -m` entry point (bind check, then uvicorn)
- netguard   SSRF guard for callbacks (no redirects, blocked/private ranges, pinned DNS)
- upload     streaming multipart reader: photos cleaned in memory, originals never on disk
- geometry   shape/range check of client-supplied wall-geometry documents
"""
PROTOCOL = "blocwerk-compute/1"
