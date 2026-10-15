# Image unique : serveur (API et interface web), worker et lanceur. Contexte de construction : racine du dépôt.
# Le lanceur démarre et arrête le serveur et l'agent selon le mode du nœud (architecture 0036).
ARG DOTNET_VERSION=10.0

FROM node:24-alpine AS frontend-build
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /source/backend

COPY backend/src/Encoding.Api/Encoding.Api.csproj ./src/Encoding.Api/
COPY backend/src/Encoding.NodeAgent/Encoding.NodeAgent.csproj ./src/Encoding.NodeAgent/
COPY backend/src/Encoding.Launcher/Encoding.Launcher.csproj ./src/Encoding.Launcher/
COPY backend/src/Encoding.Contracts/Encoding.Contracts.csproj ./src/Encoding.Contracts/
COPY backend/src/Encoding.Core/Encoding.Core.csproj ./src/Encoding.Core/
RUN dotnet restore ./src/Encoding.Api/Encoding.Api.csproj \
 && dotnet restore ./src/Encoding.NodeAgent/Encoding.NodeAgent.csproj \
 && dotnet restore ./src/Encoding.Launcher/Encoding.Launcher.csproj
COPY backend/migrations ./migrations
COPY backend/src ./src
RUN dotnet publish ./src/Encoding.Api/Encoding.Api.csproj --configuration Release --output /app/api --no-restore \
 && dotnet publish ./src/Encoding.NodeAgent/Encoding.NodeAgent.csproj --configuration Release --output /app/agent --no-restore \
 && dotnet publish ./src/Encoding.Launcher/Encoding.Launcher.csproj --configuration Release --output /app/launcher --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg mkvtoolnix mediainfo imagemagick nodejs python3 && rm -rf /var/lib/apt/lists/*
# Plugins d'intégration livrés avec le worker (processus Python séparés, voir backend/plugins/README.md).
# Le manifeste copié est le JSON que le serveur charge : une seule définition des composants.
COPY backend/plugins/sdk/encoding_plugin_sdk.py backend/plugins/arr/plugin.py /opt/encoding/plugins/arr/
COPY backend/src/Encoding.Api/Flows/Catalog/sonarr.json backend/src/Encoding.Api/Flows/Catalog/radarr.json /opt/encoding/plugins/arr/manifests/
COPY backend/plugins/sdk/encoding_plugin_sdk.py backend/plugins/plex/plugin.py /opt/encoding/plugins/plex/
COPY backend/src/Encoding.Api/Flows/Catalog/plex.json /opt/encoding/plugins/plex/manifests/
COPY backend/plugins/sdk/encoding_plugin_sdk.py backend/plugins/jellyfin/plugin.py /opt/encoding/plugins/jellyfin/
COPY backend/src/Encoding.Api/Flows/Catalog/jellyfin.json /opt/encoding/plugins/jellyfin/manifests/
WORKDIR /app
COPY --from=build /app/api ./api
COPY --from=build /app/agent ./agent
COPY --from=build /app/launcher ./launcher
COPY --from=frontend-build /frontend/dist/frontend/browser ./api/wwwroot
# Mode initial du nœud : serveur et worker. Un mode enregistré dans le dossier de données le remplace.
ENV Node__Roles=server,worker \
    Node__DataDirectory=/var/lib/encoding-node \
    Logging__Console__FormatterName=json \
    Logging__Console__FormatterOptions__UseUtcTimestamp=true \
    Logging__Console__FormatterOptions__IncludeScopes=true
# 8080 : API et interface web ; 8081 : console du worker. Le contrôle du lanceur (8082) reste local au conteneur.
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "/app/launcher/Encoding.Launcher.dll"]
