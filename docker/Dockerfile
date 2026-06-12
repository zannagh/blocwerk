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
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libfontconfig1 \
        libfreetype6 \
        libpng16-16 \
        libjpeg62-turbo \
        libwebp7 \
        libtiff6 \
        libuuid1 \
        libexpat1 \
        libstdc++6 \
        zlib1g \
        fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5050
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 5050

ENTRYPOINT ["dotnet", "Blocwerk.Web.dll"]
