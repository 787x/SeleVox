#requires -Version 7.2
param([Parameter(Mandatory)][string]$VersionTag, [Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must be outside publish directory' }
& (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $source
Assert-PublishVersion $source $version
$compiler = & (Join-Path $PSScriptRoot 'Install-InnoSetup.ps1')
if (Test-Path -LiteralPath (Join-Path $output $version.SetupName)) { throw 'Refusing to overwrite installer' }
& $compiler "/DVersion=$($version.SemVer)" "/DNumericVersion=$($version.Numeric)" "/DVersionTag=$VersionTag" "/DPublishDirectory=$source" "/DOutputDirectory=$output" (Join-Path $PSScriptRoot '..\installer\SeleVox.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }
& (Join-Path $PSScriptRoot 'Test-Installer.ps1') -SetupPath (Join-Path $output $version.SetupName) -VersionTag $VersionTag
