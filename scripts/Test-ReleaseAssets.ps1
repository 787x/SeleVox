#requires -Version 7.2
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$VersionTag)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$files = @(Get-ChildItem -LiteralPath $Directory -Force)
if ($files.Count -ne 3 -or @($files | Where-Object { $_.PSIsContainer -or $_.Name -cnotin @($version.ZipName, $version.SetupName, 'SHA256SUMS.txt') }).Count) { throw 'Expected exactly ZIP, Setup and SHA256SUMS.txt.' }
& (Join-Path $PSScriptRoot 'Test-Installer.ps1') -SetupPath (Join-Path $Directory $version.SetupName) -VersionTag $VersionTag
$expected = foreach ($name in @($version.ZipName, $version.SetupName)) {
    $file = Get-Item -LiteralPath (Join-Path $Directory $name)
    if ($file.Length -le 0) { throw 'Empty release asset' }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name`n"
}
$sum = [IO.File]::ReadAllText((Join-Path $Directory 'SHA256SUMS.txt'))
if ($sum -cne ($expected -join '')) { throw 'SHA256SUMS does not match final ZIP/Setup bytes/names.' }
Write-Host 'Three release assets and SHA-256 passed.'
