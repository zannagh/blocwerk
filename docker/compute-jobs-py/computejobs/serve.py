"""Entry point shared by the services: `python -m <service>` -> uvicorn, after the bind check.

Both images start through this (never `uvicorn --host 0.0.0.0` directly), so a service can not end up
listening beyond loopback without COMPUTE_API_KEY unless ALLOW_OPEN_BIND=1 says so explicitly.
"""
import os
import sys

from .bind import check_bind
from .settings import settings


def serve(app_path, name, default_port, default_host="127.0.0.1"):
    host = os.environ.get("HOST", default_host)
    try:
        port = int(os.environ.get("PORT", str(default_port)))
    except ValueError:
        print(f"{name}: PORT must be a number", file=sys.stderr)
        sys.exit(2)
    reason = check_bind(host, settings.api_key)
    if reason:
        print(f"{name}: {reason}", file=sys.stderr)
        sys.exit(2)
    import uvicorn
    # ONE process on purpose: the job queue is in memory. Behind a TLS proxy, set FORWARDED_ALLOW_IPS.
    uvicorn.run(app_path, host=host, port=port, workers=1, log_level="info", proxy_headers=True,
                server_header=False)
