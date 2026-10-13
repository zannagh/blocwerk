# The Linux environment the detector half of the harness runs in: the .NET SDK plus the native libraries OpenCV
# needs (the same list as docker/Dockerfile). The repository is mounted, not copied, so a code change needs no rebuild.
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       libtesseract5 libgtk-3-0t64 libgdk-pixbuf-2.0-0 libcairo2 libglib2.0-0t64 \
       libjpeg-turbo8 libwebp7 libwebpmux3 libwebpdemux2 libpng16-16t64 libtiff6 \
       libatomic1 libx11-6 libatk1.0-0t64 libcairo-gobject2 libdrm2 libfreetype6 \
       libharfbuzz0b libpango-1.0-0 libpangocairo-1.0-0 ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /repo
