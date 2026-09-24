"""Job body (runs in the job's own process group; see computejobs.jobs)."""
from computejobs.child import run_in_child


def run_job(kind, job_dir, conn):
    from .finish import run_finish
    from .pipeline import run, run_prepare
    body = {"splat": run, "splat-prepare": run_prepare, "splat-finish": run_finish}[kind]
    run_in_child(body, job_dir, conn, (ValueError,))
