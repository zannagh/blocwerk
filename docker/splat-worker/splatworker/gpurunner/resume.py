"""A runner job's checkpoints and previews (checkpoints.py): the settings from the environment and how long a
job's checkpoints live.

RUNNER_CHECKPOINT_EVERY (5000; 0 = off): gsplat saves its whole training state every that many steps and at the
  end, into RUNNER_CHECKPOINT_DIR/<job id>-<bundle sha> (default <work dir>/checkpoints). A job this runner claims
  again (a failed attempt, a runner restart, a lost lease) resumes there instead of at step 0; never across
  another bundle. Inside the container the default lives in /tmp, which survives `docker restart` but not a
  recreate: mount a volume and point RUNNER_CHECKPOINT_DIR (or RUNNER_WORK_DIR) at it to survive both.
RUNNER_CHECKPOINT_TTL_H (72): checkpoint directories untouched for longer are removed when the runner starts.
  A job that succeeded, was cancelled or failed for good drops its own at once.
RUNNER_PREVIEWS ("0.14,0.4"; 0 = off): fractions of the steps at which the splats so far are uploaded as a
  preview, when the server's claim offers previews (ultra, 50k steps: 7000 and 20000).
"""
import os

from .. import checkpoints

DEFAULT_EVERY = 5000
DEFAULT_TTL_H = 72


def _int(text, default):
    try:
        return int(str(text).strip())
    except (TypeError, ValueError):
        return default


class ResumeSettings:
    def __init__(self, work_dir, env=None):
        env = os.environ if env is None else env
        self.every = max(0, _int(env.get("RUNNER_CHECKPOINT_EVERY"), DEFAULT_EVERY))
        self.root = env.get("RUNNER_CHECKPOINT_DIR") or os.path.join(work_dir, "checkpoints")
        self.ttl_s = max(1, _int(env.get("RUNNER_CHECKPOINT_TTL_H"), DEFAULT_TTL_H)) * 3600
        self.previews = checkpoints.parse_fractions(env.get("RUNNER_PREVIEWS", checkpoints.DEFAULT_PREVIEWS))

    def for_job(self, job, job_dir):
        """The claimed job's checkpoints.TrainResume: its checkpoint directory (job id + bundle sha) and, when the
        server offers previews, where the trainer writes them."""
        ck = checkpoints.job_dir(self.root, job.get("jobId"), job.get("bundleSha256")) if self.every > 0 else None
        previews = self.previews if job.get("previews") else ()
        return checkpoints.TrainResume(ck, self.every if ck else 0,
                                       os.path.join(job_dir, "previews") if previews else None, previews)

    def prune(self):
        """Removes checkpoint directories older than the TTL; returns how many."""
        return checkpoints.prune(self.root, self.ttl_s)
