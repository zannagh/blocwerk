# Blocwerk wall-geometry service. Build context: the REPO ROOT (the shared protocol package
# docker/compute-jobs-py is copied in; the solver package lives here and tools/glyph/geometry/solve.py
# only imports it, so there is ONE copy of each source). Context filter: Dockerfile.dockerignore.
#   docker build -f docker/wall-geometry/Dockerfile -t blocwerk-geometry .               # runtime image
#   docker build -f docker/wall-geometry/Dockerfile --target test .                      # runs the tests
FROM python:3.12-slim AS base
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
WORKDIR /app
# Hash-locked (requirements.lock is generated from requirements.txt, see its header): a tampered or
# re-uploaded wheel fails the install instead of landing in the image.
COPY docker/wall-geometry/requirements.lock .
RUN pip install --require-hashes --no-deps -r requirements.lock
COPY docker/compute-jobs-py/computejobs/ computejobs/
COPY docker/wall-geometry/wallgeometry/ wallgeometry/
COPY docker/wall-geometry/service/ service/

FROM base AS test
COPY docker/wall-geometry/requirements-dev.lock .
RUN pip install --require-hashes --no-deps -r requirements-dev.lock
COPY docker/wall-geometry/tests/ tests/
COPY docker/compute-jobs-py/tests/ tests-shared/
RUN python -m pytest -q -p no:cacheprovider tests tests-shared

FROM base AS runtime
ARG GIT_SHA=unknown
ENV GIT_SHA=${GIT_SHA} WORK_DIR=/tmp/wall-geometry-jobs HOST=0.0.0.0 PORT=8000 \
    OPENCV_IO_MAX_IMAGE_PIXELS=100000000
RUN useradd --system --uid 10001 --no-create-home --home-dir /app app \
    && mkdir -p /tmp/wall-geometry-jobs && chown app /tmp/wall-geometry-jobs
USER app
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4).status == 200 else 1)"
# ONE process on purpose: the job queue is in memory (computejobs/jobs.py). Started through
# computejobs.serve, which refuses to listen on 0.0.0.0 without COMPUTE_API_KEY (exit 2) unless
# ALLOW_OPEN_BIND=1 (only for a port reachable on a private network).
CMD ["python", "-m", "service"]
