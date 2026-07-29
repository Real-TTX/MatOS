<#
.SYNOPSIS
  Computes the matOS version string and (for release/nightly) increments the
  build counter in version.json.

.PARAMETER Mode
  release  -> <major>.<minor>.<build>-<yyyyMMdd>   (increments build)
  nightly  -> nightly-<build>-<yyyyMMdd>           (increments build)
  local    -> local-<yyyyMMdd>                     (no increment)

.OUTPUTS
  The version string on stdout.
#>
param(
    [ValidateSet('release', 'nightly', 'local')]
    [string]$Mode = 'local'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repoRoot 'version.json'
$v = Get-Content $versionFile -Raw | ConvertFrom-Json
$date = Get-Date -Format 'yyyyMMdd'

switch ($Mode) {
    'release' {
        $v.build++
        ($v | ConvertTo-Json) | Set-Content $versionFile -Encoding utf8
        "$($v.major).$($v.minor).$($v.build)-$date"
    }
    'nightly' {
        $v.build++
        ($v | ConvertTo-Json) | Set-Content $versionFile -Encoding utf8
        "nightly-$($v.build)-$date"
    }
    'local' {
        "local-$date"
    }
}
