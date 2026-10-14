"""Runtime configuration from the environment (shared fields: computejobs.settings)."""
from computejobs import capture_limits
from computejobs.settings import env_int, settings

# Images per job follow the capture limits (CAPTURE_MAX_PHOTOS + CAPTURE_MAX_VIDEO_FRAMES, computejobs/capture_limits.py);
# a textures request carries every photo at full size (48 MP: 12-22 MB each), streamed to the job dir: 4 GB per request
# and the timeouts below are sized for 200 images and grow with the limits.
IMAGES = capture_limits.geometry_images()
settings.load(work_dir="/tmp/wall-geometry-jobs", max_request_mb=capture_limits.scaled_s(4096, IMAGES), max_photo_mb=40,
              max_photos=IMAGES)
settings.solve_timeout_s = env_int("SOLVE_TIMEOUT_S", capture_limits.scaled_s(1800, IMAGES))
settings.textures_timeout_s = env_int("TEXTURES_TIMEOUT_S", capture_limits.scaled_s(2700, IMAGES))
# total output pixels of one textures job (all facets are held in memory together); 200 MP
settings.textures_max_pixels = env_int("TEXTURES_MAX_MEGAPIXELS", 200) * 1_000_000
# memory the multi-view blend may take: its sample slots ((blendViews + 2) x 7 bytes per output pixel)
# plus the photos' sparse per-cell views (textures.blend_bytes); a bigger job renders single-view instead. 2 GB fits ~36 MP of textures (a wall at 2 mm/px); raise it
# with mmPerPx 1 (4x the pixels) on a machine with the RAM for it.
settings.textures_blend_max_bytes = env_int("TEXTURES_BLEND_MAX_BYTES", 2_000_000_000)
# kind solve-sfm: SfM on a feature reconstruction (splat-prepare's sparse.zip, at most SFM_MAX_SPARSE_MB zipped
# and twice that unpacked; The Attic's 353 images: 37 MB) within SFM_TIMEOUT_S (The Attic: ~25 s).
settings.sfm_timeout_s = env_int("SFM_TIMEOUT_S", capture_limits.scaled_s(900, IMAGES))
settings.sfm_max_sparse_bytes = env_int("SFM_MAX_SPARSE_MB", 256) << 20
