# Wall-stitch sidecar: HTTP wrapper around the vendored Python image pipeline.
# Build from this directory:  docker build -t blocwerk-wall-stitch:local docker/wall-stitch
# Pinned to bookworm: trixie renamed several of the runtime libraries below (the t64
# transition), and the pinned OpenCV/SciPy wheels are manylinux_2_28, which bookworm's
# glibc 2.36 satisfies.
FROM python:3.12-slim-bookworm AS runtime

# The headless OpenCV wheel bundles its own image codecs, so this is only the C++/OpenMP
# runtime it links against, plus glib and curl for the health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libgomp1 \
        libglib2.0-0 \
        curl \
    && rm -rf /var/lib/apt/lists/*

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1

WORKDIR /srv

COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

# The pipeline is vendored into the image (see README) so the container is
# self-contained: nothing is mounted from a developer's machine at run time.
COPY pipeline/ /opt/pipeline/
COPY app/ /srv/app/

# Non-root. The job volume is chowned here so the named volume inherits the ownership.
RUN useradd --system --create-home --uid 10001 stitch \
    && mkdir -p /data/jobs /opt/models \
    && chown -R stitch:stitch /data /opt/pipeline /srv

USER stitch

ENV WALLSTITCH_DATA_DIR=/data/jobs \
    WALLSTITCH_PIPELINE_DIR=/opt/pipeline \
    WALLSTITCH_ONNX_MODEL=/opt/models/climbingcrux.onnx

EXPOSE 8080
VOLUME ["/data/jobs"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

# One worker process: the pool inside the app is what bounds concurrency, and a second
# uvicorn worker would give each its own in-memory queue.
CMD ["uvicorn", "app.asgi:app", "--host", "0.0.0.0", "--port", "8080", "--workers", "1"]
