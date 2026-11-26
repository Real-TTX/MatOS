# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable (no native host), so the arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatOS.Core/MatOS.Core.csproj", "src/MatOS.Core/"]
COPY ["src/MatOS.Web/MatOS.Web.csproj", "src/MatOS.Web/"]
RUN dotnet restore "src/MatOS.Web/MatOS.Web.csproj"

COPY src src
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatOS.Web/MatOS.Web.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Docker CLI + compose plugin: MatOS installs and manages the apps through the mounted
# /var/run/docker.sock. Installed from Docker's apt repo (multi-arch via dpkg --print-architecture).
# The base image is Ubuntu or Debian depending on the .NET release, so the repo is chosen from /etc/os-release.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
 && install -m 0755 -d /etc/apt/keyrings \
 && . /etc/os-release \
 && curl -fsSL "https://download.docker.com/linux/${ID}/gpg" -o /etc/apt/keyrings/docker.asc \
 && chmod a+r /etc/apt/keyrings/docker.asc \
 && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${ID} ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin \
 && apt-get purge -y curl gnupg \
 && apt-get autoremove -y \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8030 \
    ASPNETCORE_ENVIRONMENT=Production \
    MatOS__DataPath=/data \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1

# Config, database, keys, compose files of the installed apps and backups: one volume.
RUN mkdir -p /data
VOLUME ["/data"]
EXPOSE 8030

COPY --from=build /app/publish .

# The app checks itself, so the image needs no extra tools.
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s --retries=3 \
    CMD ["dotnet", "MatOS.Web.dll", "--healthcheck"]

# Runs as root on purpose: access to the Docker socket is root-equivalent anyway.
ENTRYPOINT ["dotnet", "MatOS.Web.dll"]
