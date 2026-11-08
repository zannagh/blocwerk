# The recognition worker (src/Blocwerk.Recognition.Worker): a runner for the recognition kinds, always on next to the app.
# Multi-arch like docker/Dockerfile (one amd64 builder cross-builds linux/amd64 and linux/arm64). Models are NOT baked
# in: mount them read-only at /models (RECOGNITION__MODELSDIR). Build from the repo root:
#   docker buildx build -f docker/recognition/Dockerfile --platform linux/amd64,linux/arm64 .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /repo

COPY Directory.Packages.props Directory.Build.props global.json .stylecop.json ./
COPY src/Blocwerk.Recognition.Abstractions/Blocwerk.Recognition.Abstractions.csproj src/Blocwerk.Recognition.Abstractions/
COPY src/Blocwerk.HoldDetection/Blocwerk.HoldDetection.csproj src/Blocwerk.HoldDetection/
COPY src/Blocwerk.Recognition.Worker/Blocwerk.Recognition.Worker.csproj src/Blocwerk.Recognition.Worker/
RUN dotnet restore src/Blocwerk.Recognition.Worker/Blocwerk.Recognition.Worker.csproj -a $TARGETARCH

COPY src/Blocwerk.Recognition.Abstractions/ src/Blocwerk.Recognition.Abstractions/
COPY src/Blocwerk.HoldDetection/ src/Blocwerk.HoldDetection/
COPY src/Blocwerk.Recognition.Worker/ src/Blocwerk.Recognition.Worker/
RUN dotnet publish src/Blocwerk.Recognition.Worker/Blocwerk.Recognition.Worker.csproj -c Release -o /app -a $TARGETARCH \
    && rm -f /app/models/*.onnx

# Guard (as in docker/Dockerfile): the OpenCV native library must match $TARGETARCH (ELF e_machine at bytes 18-19).
RUN set -eu; \
    so=/app/libOpenCvSharpExtern.so; \
    [ -f "$so" ] || { echo "guard: $so missing from publish output"; exit 1; }; \
    case "$TARGETARCH" in amd64) expected=3e00 ;; arm64) expected=b700 ;; *) echo "guard: unhandled TARGETARCH '$TARGETARCH'"; exit 1 ;; esac; \
    actual=$(od -An -tx1 -j18 -N2 "$so" | tr -d ' \n'); \
    [ "$actual" = "$expected" ] || { echo "guard: libOpenCvSharpExtern.so is ELF machine $actual, expected $expected"; exit 1; }

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Native dependencies of libOpenCvSharpExtern.so, the union of both architectures (docker/Dockerfile explains which
# package needs which): arm64 links OpenCV's codecs dynamically (libtesseract5, libjpeg-turbo8, libwebp*, libpng16,
# libtiff6 plus ffmpeg's libav*), amd64 pulls in GTK/pango/cairo-gobject, libatomic1, libX11, libdrm2, libfreetype6.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
       libtesseract5 libgtk-3-0t64 libgdk-pixbuf-2.0-0 libcairo2 libglib2.0-0t64 \
       libjpeg-turbo8 libwebp7 libwebpmux3 libwebpdemux2 libpng16-16t64 libtiff6 \
       libatomic1 libx11-6 libatk1.0-0t64 libcairo-gobject2 libdrm2 libfreetype6 \
       libharfbuzz0b libpango-1.0-0 libpangocairo-1.0-0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Design §4: workstation GC that gives memory back. The model cache (models fetched by hash when /models lacks them)
# lives in /tmp; the pod mounts its models read-only.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5 \
    RECOGNITION__MODELSDIR=/models \
    RECOGNITION__MODELCACHEDIR=/tmp/blocwerk-models
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Blocwerk.Recognition.Worker.dll"]
