ARG DOTNET_VERSION=10.0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /source

COPY src/Encoding.Api/Encoding.Api.csproj ./src/Encoding.Api/
COPY src/Encoding.Contracts/Encoding.Contracts.csproj ./src/Encoding.Contracts/
RUN dotnet restore ./src/Encoding.Api/Encoding.Api.csproj
COPY migrations ./migrations
COPY src/Encoding.Api ./src/Encoding.Api
COPY src/Encoding.Contracts ./src/Encoding.Contracts
RUN dotnet publish ./src/Encoding.Api/Encoding.Api.csproj --configuration Release --output /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Encoding.Api.dll"]
