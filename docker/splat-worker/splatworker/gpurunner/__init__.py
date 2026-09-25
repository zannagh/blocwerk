"""3D runner: trains Gaussian splats on this machine's GPU for a Blocwerk server.

The runner PULLS work (no inbound ports, works behind NAT): it says hello with its capabilities
(caps.py), long-polls for a job, downloads the job's training bundle (bundle.py, resumable), trains it
through the worker's own trainer path (train.py: gsplat on CUDA with the wall zones, or Brush), and
uploads only the trained scene (a slim .ply, gzip). The key (bwr_...) comes from the BWR_KEY environment
variable, is popped from the environment at start and never logged.

    BWR_KEY=bwr_... python -m splatworker.gpurunner --server https://blocwerk.app

Keep this module free of imports: the container health check (gpurunner.health) loads the package.
"""
