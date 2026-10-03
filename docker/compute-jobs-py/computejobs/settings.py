"""Runtime configuration from the environment.

One process-wide `settings` object. A service calls `settings.load(...)` once at import with its own
defaults and may hang extra attributes on it; tests override attributes directly.

The two secrets (COMPUTE_API_KEY, COMPUTE_CALLBACK_SECRET) are read and then REMOVED from os.environ,
so nothing this process starts (job processes, COLMAP, Brush, ...) inherits them.
"""
import os

SECRET_VARS = ("COMPUTE_API_KEY", "COMPUTE_CALLBACK_SECRET")
_captured = {}


def env_int(name, default):
    try:
        return int(os.environ.get(name, default))
    except ValueError:
        return default


def env_flag(name):
    return os.environ.get(name, "").strip().lower() in ("1", "true", "yes")


def take_secret(name):
    """Read a secret once and scrub it from the environment (later loads reuse the captured value)."""
    value = os.environ.pop(name, None)
    if value:
        _captured[name] = value
    return _captured.get(name)


class Settings:
    def __init__(self):
        self.load()

    def load(self, *, work_dir="/tmp/compute-jobs", max_request_mb=400, max_photo_mb=40,
             max_photos=60, max_queued=16, result_ttl_s=3600):
        self.api_key = take_secret("COMPUTE_API_KEY")
        self.callback_secret = take_secret("COMPUTE_CALLBACK_SECRET")
        self.work_dir = os.environ.get("WORK_DIR", work_dir)
        self.max_request_bytes = env_int("MAX_REQUEST_MB", max_request_mb) * 1024 * 1024
        self.max_photo_bytes = env_int("MAX_PHOTO_MB", max_photo_mb) * 1024 * 1024
        self.max_photos = env_int("MAX_PHOTOS", max_photos)
        self.max_queued = env_int("MAX_QUEUED_JOBS", max_queued)
        self.result_ttl_s = env_int("RESULT_TTL_S", result_ttl_s)
        # decoded pixel count above which a photo is refused (decompression bombs); 100 MP
        self.max_image_pixels = env_int("MAX_IMAGE_MEGAPIXELS", 100) * 1_000_000
        # callbacks to loopback/private addresses (SSRF guard, see netguard.py)
        self.callback_allow_private = env_flag("CALLBACK_ALLOW_PRIVATE")
        self.callback_allowed_hosts = os.environ.get("CALLBACK_ALLOWED_HOSTS", "")
        self.git_sha = os.environ.get("GIT_SHA", "unknown")
        return self


settings = Settings()
