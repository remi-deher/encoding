FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg mediainfo && rm -rf /var/lib/apt/lists/*
WORKDIR /source
