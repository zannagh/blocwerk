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
# JPEG quality (1-100) of those re-encodes: the arrival copy (INGEST_JPEG_QUALITY) and the training images
# the ingest stage writes for COLMAP + Brush, photos and video frames alike (FRAME_JPEG_QUALITY).
settings.ingest_jpeg_quality = min(100, max(1, env_int("INGEST_JPEG_QUALITY", 95)))
settings.frame_jpeg_quality = min(100, max(1, env_int("FRAME_JPEG_QUALITY", 92)))
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
# COLMAP_MAX_FEATURES is the features per image in every case (tuning.feature_budget); 0 = the job's own
# count by photo number (16384 up to 60, 8192 beyond).
settings.colmap_max_features = env_int("COLMAP_MAX_FEATURES", 8192)
settings.colmap_max_matches = env_int("COLMAP_MAX_MATCHES", 8192)
settings.colmap_threads = env_int("COLMAP_THREADS", 4)
# GPU SfM (colmap.py, tuning.gpu_matching_tiers; Dockerfile.cuda: COLMAP built with CUDA): COLMAP_USE_GPU=1
# runs SIFT extraction and matching on the NVIDIA GPU COLMAP_GPU_INDEX (matching fitted to its free VRAM).
# COLMAP_DSP_SIFT / COLMAP_AFFINE_SHAPE: domain-size pooling / affine-covariant SIFT (better matches on
# texture seen at an angle; COLMAP extracts those on the CPU only, GPU or not). COLMAP_MAPPER: incremental
# (mapper) or global (global_mapper, COLMAP 4.x). COLMAP_VOCAB_TREE_IMAGES > 0: after the frame pair list,
# every video frame is also matched with its that-many most similar images from the vocabulary tree
# COLMAP_VOCAB_TREE_PATH (loop closure for walks; 0 = off).
settings.colmap_use_gpu = env_int("COLMAP_USE_GPU", 0) > 0
settings.colmap_gpu_index = env_int("COLMAP_GPU_INDEX", 0)
settings.colmap_dsp_sift = env_int("COLMAP_DSP_SIFT", 1) > 0
settings.colmap_affine_shape = env_int("COLMAP_AFFINE_SHAPE", 1) > 0
settings.colmap_mapper = os.environ.get("COLMAP_MAPPER", "incremental").strip().lower() or "incremental"
settings.colmap_vocab_tree_images = env_int("COLMAP_VOCAB_TREE_IMAGES", 0)
settings.colmap_vocab_tree_path = os.environ.get("COLMAP_VOCAB_TREE_PATH", "")
settings.max_memory_mb = env_int("SPLAT_MAX_MEMORY_MB", 0)
# SPLAT_MIN_MEMORY_MB: floor of Brush's budget (0 = 3 GB; COLMAP keeps the plain budget) for a machine whose "available" memory is low only
# because idle apps sit in it; the high/max profiles need 5-6 GB / 12+ GB (profiles.py, README).
settings.min_memory_mb = env_int("SPLAT_MIN_MEMORY_MB", 0)
settings.max_swap_growth_mb = env_int("SPLAT_MAX_SWAP_GROWTH_MB", 2048)
# Walk-along video frames (photos named vf_*, see frames.py): each frame is matched with its next
# FRAME_NEIGHBOURS frames, and every FRAME_PHOTO_STRIDE-th frame with every photo.
settings.frame_neighbours = env_int("FRAME_NEIGHBOURS", 6)
settings.frame_photo_stride = env_int("FRAME_PHOTO_STRIDE", 4)
# Trainer: brush (Vulkan / Metal, default) or gsplat (CUDA; Dockerfile.cuda, whose trainer Python is
# GSPLAT_PYTHON). SPLAT_PROFILE_OVERRIDE (e.g. `ultra`) replaces every request's options.quality;
# SPLAT_VRAM_MB (0 = nvidia-smi) is the VRAM gsplat plans with. See trainers.py.
settings.splat_trainer = os.environ.get("SPLAT_TRAINER", "brush").strip().lower() or "brush"
settings.gsplat_python = os.environ.get("GSPLAT_PYTHON", "python3")
settings.profile_override = os.environ.get("SPLAT_PROFILE_OVERRIDE", "").strip().lower()
settings.vram_mb = env_int("SPLAT_VRAM_MB", 0)
# GSPLAT_EVAL_EVERY > 0: every that-many-th view (sorted by name) is held out of gsplat's training and
# scored (PSNR / SSIM) at the end: stats.eval* (gsplat_train.py). 0 = off: every view trains.
settings.gsplat_eval_every = max(0, env_int("GSPLAT_EVAL_EVERY", 0))
# The worker's role: "all" (default: every kind, the trainer required) or "cpu" (the server-side half for 3D
# runners: splat-prepare + splat-finish only; no GPU and no trainer needed, kind `splat` is refused).
settings.worker_mode = os.environ.get("SPLAT_WORKER_MODE", "all").strip().lower() or "all"
# kind splat-finish: the largest trained scene (a runner's .ply / .spz) accepted.
settings.max_result_bytes = env_int("SPLAT_MAX_RESULT_MB", 2048) << 20
