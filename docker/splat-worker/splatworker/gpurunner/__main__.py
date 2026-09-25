"""`python -m splatworker.gpurunner --server https://blocwerk.app` (key: env BWR_KEY only).

Exit codes: 0 stopped (SIGTERM / SIGINT, --once), 2 misconfigured (no key, bad URL, trainer missing),
3 the key was refused (revoked or wrong: create a new one in Blocwerk)."""
import argparse
import logging
import os
import signal
import sys

from ..settings import settings  # loads the env (SPLAT_TRAINER, GSPLAT_PYTHON, BRUSH_BIN, ...) first
from .alive import work_dir
from .caps import Capabilities
from .client import Client, check_server
from .loop import Runner

log = logging.getLogger("gpurunner")


def parse_args(argv):
    p = argparse.ArgumentParser(prog="python -m splatworker.gpurunner",
                                description="Blocwerk 3D runner: trains photo-real views (Gaussian splats) "
                                            "on this machine's GPU. The key is read from BWR_KEY.")
    p.add_argument("--server", required=True, help="the Blocwerk server, e.g. https://blocwerk.app")
    p.add_argument("--insecure-http", action="store_true", help="allow plain http to a non-local server")
    p.add_argument("--no-gzip", action="store_true", help="upload the trained scene uncompressed")
    p.add_argument("--once", action="store_true", help="exit after one job (testing)")
    p.add_argument("--key", help=argparse.SUPPRESS)
    return p.parse_args(argv)


def read_key():
    """BWR_KEY, popped from the environment: no child process (trainer, nvidia-smi) ever sees it."""
    key = os.environ.pop("BWR_KEY", "").strip()
    if not key.startswith("bwr_"):
        sys.exit("BWR_KEY is not set to a runner key (bwr_...): create one in Blocwerk (wall settings, 3D runners) "
                 "and pass it through the environment (docker: --env-file runner.env)")
    return key


def main(argv=None):
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", stream=sys.stderr)
    args = parse_args(argv)
    if args.key:
        sys.exit("refusing --key: a key on the command line ends up in the shell history and `ps`; set BWR_KEY")
    key = read_key()
    try:
        server = check_server(args.server, args.insecure_http)
    except ValueError as e:
        sys.exit(str(e))
    if settings.profile_override:  # the server picks each job's quality; a runner never overrides it
        log.warning("SPLAT_PROFILE_OVERRIDE=%s is ignored by a 3D runner", settings.profile_override)
        settings.profile_override = ""
    try:
        caps = Capabilities()
    except ValueError as e:
        sys.exit(str(e))
    if not caps.usable:
        log.error("the %s trainer is not usable here (set GSPLAT_PYTHON / BRUSH_BIN; gsplat needs a CUDA GPU: "
                  "docker run --gpus all)", caps.trainer)
        return 2
    gzip_upload = not args.no_gzip and os.environ.get("RUNNER_UPLOAD_GZIP", "1").strip() not in ("0", "false", "no")
    runner = Runner(Client(server, key, gzip_upload), work_dir(), caps, max_jobs=1 if args.once else None)

    def stop(signum, _frame):
        log.info("signal %d: handing back the running job and stopping", signum)
        runner.shutdown.set()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    log.info("Blocwerk 3D runner %s -> %s (%s, work dir %s)", caps.version, server, caps.trainer, runner.work_dir)
    return runner.run()


if __name__ == "__main__":
    sys.exit(main())
