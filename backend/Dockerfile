ARG DOTNET_VERSION=10.0

FROM node:24-alpine AS frontend-build
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /source

COPY backend/src/Encoding.Api/Encoding.Api.csproj ./src/Encoding.Api/
COPY backend/src/Encoding.Contracts/Encoding.Contracts.csproj ./src/Encoding.Contracts/
RUN dotnet restore ./src/Encoding.Api/Encoding.Api.csproj
COPY backend/migrations ./migrations
COPY backend/src/Encoding.Api ./src/Encoding.Api
COPY backend/src/Encoding.Contracts ./src/Encoding.Contracts
RUN dotnet publish ./src/Encoding.Api/Encoding.Api.csproj --configuration Release --output /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=frontend-build /frontend/dist/frontend/browser ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Encoding.Api.dll"]
