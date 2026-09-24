"""The NVIDIA GPU's memory (for the gsplat trainer): `nvidia-smi`, which the NVIDIA container toolkit
mounts with the `utility` capability. SPLAT_VRAM_MB overrides it (free = total = that value), e.g.
to share a GPU with something else. None when there is no NVIDIA GPU to be seen."""
import subprocess

from computejobs.child import tool_env

from .settings import settings


def parse_nvidia_smi(text):
    """[{"name", "totalMb", "freeMb"}] from nvidia-smi's
    `--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits`."""
    gpus = []
    for line in text.strip().splitlines():
        parts = [p.strip() for p in line.rsplit(",", 2)]
        if len(parts) == 3 and parts[1].isdigit() and parts[2].isdigit():
            gpus.append({"name": parts[0], "totalMb": int(parts[1]), "freeMb": int(parts[2])})
    return gpus


def vram():
    """{"name", "totalMb", "freeMb"} of the first visible NVIDIA GPU, or None."""
    if settings.vram_mb > 0:
        return {"name": "SPLAT_VRAM_MB", "totalMb": settings.vram_mb, "freeMb": settings.vram_mb}
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total,memory.free",
                              "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=30,
                             env=tool_env()).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    gpus = parse_nvidia_smi(out)
    return gpus[0] if gpus else None
