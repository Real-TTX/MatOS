# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG APP_VERSION=local
COPY . .
RUN dotnet restore src/MatOS.Web/MatOS.Web.csproj
RUN dotnet publish src/MatOS.Web/MatOS.Web.csproj -c Release -o /app/publish \
    /p:UseAppHost=false /p:InformationalVersion=${APP_VERSION}

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Data directory (JSON configs, users/sessions, DataProtection keys) - mounted as a volume.
RUN mkdir -p /app/data
VOLUME ["/app/data"]

ENV ASPNETCORE_URLS=http://+:8080
ENV MATOS_VERSION=local
ENV MATOS_DATA_DIR=/app/data
EXPOSE 8080

ENTRYPOINT ["dotnet", "MatOS.Web.dll"]
