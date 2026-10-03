"""MCMC position noise for the gsplat trainer (no torch or worker imports: the trainer's own Python loads it)."""


def mcmc_noise_lr(scene_scale):
    """gsplat tunes MCMC's position noise (5e5) for a unit-normalised scene; our world space keeps COLMAP's scale and
    the means' learning rate carries scene_scale, so the noise grows with scene_scale squared: undone here (at scale
    6.6 the untuned noise made the near-transparent splats random-walk out of the scene and training collapsed)."""
    return 5e5 / max(scene_scale, 1e-6) ** 2
