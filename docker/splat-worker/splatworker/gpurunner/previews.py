"""The trainer's previews (checkpoints.newest_preview), uploaded while it trains, on their own thread: each one is
checked like the final result (gsplat_trainer.check_frame, so a preview in the wrong frame is never installed),
slimmed and PUT to /preview. Best effort: a preview that fails the check or the upload is skipped (the next one,
or the final result, follows), and one still uploading when the training ends is aborted (the final result
goes first). Only the newest preview is ever sent."""
import glob
import logging
import os
import threading
import time

import numpy as np
from computejobs.child import JobError

from .. import checkpoints, gsplat_trainer
from ..slimply import write_slim_ply
from ..splatio import read_ply
from . import client as http
from .client import Backoff, Gone, Rejected, Stopped, Transient, Unauthorized

log = logging.getLogger("gpurunner")
POLL_S = 5.0
PATIENCE_S = 300  # a preview is not worth waiting long for: the next one follows


class PreviewUploader:
    def __init__(self, client, job_id, directory, dataset, stop, poll_s=None):
        self.client, self.job_id, self.dir, self.dataset, self.stop = client, job_id, directory, dataset, stop
        self.poll_s = POLL_S if poll_s is None else poll_s
        self.abort = threading.Event()  # ends a running upload: the training is over
        self.sent, self.uploaded = 0, []
        self.thread = threading.Thread(target=self._loop, name="previews", daemon=True)

    def start(self):
        self.thread.start()
        return self

    def close(self):
        self.abort.set()
        if self.thread.is_alive():
            self.thread.join(timeout=30)

    def _loop(self):
        while not self.abort.is_set() and not self.stop.is_set():
            self.poll_once()
            self.abort.wait(self.poll_s)

    def poll_once(self):
        """Sends the newest preview if it is newer than the last one taken; returns its step or None."""
        found = checkpoints.newest_preview(self.dir)
        if found is None or found[0] <= self.sent:
            return None
        step, total, path = found
        self.sent = step
        try:
            self._send(step, total, path)
        except (JobError, ValueError, OSError) as e:
            log.warning("job %s: the preview at step %d is not uploaded: %s", self.job_id, step, e)
        finally:
            self._clean(step)
        return step

    def _clean(self, step):
        """Drops the previews up to `step` and the upload copies (a newer preview stays for the next poll)."""
        for f in glob.glob(os.path.join(glob.escape(self.dir), "*")):
            m = checkpoints.PREVIEW.search(f)
            if (m and int(m.group(1)) <= step) or os.path.basename(f).startswith("upload-"):
                _remove(f)

    def _send(self, step, total, path):
        cols = read_ply(path)
        check = gsplat_trainer.check_frame(np.stack([cols["x"], cols["y"], cols["z"]], 1), self.dataset)
        slim = os.path.join(self.dir, f"upload-{step}.ply")
        write_slim_ply(cols, slim)
        stats = {"previewStep": step, "totalSteps": total, "frameCheckSpreadRatio": (check or {}).get("spreadRatio")}
        self._upload(step, total, slim, stats)

    def _upload(self, step, total, slim, stats):
        backoff, t0 = Backoff(), time.monotonic()
        while not self.abort.is_set() and not self.stop.is_set():
            try:
                sent = self.client.upload_preview(self.job_id, slim, step, total, stats, self.abort)
                self.uploaded.append(step)
                log.info("job %s: preview at step %d/%d uploaded (%.1f MB)", self.job_id, step, total, sent / 1e6)
                return
            except Transient as e:
                if time.monotonic() - t0 > PATIENCE_S:
                    log.info("job %s: preview at step %d not delivered (%s); skipping it", self.job_id, step, e)
                    return
                http.pause(self.abort, backoff.next(e.retry_after))
            except (Rejected, Gone, Stopped, Unauthorized) as e:
                log.info("job %s: preview at step %d not taken (%s)", self.job_id, step, e)
                return


def _remove(path):
    try:
        os.remove(path)
    except OSError:
        pass
