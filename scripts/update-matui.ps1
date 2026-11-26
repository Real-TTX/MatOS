#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Copies the newest MatUi toolkit (the UI of all Mat apps) from a running MatDesign into this app.
.DESCRIPTION
    MatDesign serves the toolkit as an archive (/ui/matui.zip). MatOS carries its own copy in
    src/MatOS.Web/wwwroot/matui (rule CODE-03: copy, do not link), so it works offline. Run this when
    MatDesign has a new release, then rebuild and look at the pages. The version is the first line of matui.css.
.PARAMETER Url
    Address of MatDesign (default http://localhost:8060, the dev stack).
.EXAMPLE
    ./scripts/update-matui.ps1
.EXAMPLE
    ./scripts/update-matui.ps1 -Url https://design.example.com
#>
param(
    [string]$Url = 'http://localhost:8060'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'src/MatOS.Web/wwwroot/matui'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('matui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'matui.zip'
    Invoke-WebRequest -Uri ($Url.TrimEnd('/') + '/ui/matui.zip') -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $work -Force
    $folder = Join-Path $work 'matui'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in 'matui.css', 'matui.js', 'matui.boot.js', 'matui-icons.svg') {
        Copy-Item -Path (Join-Path $folder $file) -Destination (Join-Path $target $file) -Force
    }
    Write-Host ('Toolkit updated: ' + (Get-Content (Join-Path $target 'matui.css') -TotalCount 1)) -ForegroundColor Green
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
