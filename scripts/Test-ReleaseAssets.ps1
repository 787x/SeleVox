#requires -Version 7.2
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$VersionTag)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$files = @(Get-ChildItem -LiteralPath $Directory -Force)
if ($files.Count -ne 2 -or @($files | Where-Object { $_.PSIsContainer -or $_.Name -cnotin @($version.ZipName, 'SHA256SUMS.txt') }).Count) { throw 'Expected exactly ZIP and SHA256SUMS.txt.' }
$zip = Join-Path $Directory $version.ZipName
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sum = [IO.File]::ReadAllText((Join-Path $Directory 'SHA256SUMS.txt'))
if ($sum -cne "$hash  $($version.ZipName)`n") { throw 'SHA256SUMS does not match final ZIP bytes/name.' }
Write-Host 'Release assets and SHA-256 passed.'
