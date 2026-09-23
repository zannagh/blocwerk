"""Runtime configuration from the environment (shared fields: computejobs.settings)."""
from computejobs.settings import env_int, settings

settings.load(work_dir="/tmp/wall-geometry-jobs", max_request_mb=400, max_photo_mb=40, max_photos=60)
settings.solve_timeout_s = env_int("SOLVE_TIMEOUT_S", 600)
settings.textures_timeout_s = env_int("TEXTURES_TIMEOUT_S", 900)
# total output pixels of one textures job (all facets are held in memory together); 200 MP
settings.textures_max_pixels = env_int("TEXTURES_MAX_MEGAPIXELS", 200) * 1_000_000
