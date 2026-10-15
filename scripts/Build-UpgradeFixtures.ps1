#requires -Version 7.2
# Local build only; no Git refs or releases. Fault variants are CI-only.
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\upgrade-fixtures'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Upgrade fixture output must not exist' }
New-Item -ItemType Directory -Path $root | Out-Null
$tags = @('v0.0.1', 'v0.0.2-rc.1', 'v0.0.2-rc.2', 'v0.0.2', 'v0.0.3-rc.1')
foreach ($tag in $tags) {
    $source = Join-Path $root "$tag/publish"
    & (Join-Path $PSScriptRoot 'Publish-Windows.ps1') -VersionTag $tag -OutputDirectory $source
    $components = Join-Path $source 'UpgradeFixture'
    New-Item -ItemType Directory -Path $components | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'SeleVox.Core.dll') -Destination (Join-Path $components 'current-component.dll')
    if ($tag -in @('v0.0.1', 'v0.0.2-rc.1')) { Copy-Item -LiteralPath (Join-Path $source 'SeleVox.Core.dll') -Destination (Join-Path $components 'obsolete-component.dll') }
    if ($tag -eq 'v0.0.2') { Copy-Item -LiteralPath (Join-Path $source 'SeleVox.Core.dll') -Destination (Join-Path $components 'stable-only.dll') }
    & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -VersionTag $tag -PublishDirectory $source -OutputDirectory (Join-Path $root "$tag/setup")
}
foreach ($fault in @('Fail', 'Interrupt', 'Verify')) {
    & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -VersionTag 'v0.0.3-rc.1' -PublishDirectory (Join-Path $root 'v0.0.3-rc.1/publish') -OutputDirectory (Join-Path $root $fault) -TestFault $fault
}
Write-Host 'Five actual versioned Setup payloads and three isolated fault variants built.'
