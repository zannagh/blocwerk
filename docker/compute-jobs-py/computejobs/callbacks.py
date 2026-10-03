"""Best-effort status callbacks: signed POSTs from a background thread, never blocking a job.

Outbound requests go through netguard (SSRF guard: no redirects, no metadata/link-local targets,
private targets only when allowed, DNS resolved once and pinned)."""
import json
import logging
import queue
import threading
import time

from . import netguard
from .security import sign
from .settings import settings

log = logging.getLogger("computejobs.callbacks")
RETRY_DELAYS_S = (1, 3, 9)
TIMEOUT_S = 5


class CallbackSender:
    def __init__(self):
        self.q = queue.Queue(maxsize=1000)
        self.warned = False
        threading.Thread(target=self._loop, name="callbacks", daemon=True).start()

    def send(self, url, status):
        if not url:
            return
        if not settings.callback_secret:
            if not self.warned:
                log.warning("callbackUrl given but COMPUTE_CALLBACK_SECRET is unset: callbacks are "
                            "not sent (they could not be verified); poll instead")
                self.warned = True
            return
        body = json.dumps(status, separators=(",", ":")).encode()
        try:
            self.q.put_nowait((url, body))
        except queue.Full:
            log.warning("callback queue full, dropping a status update for job %s", status.get("jobId"))

    def _loop(self):
        while True:
            url, body = self.q.get()
            for attempt, delay in enumerate((0,) + RETRY_DELAYS_S):
                time.sleep(delay)
                if self._post(url, body, attempt):
                    break

    @staticmethod
    def _post(url, body, attempt):
        headers = {"Content-Type": "application/json",
                   "X-Blocwerk-Signature": sign(body, settings.callback_secret)}
        try:
            status = netguard.post(url, body, headers, TIMEOUT_S)
        except netguard.UnsafeTarget as e:
            log.warning("callback refused: %s", e)
            return True  # not retryable
        except Exception as e:  # noqa: BLE001 - best effort by design
            log.info("callback attempt %d failed: %s", attempt + 1, type(e).__name__)
            return False
        if not 200 <= status < 300:
            log.info("callback attempt %d answered %d (redirects are not followed)", attempt + 1, status)
        return 200 <= status < 300
