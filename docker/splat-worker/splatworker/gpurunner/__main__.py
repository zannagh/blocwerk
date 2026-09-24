"""`python -m splatworker.gpurunner --server https://blocwerk.app` (key: env BWR_KEY only)."""
import argparse
import logging
import os
import signal
import sys
import tempfile

from ..settings import settings  # noqa: F401 - loads the env (BRUSH_BIN, SPLAT_MAX_MEMORY_MB, ...) first
from .client import Client, check_server
from .loop import Runner


def parse_args(argv):
    p = argparse.ArgumentParser(prog="python -m splatworker.gpurunner",
                                description="Blocwerk 3D runner: trains photo-real views (Gaussian splats) "
                                            "on this machine's GPU. The key is read from BWR_KEY.")
    p.add_argument("--server", required=True, help="the Blocwerk server, e.g. https://blocwerk.app")
    p.add_argument("--insecure-http", action="store_true", help="allow plain http to a non-local server")
    p.add_argument("--once", action="store_true", help="exit after one job (testing)")
    p.add_argument("--key", help=argparse.SUPPRESS)
    return p.parse_args(argv)


def main(argv=None):
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", stream=sys.stderr)
    args = parse_args(argv)
    if args.key:
        sys.exit("refusing --key: a key on the command line ends up in the shell history and `ps`; set BWR_KEY")
    key = os.environ.pop("BWR_KEY", "").strip()  # popped: Brush and every other child never see it
    if not key.startswith("bwr_"):
        sys.exit("BWR_KEY is not set to a runner key (bwr_...): create one in Blocwerk (wall settings, 3D runners)")
    try:
        server = check_server(args.server, args.insecure_http)
    except ValueError as e:
        sys.exit(str(e))
    work_dir = os.environ.get("RUNNER_WORK_DIR") or os.path.join(tempfile.gettempdir(), "blocwerk-runner")
    runner = Runner(Client(server, key), work_dir, max_jobs=1 if args.once else None)

    def stop(signum, _frame):
        logging.getLogger("gpurunner").info("signal %d: finishing", signum)
        runner.shutdown.set()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    logging.getLogger("gpurunner").info("Blocwerk 3D runner -> %s (work dir %s)", server, work_dir)
    return runner.run()


if __name__ == "__main__":
    sys.exit(main())
