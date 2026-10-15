#requires -Version 7.2
param([Parameter(Mandatory)][string]$VersionTag, [Parameter(Mandatory)][string]$ExpectedCommit, [Parameter(Mandatory)][string]$AssetDirectory, [Parameter(Mandatory)][string]$Repository)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
function Invoke-Gh([string[]]$Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: $($Arguments[0])" }
    $result
}
& (Join-Path $PSScriptRoot 'Test-ReleaseTag.ps1') -VersionTag $VersionTag -ExpectedCommit $ExpectedCommit
& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $AssetDirectory -VersionTag $VersionTag
# Listing is fail-closed: authorization/network errors are never treated as 'not found'.
$pages = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases?per_page=100", '--paginate', '--slurp')) -join "`n" | ConvertFrom-Json
foreach ($page in $pages) {
    foreach ($release in $page) {
        if ($release.tag_name -ceq $VersionTag) { throw 'This tag already has a Release or Draft. Inspect it manually; no assets were changed.' }
    }
}
$notes = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\docs\Release-Notes.md') -Raw
$notes = $notes.Replace('{{TAG}}', $VersionTag).Replace('{{COMMIT}}', $ExpectedCommit)
$notesPath = Join-Path $AssetDirectory 'release-notes.md'
try {
    [IO.File]::WriteAllText($notesPath, $notes, [Text.UTF8Encoding]::new($false))
    $arguments = @('release', 'create', $VersionTag, '--repo', $Repository, '--verify-tag', '--target', $ExpectedCommit, '--draft', '--latest=false', '--title', "SeleVox $VersionTag", '--notes-file', $notesPath)
    if ($version.Prerelease) { $arguments += '--prerelease' }
    Invoke-Gh -Arguments $arguments | Out-Null
} finally { if (Test-Path -LiteralPath $notesPath) { Remove-Item -LiteralPath $notesPath } }
# Upload without --clobber; any failure leaves a non-public draft for inspection.
Invoke-Gh -Arguments @('release', 'upload', $VersionTag, (Join-Path $AssetDirectory $version.ZipName), (Join-Path $AssetDirectory 'SHA256SUMS.txt'), '--repo', $Repository) | Out-Null
$release = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases/tags/$VersionTag")) -join "`n" | ConvertFrom-Json
if (-not $release.draft -or $release.tag_name -cne $VersionTag -or $release.prerelease -ne $version.Prerelease -or $release.target_commitish -cne $ExpectedCommit -or $release.assets.Count -ne 2) { throw 'Draft identity/assets mismatch; refusing to publish.' }
foreach ($name in @($version.ZipName, 'SHA256SUMS.txt')) {
    $asset = @($release.assets | Where-Object { $_.name -ceq $name })
    $file = Get-Item -LiteralPath (Join-Path $AssetDirectory $name)
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or $asset[0].size -ne $file.Length -or $asset[0].digest -cne $digest) { throw 'Uploaded asset name/size/SHA-256 mismatch; draft remains private.' }
}
# Recheck remote identity immediately before publication.
& (Join-Path $PSScriptRoot 'Test-ReleaseTag.ps1') -VersionTag $VersionTag -ExpectedCommit $ExpectedCommit
$latest = if ($version.Prerelease) { '--latest=false' } else { '--latest' }
Invoke-Gh -Arguments @('release', 'edit', $VersionTag, '--repo', $Repository, '--verify-tag', '--draft=false', $latest) | Out-Null
