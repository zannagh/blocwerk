"""The settings a 3D runner sets differently from the HTTP splat worker, applied in the runner process (__main__) AND in
every spawned compute child (compute_child), which imports the settings afresh from the environment.

* SPLAT_PROFILE_OVERRIDE is ignored: the server picks each job's quality.
* The photo cap (and the prepare timeout grown with it) is the largest capture ANY server may send, unless MAX_PHOTOS /
  SPLAT_TIMEOUT_S say otherwise. The HTTP worker sizes it from its own CAPTURE_MAX_* (the server's .env next to it); a
  runner has no such env and gets jobs from a server that already checked the capture against its own limits, so a
  runner without them must not refuse a capture the server accepted (The Attic: 781 photos against the default 600)."""
import os

from computejobs import capture_limits

from ..settings import FLOOR, settings

PREPARE_BASE_TIMEOUT_S = 4 * 3600


def server_max_images():
    """Photos + video frames + anchor photos of the largest capture any server may accept (the ranges' upper ends)."""
    d = capture_limits.DEFAULTS
    return d["photosRange"][1] + d["videoFramesRange"][1] + d["anchorImages"]


def apply(env=None):
    env = os.environ if env is None else env
    settings.profile_override = ""
    if not env.get("MAX_PHOTOS"):
        settings.max_photos = max(settings.max_photos, server_max_images())
    if not env.get("SPLAT_TIMEOUT_S"):
        settings.splat_timeout_s = max(settings.splat_timeout_s,
                                       capture_limits.scaled_s(PREPARE_BASE_TIMEOUT_S, server_max_images(), FLOOR))
