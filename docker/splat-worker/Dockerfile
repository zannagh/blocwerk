# Blocwerk splat worker for LINUX GPU hosts (Vulkan). Build context: the REPO ROOT (the shared protocol
# package docker/compute-jobs-py is copied in). Context filter: Dockerfile.dockerignore.
#   docker build -f docker/splat-worker/Dockerfile -t blocwerk-splat-worker .            # runtime image
#   docker build -f docker/splat-worker/Dockerfile --target test .                       # runs the tests
#   docker run --gpus all -p 8100:8100 -e COMPUTE_API_KEY=... blocwerk-splat-worker     # NVIDIA
#   docker run --device /dev/dri -p 8100:8100 -e COMPUTE_API_KEY=... blocwerk-splat-worker  # AMD / Intel
# The same image, two more roles (README "3D runner"):
#   3D runner (Brush on Vulkan; pulls training from a Blocwerk server; no ports):
#     docker run -d --name blocwerk-runner --restart unless-stopped --device /dev/dri --env-file runner.env \
#       blocwerk-splat-worker python -m splatworker.gpurunner --server https://blocwerk.app
#   CPU half next to the app (splat-prepare + splat-finish only, no GPU): -e SPLAT_WORKER_MODE=cpu
#
# linux/amd64 only: Brush publishes no Linux arm64 build. On macOS use run-native.sh instead (Docker
# Desktop's VM has no GPU; see README).
FROM ubuntu:24.04 AS base
ARG DEBIAN_FRONTEND=noninteractive
# Pinned tool versions. COLMAP comes from Ubuntu 24.04 (3.9.1, CPU SIFT is all we use); Brush from its
# GitHub release, checked against the release's published sha256.
ARG BRUSH_VERSION=v0.3.0
ARG BRUSH_SHA256=4f0f9a8785d1951c62df26aae247c02c5bba32b00f40b06df4e1c9b867399e20
RUN apt-get update && apt-get install -y --no-install-recommends \
        colmap \
        python3 python3-venv \
        libvulkan1 mesa-vulkan-drivers vulkan-tools \
        ca-certificates curl xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL -o /tmp/brush.tar.xz \
        "https://github.com/ArthurBrussee/brush/releases/download/${BRUSH_VERSION}/brush-app-x86_64-unknown-linux-gnu.tar.xz" \
    && echo "${BRUSH_SHA256}  /tmp/brush.tar.xz" | sha256sum -c - \
    && mkdir -p /opt/brush && tar -xJf /tmp/brush.tar.xz -C /opt/brush --strip-components=1 \
    && rm /tmp/brush.tar.xz && /opt/brush/brush_app --version
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PATH=/opt/venv/bin:$PATH BRUSH_BIN=/opt/brush/brush_app COLMAP_BIN=/usr/bin/colmap
WORKDIR /app
# Hash-locked (requirements.lock is generated from requirements.txt, see its header): a tampered or
# re-uploaded wheel fails the install instead of landing in the image.
COPY docker/splat-worker/requirements.lock .
RUN python3 -m venv /opt/venv && pip install --require-hashes --no-deps -r requirements.lock
COPY docker/compute-jobs-py/computejobs/ computejobs/
COPY docker/splat-worker/splatworker/ splatworker/

FROM base AS test
COPY docker/splat-worker/requirements-dev.lock .
RUN pip install --require-hashes --no-deps -r requirements-dev.lock
COPY docker/splat-worker/tests/ tests/
COPY docker/compute-jobs-py/tests/ tests-shared/
RUN python -m pytest -q -p no:cacheprovider tests tests-shared

FROM base AS runtime
ARG GIT_SHA=unknown
# NVIDIA container toolkit: `graphics` makes it mount the driver's Vulkan ICD into the container.
ENV GIT_SHA=${GIT_SHA} HOST=0.0.0.0 PORT=8100 WORK_DIR=/var/lib/splat-worker/jobs \
    BRUSH_CACHE_DIR=/var/lib/splat-worker/brush-cache XDG_CACHE_HOME=/var/lib/splat-worker/brush-cache/xdg \
    NVIDIA_VISIBLE_DEVICES=all NVIDIA_DRIVER_CAPABILITIES=compute,graphics,utility
RUN useradd --system --uid 10001 --no-create-home --home-dir /app app \
    && mkdir -p /var/lib/splat-worker/jobs /var/lib/splat-worker/brush-cache \
    && chown -R app /var/lib/splat-worker
USER app
EXPOSE 8100
# the worker's /health, or for a 3D runner its liveness file (splatworker/gpurunner/health.py)
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["python", "-m", "splatworker.gpurunner.health"]
# ONE process on purpose (in-memory queue). Refuses to start on 0.0.0.0 without COMPUTE_API_KEY
# unless ALLOW_OPEN_BIND=1 (only for a port reachable on a private network).
CMD ["python", "-m", "splatworker"]
