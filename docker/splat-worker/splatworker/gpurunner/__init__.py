"""3D runner: trains Gaussian splats on this machine's GPU for a Blocwerk server.

The runner PULLS work (no inbound ports, works behind NAT): it says hello with its capabilities,
long-polls for a job, downloads the job's training bundle (bundle.py), trains it with Brush under the
worker's memory guard and quality profiles (training.py), and uploads only the trained scene. The key
(bwr_...) comes from the BWR_KEY environment variable and is never logged.

    BWR_KEY=bwr_... python -m splatworker.gpurunner --server https://blocwerk.app
"""
