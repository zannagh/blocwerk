# syntax=docker/dockerfile:1
# Hold recognition (Blocwerk.Recognition.Abstractions, Blocwerk.HoldDetection) is restored from GitHub Packages, which
# needs a token even to read. Pass it as a BuildKit secret, never as a build argument:
#   docker buildx build -f docker/Dockerfile --secret id=github_token,env=GITHUB_TOKEN .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /repo

COPY Directory.Packages.props Directory.Build.props global.json .stylecop.json nuget.config ./
COPY src/Blocwerk.Contracts/Blocwerk.Contracts.csproj src/Blocwerk.Contracts/
COPY src/Blocwerk.Core/Blocwerk.Core.csproj src/Blocwerk.Core/
COPY src/Blocwerk.Authentication/Blocwerk.Authentication.csproj src/Blocwerk.Authentication/
COPY src/Blocwerk.Shared.Ui/Blocwerk.Shared.Ui.csproj src/Blocwerk.Shared.Ui/
COPY src/Blocwerk.Client/Blocwerk.Client.csproj src/Blocwerk.Client/
COPY src/Blocwerk.Web/Blocwerk.Web.csproj src/Blocwerk.Web/
RUN --mount=type=secret,id=github_token,required=true \
    NuGetPackageSourceCredentials_zannagh="Username=docker;Password=$(cat /run/secrets/github_token)" \
    dotnet restore src/Blocwerk.Web/Blocwerk.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/Blocwerk.Web/Blocwerk.Web.csproj -c Release -o /app -a $TARGETARCH --no-restore \
    -p:GenerateStaticWebAssetsManifest=true

# Guard: the publish output must carry Blazor's framework script. It comes from a package the SDK only restores
# for projects with .razor files, and the restore above sees csproj files only (RequiresAspNetWebAssets in
# Blocwerk.Web.csproj covers that). Without it the image starts fine and every page stays at "Loading..." (0.5.0).
RUN set -eu; \
    js=/app/wwwroot/_framework/blazor.web.js; \
    if [ ! -f "$js" ]; then echo "guard: $js missing from publish output"; exit 1; fi; \
    if ! grep -q '"Route": *"_framework/blazor.web.js"' /app/Blocwerk.Web.staticwebassets.endpoints.json; then \
      echo "guard: _framework/blazor.web.js missing from the static web assets endpoints manifest"; \
      exit 1; \
    fi; \
    echo "guard: _framework/blazor.web.js published"

# Guard: the publish output must carry the OpenCV native library built for $TARGETARCH.
# This image is cross-built (one builder, both platforms), so a bad runtime package in
# Blocwerk.HoldDetection (zannagh/blocwerk-recognition) would silently ship the wrong architecture — the app would
# still start and only throw (DllNotFoundException) once the big-wall hold matcher runs.
# Bytes 18-19 of the ELF header are e_machine: 3e00 = x86-64, b700 = AArch64 (little endian).
RUN set -eu; \
    so=/app/libOpenCvSharpExtern.so; \
    if [ ! -f "$so" ]; then echo "guard: $so missing from publish output"; exit 1; fi; \
    case "$TARGETARCH" in \
      amd64) expected=3e00 ;; \
      arm64) expected=b700 ;; \
      *) echo "guard: unhandled TARGETARCH '$TARGETARCH'"; exit 1 ;; \
    esac; \
    actual=$(od -An -tx1 -j18 -N2 "$so" | tr -d ' \n'); \
    if [ "$actual" != "$expected" ]; then \
      echo "guard: libOpenCvSharpExtern.so is ELF machine $actual, expected $expected for $TARGETARCH"; \
      exit 1; \
    fi; \
    echo "guard: libOpenCvSharpExtern.so matches $TARGETARCH"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg is used to scale oversized beta clips down toward the target size (see IVideoTranscoder).
# heif-convert (libheif-examples + the libde265 decoder plugin) turns HEIC capture photos into JPEGs
# (see HeifCapturePhotoConverter); Ubuntu's ffmpeg 6.1 cannot read HEIC stills.
# The remaining libs are native dependencies of libOpenCvSharpExtern.so, used by the big-wall
# hold overlap matcher. This image is multi-arch and the two OpenCvSharp runtime packages do
# NOT need the same libs, so the list below is the union of both (both are dependencies of the
# Blocwerk.HoldDetection package; a RID-specific restore picks the one per architecture):
#   arm64 (OpenCvSharp4.runtime.linux-arm64) links OpenCV's codecs dynamically and needs
#     libtesseract5, libjpeg-turbo8, libwebp*, libpng16, libtiff6 (plus ffmpeg's libav*).
#   amd64 (OpenCvSharp4.official.runtime.linux-x64) statically links those codecs but pulls in
#     the full GTK/pango/cairo-gobject stack plus libatomic1, libX11, libdrm2 and libfreetype6.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libheif-examples libheif-plugin-libde265 \
       libtesseract5 libgtk-3-0t64 libgdk-pixbuf-2.0-0 libcairo2 libglib2.0-0t64 \
       libjpeg-turbo8 libwebp7 libwebpmux3 libwebpdemux2 libpng16-16t64 libtiff6 \
       libatomic1 libx11-6 libatk1.0-0t64 libcairo-gobject2 libdrm2 libfreetype6 \
       libharfbuzz0b libpango-1.0-0 libpangocairo-1.0-0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5050
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 5050

ENTRYPOINT ["dotnet", "Blocwerk.Web.dll"]
