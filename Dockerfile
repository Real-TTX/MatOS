# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG APP_VERSION=local
COPY . .
# Log the SDK we actually ended up with, plus arch + free-space, so failing CI
# runs always tell us what mcr.microsoft.com/dotnet/sdk:10.0 resolved to and
# on what platform.
RUN echo "=== DIAG ===" && uname -a && df -h / && dotnet --info && echo "=== /DIAG ==="
RUN dotnet restore src/MatOS.Web/MatOS.Web.csproj
# Redirect publish output into a file so even if the terminal gets truncated
# by buildx, `cat` at the end still shows the tail on failure.
RUN sh -c 'dotnet publish src/MatOS.Web/MatOS.Web.csproj -c Release -o /app/publish /p:UseAppHost=false /p:InformationalVersion="${APP_VERSION}" > /tmp/publish.log 2>&1; rc=$?; echo "--- publish tail ---"; tail -80 /tmp/publish.log; exit $rc'

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# docker compose CLI (standalone v2 binary) so matOS can deploy user Compose stacks,
# plus git so matOS can clone/pull app repos (GitOps app sources).
ARG COMPOSE_VERSION=v2.29.7
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git \
 && curl -fSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-x86_64" -o /usr/local/bin/docker-compose \
 && chmod +x /usr/local/bin/docker-compose \
 && apt-get purge -y curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Data directory (JSON configs, users/sessions, DataProtection keys) - mounted as a volume.
RUN mkdir -p /app/data
VOLUME ["/app/data"]

ENV ASPNETCORE_URLS=http://+:8080
ENV MATOS_VERSION=local
ENV MATOS_DATA_DIR=/app/data
EXPOSE 8080

ENTRYPOINT ["dotnet", "MatOS.Web.dll"]
