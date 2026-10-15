#requires -Version 7.2
param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$VersionTag)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$file = Get-Item -LiteralPath $SetupPath
$info = $file.VersionInfo
if ($file.Name -cne $version.SetupName -or $file.Length -lt 1MB -or $info.FileVersion.Trim() -ne $version.Numeric -or $info.ProductVersion.Trim() -cne $version.SemVer) { throw 'Installer name/size/version mismatch' }
Write-Host 'Installer name, size and version passed.'
