"""Job body (runs in the job's own process group; see computejobs.jobs)."""
from computejobs.child import run_in_child


def run_job(kind, job_dir, conn):
    from .pipeline import run
    run_in_child(run, job_dir, conn, (ValueError,))
