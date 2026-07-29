<#
.SYNOPSIS
  Rebuilds the image and (re)deploys the matOS stack.
  This is the live-reload / testing workflow: always rebuild + redeploy.

.PARAMETER Mode
  dev     -> docker-compose.yml          (local build, MATOS_VERSION=local-<date>)
  release -> docker-compose.release.yml  (release build, increments build number)

.EXAMPLE
  ./scripts/deploy.ps1                # dev
  ./scripts/deploy.ps1 -Mode release
#>
param(
    [ValidateSet('dev', 'release')]
    [string]$Mode = 'dev'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ($Mode -eq 'release') {
    $composeFile = 'docker-compose.release.yml'
    $env:MATOS_VERSION = & "$PSScriptRoot/version.ps1" -Mode release
} else {
    $composeFile = 'docker-compose.yml'
    $env:MATOS_VERSION = & "$PSScriptRoot/version.ps1" -Mode local
}

Write-Host "Deploying matOS ($Mode) version $($env:MATOS_VERSION)..." -ForegroundColor Cyan
docker compose -f $composeFile up -d --build
if ($LASTEXITCODE -ne 0) { throw "docker compose failed with exit code $LASTEXITCODE" }

Write-Host "Done. matOS UI: http://localhost:4333" -ForegroundColor Green
