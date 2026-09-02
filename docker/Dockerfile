FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /repo

COPY Directory.Packages.props Directory.Build.props global.json .stylecop.json ./
COPY src/Blocwerk.Core/Blocwerk.Core.csproj src/Blocwerk.Core/
COPY src/Blocwerk.Authentication/Blocwerk.Authentication.csproj src/Blocwerk.Authentication/
COPY src/Blocwerk.HoldDetection/Blocwerk.HoldDetection.csproj src/Blocwerk.HoldDetection/
COPY src/Blocwerk.Web/Blocwerk.Web.csproj src/Blocwerk.Web/
RUN dotnet restore src/Blocwerk.Web/Blocwerk.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/Blocwerk.Web/Blocwerk.Web.csproj -c Release -o /app -a $TARGETARCH \
    -p:GenerateStaticWebAssetsManifest=true

# Guard: the publish output must carry the OpenCV native library built for $TARGETARCH.
# This image is cross-built (one builder, both platforms), so a bad MSBuild condition in
# Blocwerk.HoldDetection.csproj would silently ship the wrong architecture — the app would
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
# The remaining libs are native dependencies of libOpenCvSharpExtern.so, used by the big-wall
# hold overlap matcher. This image is multi-arch and the two OpenCvSharp runtime packages do
# NOT need the same libs, so the list below is the union of both (see the csproj for which
# package each architecture gets):
#   arm64 (OpenCvSharp4.runtime.linux-arm64) links OpenCV's codecs dynamically and needs
#     libtesseract5, libjpeg-turbo8, libwebp*, libpng16, libtiff6 (plus ffmpeg's libav*).
#   amd64 (OpenCvSharp4.official.runtime.linux-x64) statically links those codecs but pulls in
#     the full GTK/pango/cairo-gobject stack plus libatomic1, libX11, libdrm2 and libfreetype6.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
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
