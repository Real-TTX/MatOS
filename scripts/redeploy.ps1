#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rebuilds the MatOS container and redeploys the dev stack (the project's live-reload / test workflow).
.DESCRIPTION
    Always rebuilds the image from the current source and recreates the container; the data volume stays.
    The version of a local build is "local-<builddate>".
.PARAMETER Fresh
    Also deletes the data volume first (clean start, the setup wizard appears again).
.PARAMETER Port
    Host port of the web interface (default 8030).
.EXAMPLE
    ./scripts/redeploy.ps1
.EXAMPLE
    ./scripts/redeploy.ps1 -Fresh -Port 8120
#>
param(
    [switch]$Fresh,
    [int]$Port = 8030
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:MATOS_CHANNEL = 'local'
    $env:MATOS_BUILD_DATE = (Get-Date).ToUniversalTime().ToString('yyyyMMdd')
    $env:MATOS_PORT = "$Port"

    $compose = @('compose', '-f', 'docker-compose.dev.yml')

    # Windows PowerShell 5.1 turns the progress lines docker writes to stderr into errors; judge by the exit code instead.
    $ErrorActionPreference = 'Continue'

    # The dev stack was "mattiverse-dev" until the rename: its containers hold the ports and its network the subnet. They go once; the volume stays
    # (docker-compose.dev.yml uses it).
    docker container inspect mattiverse-dev *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'Removing the old dev stack of Mattiverse (its volume stays) ...' -ForegroundColor Yellow
        docker stop mattiverse-dev mattiverse-dev-sqlite-web
        docker rm mattiverse-dev mattiverse-dev-sqlite-web
        docker network rm mattiverse-dev_default
    }

    if ($Fresh) {
        Write-Host 'Removing the stack and its volume ...' -ForegroundColor Yellow
        docker @compose down -v
    }

    docker @compose up -d --build --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'docker compose failed.' }

    Write-Host ''
    Write-Host "MatOS local-$($env:MATOS_BUILD_DATE) is running on http://localhost:$Port" -ForegroundColor Green
    Write-Host 'Dev login: admin@example.com / Start12345   |   SQLite web: http://localhost:8021' -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
