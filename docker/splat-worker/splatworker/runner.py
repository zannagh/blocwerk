"""Job body (runs in the job's own process group; see computejobs.jobs)."""
from computejobs.child import run_in_child


def run_job(kind, job_dir, conn):
    if kind == "splat-prepare":
        from .prepare import run_prepare as body
    elif kind == "splat-finish":
        from .finish import run_finish as body
    else:
        from .pipeline import run as body
    run_in_child(body, job_dir, conn, (ValueError,))
