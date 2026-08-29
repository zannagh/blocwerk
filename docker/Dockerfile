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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg is used to scale oversized beta clips down toward the target size (see IVideoTranscoder).
# The remaining libs are native dependencies of OpenCvSharp4.runtime.linux-arm64
# (libOpenCvSharpExtern.so) used by the big-wall hold overlap matcher.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
       libtesseract5 libgtk-3-0t64 libgdk-pixbuf-2.0-0 libcairo2 libglib2.0-0t64 \
       libjpeg-turbo8 libwebp7 libwebpmux3 libwebpdemux2 libpng16-16t64 libtiff6 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5050
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 5050

ENTRYPOINT ["dotnet", "Blocwerk.Web.dll"]
