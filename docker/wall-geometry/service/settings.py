"""Runtime configuration from the environment (shared fields: computejobs.settings)."""
from computejobs.settings import env_int, settings

settings.load(work_dir="/tmp/wall-geometry-jobs", max_request_mb=400, max_photo_mb=40, max_photos=60)
settings.solve_timeout_s = env_int("SOLVE_TIMEOUT_S", 600)
settings.textures_timeout_s = env_int("TEXTURES_TIMEOUT_S", 900)
# total output pixels of one textures job (all facets are held in memory together); 200 MP
settings.textures_max_pixels = env_int("TEXTURES_MAX_MEGAPIXELS", 200) * 1_000_000
# memory the multi-view blend's sample slots may take ((blendViews + 2) x 7 bytes per output pixel);
# a bigger job renders single-view instead. 2 GB fits ~36 MP of textures (a wall at 2 mm/px); raise it
# with mmPerPx 1 (4x the pixels) on a machine with the RAM for it.
settings.textures_blend_max_bytes = env_int("TEXTURES_BLEND_MAX_BYTES", 2_000_000_000)
