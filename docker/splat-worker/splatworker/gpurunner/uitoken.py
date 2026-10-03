"""The status page's local token (web.py): made at first start in <state dir>/ui-token (owner-only), shown in the
runner log as the page's link, and required for the pause switch (POST): as the `X-Runner-Token` header, or as the
cookie the page sets when it is opened with `?token=...` (or from the machine's own loopback). Stdlib only."""
import hmac
import logging
import os
import secrets

log = logging.getLogger("gpurunner")
COOKIE = "blocwerk_runner_token"


def load_or_create(directory):
    """The token (kept across restarts, so a bookmarked link keeps working)."""
    path = os.path.join(directory, "ui-token")
    try:
        with open(path) as fh:
            token = fh.read().strip()
        if len(token) >= 20:
            return token
    except OSError:
        pass
    token = secrets.token_urlsafe(24)
    try:
        os.makedirs(directory, exist_ok=True)
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as fh:
            fh.write(token)
    except OSError as e:
        log.warning("could not keep the status page token in %s (%s): a new one comes with each start", path, e)
    return token


def matches(token, candidate):
    return bool(candidate) and hmac.compare_digest(token.encode(), str(candidate).encode())


def from_cookie(header):
    for part in (header or "").split(";"):
        name, _, value = part.strip().partition("=")
        if name == COOKIE:
            return value
    return None


def set_cookie(token):
    return f"{COOKIE}={token}; Path=/; HttpOnly; SameSite=Strict"
