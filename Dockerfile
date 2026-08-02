# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG APP_VERSION=local
COPY . .
# Log the SDK we actually ended up with so a failing CI run always tells us what
# tag mcr.microsoft.com/dotnet/sdk:10.0 resolved to on this run.
RUN dotnet --info
RUN dotnet restore src/MatOS.Web/MatOS.Web.csproj
# Single-line publish so any Bash / Buildx line-continuation quirk on the CI
# runner can't split the argument list mid-flight; -v:n keeps output actionable
# without the noise of full diagnostic. Sanitize the informational version — the
# CI computes something like "nightly-42-20260802-0937" which is fine, but strip
# it defensively just in case.
RUN dotnet publish src/MatOS.Web/MatOS.Web.csproj -c Release -o /app/publish -v:n /p:UseAppHost=false /p:InformationalVersion="${APP_VERSION}"

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# docker compose CLI (standalone v2 binary) so matOS can deploy user Compose stacks
ARG COMPOSE_VERSION=v2.29.7
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
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
