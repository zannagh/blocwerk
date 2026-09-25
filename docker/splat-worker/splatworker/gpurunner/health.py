"""`python -m splatworker.gpurunner.health`: the image's HEALTHCHECK for both of its roles.

A 3D runner (it keeps <work dir>/alive fresh, alive.py) is healthy while that file is younger than 120 s;
a stuck loop, a hung upload or a dead process goes unhealthy. Without the file (the worker role: no
runner here) it probes the worker's own /health on 127.0.0.1:$PORT (8100). Exit 0 = healthy, 1 = not.
"""
import os
import sys
import urllib.request

from .alive import MAX_AGE_S, age_s, path


def runner_healthy(file_path=None, max_age_s=MAX_AGE_S):
    """True / False for a runner, None when this is not a runner (no alive file)."""
    age = age_s(file_path or path())
    return None if age is None else age < max_age_s


def worker_healthy(port=None, timeout=4):
    url = f"http://127.0.0.1:{port or os.environ.get('PORT') or 8100}/health"
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return r.status == 200
    except OSError:
        return False


def main():
    runner = runner_healthy()
    return 0 if (runner if runner is not None else worker_healthy()) else 1


if __name__ == "__main__":
    sys.exit(main())
