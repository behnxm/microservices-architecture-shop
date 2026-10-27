# Multi-stage Dockerfile for Shop Microservices Architecture
ARG PROJECT=src/Shop.Gateway/Shop.Gateway.csproj
ARG DLL_NAME=Shop.Gateway.dll

# 1. Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG PROJECT
WORKDIR /source

# Copy solution-level configuration files
COPY global.json Directory.Build.props Directory.Packages.props Shop.slnx ./

# Copy csproj files for optimal layer caching during restore
COPY src/Shop.Contracts/Shop.Contracts.csproj src/Shop.Contracts/
COPY src/Shop.ServiceDefaults/Shop.ServiceDefaults.csproj src/Shop.ServiceDefaults/
COPY src/Shop.BuildingBlocks.Messaging/Shop.BuildingBlocks.Messaging.csproj src/Shop.BuildingBlocks.Messaging/
COPY src/Shop.Catalog.Api/Shop.Catalog.Api.csproj src/Shop.Catalog.Api/
COPY src/Shop.Ordering.Api/Shop.Ordering.Api.csproj src/Shop.Ordering.Api/
COPY src/Shop.Notifications.Worker/Shop.Notifications.Worker.csproj src/Shop.Notifications.Worker/
COPY src/Shop.Gateway/Shop.Gateway.csproj src/Shop.Gateway/
COPY src/Shop.AppHost/Shop.AppHost.csproj src/Shop.AppHost/

# Restore dependencies
RUN dotnet restore ${PROJECT}

# Copy full source and publish
COPY src/ src/
RUN dotnet publish ${PROJECT} -c Release -o /app/publish --no-restore

# 2. Runtime Stage (Chiseled Ubuntu noble, non-root user)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
ARG DLL_NAME

WORKDIR /app
COPY --from=build /app/publish .

# Run as non-root user (UID 1654)
USER $APP_UID

# Environment defaults
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet"]
CMD ["Shop.Gateway.dll"]
