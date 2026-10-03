"""Test helpers: fixture access and a stand-in job runner (the real pipeline needs COLMAP + a GPU)."""
import os

from computejobs.child import run_in_child

FIXTURES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures")


def fixture_lines(name):
    with open(os.path.join(FIXTURES, name), encoding="utf-8") as fh:
        return [ln.rstrip("\n") for ln in fh]


def _noop(job_dir, progress):
    progress(0.5, "ingest", "1/1 photos")
    return {"files": []}


def noop_runner(kind, job_dir, conn):
    run_in_child(_noop, job_dir, conn)
