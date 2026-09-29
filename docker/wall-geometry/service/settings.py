"""Runtime configuration from the environment (shared fields: computejobs.settings)."""
from computejobs.settings import env_int, settings

# 200 photos per job (the app's CAPTURE__MAXPHOTOS default); a textures request carries every one of them at full
# size (48 MP: 12-22 MB each), streamed to the job dir, hence 4 GB per request and the longer timeouts.
settings.load(work_dir="/tmp/wall-geometry-jobs", max_request_mb=4096, max_photo_mb=40, max_photos=200)
settings.solve_timeout_s = env_int("SOLVE_TIMEOUT_S", 1800)
settings.textures_timeout_s = env_int("TEXTURES_TIMEOUT_S", 2700)
# total output pixels of one textures job (all facets are held in memory together); 200 MP
settings.textures_max_pixels = env_int("TEXTURES_MAX_MEGAPIXELS", 200) * 1_000_000
# memory the multi-view blend's sample slots may take ((blendViews + 2) x 7 bytes per output pixel);
# a bigger job renders single-view instead. 2 GB fits ~36 MP of textures (a wall at 2 mm/px); raise it
# with mmPerPx 1 (4x the pixels) on a machine with the RAM for it.
settings.textures_blend_max_bytes = env_int("TEXTURES_BLEND_MAX_BYTES", 2_000_000_000)
# kind solve-sfm: SfM on a feature reconstruction (splat-prepare's sparse.zip, at most SFM_MAX_SPARSE_MB zipped
# and twice that unpacked; The Attic's 353 images: 37 MB) within SFM_TIMEOUT_S (The Attic: ~25 s).
settings.sfm_timeout_s = env_int("SFM_TIMEOUT_S", 900)
settings.sfm_max_sparse_bytes = env_int("SFM_MAX_SPARSE_MB", 256) << 20
