# syntax=docker/dockerfile:1.7
# ============================================================================
# NimShare — self-hosted file sharing on Azure
# ============================================================================
# Multi-stage:
#   1. build   — restore + publish the ASP.NET Core 8 app
#   2. runtime — small aspnet:8.0 image, non-root user, /data volume for Sqlite
#
# Target platforms: linux/amd64 (linux/arm64 also supported by the base image)
# ============================================================================

ARG DOTNET_VERSION=8.0

# ---------------------------------------------------------------------------
# Stage 1: build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Copy csproj files first so restore can cache when only source changes.
COPY NimShare.sln ./
COPY src/NimShare.Api/NimShare.Api.csproj src/NimShare.Api/
COPY src/NimShare.Core/NimShare.Core.csproj src/NimShare.Core/
RUN dotnet restore src/NimShare.Api/NimShare.Api.csproj

# Now the source tree.
COPY src/ src/
RUN dotnet publish src/NimShare.Api/NimShare.Api.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Stage 2: runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime

# The aspnet:8.0 image already ships with a non-root "app" user (UID 1654),
# exposed via the $APP_UID build arg. Reuse it — do NOT try to add users;
# groupadd/useradd fail with SIGKILL (exit 9) under buildx here, and any
# custom apt install has been observed to conflict with the base's libs.
ARG APP_UID=1654

WORKDIR /app
COPY --from=build /app ./
RUN chown -R ${APP_UID}:${APP_UID} /app

# App Service reads WEBSITES_PORT; the ASP.NET Core listener follows ASPNETCORE_URLS.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Database__Provider=Sqlite \
    ConnectionStrings__Default="Data Source=/data/nimshare.db;Cache=Shared" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1

# /data is mounted from Azure Files by the App Service azureStorageAccounts config.
VOLUME ["/data"]

USER ${APP_UID}
EXPOSE 8080

ENTRYPOINT ["dotnet", "NimShare.Api.dll"]
