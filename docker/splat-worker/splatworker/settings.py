"""Runtime configuration from the environment (shared fields: computejobs.settings)."""
import os

from computejobs.settings import env_int, settings

settings.load(work_dir=os.path.join(os.environ.get("TMPDIR", "/tmp"), "splat-jobs"), max_request_mb=4096,
              max_photo_mb=40, max_photos=400, max_queued=4, result_ttl_s=3600)
settings.brush_bin = os.environ.get("BRUSH_BIN", "brush_app")
settings.colmap_bin = os.environ.get("COLMAP_BIN", "colmap")
settings.splat_timeout_s = env_int("SPLAT_TIMEOUT_S", 4 * 3600)
settings.min_photos = env_int("MIN_PHOTOS", 3)
# Photos are re-encoded (metadata-free) on arrival at most this long edge; the job then downscales
# them to the quality profile's edges (profiles.py).
settings.ingest_max_edge = env_int("INGEST_MAX_EDGE", 4096)
# Brush keeps its GPU kernel autotune cache in ./target under its working directory: share it.
settings.brush_cache_dir = os.environ.get("BRUSH_CACHE_DIR", os.path.join(settings.work_dir, "_brush-cache"))
# Resource caps so one job cannot swamp the machine (a 14-photo job once took COLMAP to 25 GB):
# COLMAP's SIFT input edge, features per image, matches per pair and threads (upper bounds; the job
# lowers threads / drops guided matching to fit its memory budget, see resources.py + tuning.py).
# SPLAT_MAX_MEMORY_MB: hard upper bound on that budget (0 = unset: the budget comes from the
# machine alone); every COLMAP/Brush run is killed above the budget (footprint watchdog on every OS,
# RLIMIT_AS for COLMAP on Linux), and also once the system's swap grew by SPLAT_MAX_SWAP_GROWTH_MB
# while it ran (0 = no swap check).
settings.colmap_max_image_size = env_int("COLMAP_MAX_IMAGE_SIZE", 2400)
settings.colmap_max_features = env_int("COLMAP_MAX_FEATURES", 8192)
settings.colmap_max_matches = env_int("COLMAP_MAX_MATCHES", 8192)
settings.colmap_threads = env_int("COLMAP_THREADS", 4)
settings.max_memory_mb = env_int("SPLAT_MAX_MEMORY_MB", 0)
# SPLAT_MIN_MEMORY_MB: floor of Brush's budget (0 = 3 GB; COLMAP keeps the plain budget) for a machine whose "available" memory is low only
# because idle apps sit in it; the high/max profiles need 5-6 GB / 12+ GB (profiles.py, README).
settings.min_memory_mb = env_int("SPLAT_MIN_MEMORY_MB", 0)
settings.max_swap_growth_mb = env_int("SPLAT_MAX_SWAP_GROWTH_MB", 2048)
# Walk-along video frames (photos named vf_*, see frames.py): each frame is matched with its next
# FRAME_NEIGHBOURS frames, and every FRAME_PHOTO_STRIDE-th frame with every photo.
settings.frame_neighbours = env_int("FRAME_NEIGHBOURS", 6)
settings.frame_photo_stride = env_int("FRAME_PHOTO_STRIDE", 4)
