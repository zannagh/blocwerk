"""Bearer auth + callback signing now live in the shared package (docker/compute-jobs-py).

Re-exported here because docker/compute-jobs-protocol.md points receivers at `sign`/`verify`."""
from computejobs.security import require_api_key, sign, verify  # noqa: F401
