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
function Get-TagReleases {
    $pages = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases?per_page=100", '--paginate', '--slurp')) -join "`n" | ConvertFrom-Json
    foreach ($page in $pages) {
        foreach ($release in $page) {
            if ($release.tag_name -ceq $VersionTag) { $release }
        }
    }
}
function Assert-DraftIdentity($Release, [long]$ReleaseId) {
    if ($ReleaseId -le 0 -or $Release.id -ne $ReleaseId -or $Release.draft -ne $true -or $Release.tag_name -cne $VersionTag -or $Release.prerelease -ne $version.Prerelease -or $Release.target_commitish -cne $ExpectedCommit) {
        throw 'Draft identity mismatch; refusing to publish.'
    }
}
& (Join-Path $PSScriptRoot 'Test-ReleaseTag.ps1') -VersionTag $VersionTag -ExpectedCommit $ExpectedCommit
& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $AssetDirectory -VersionTag $VersionTag
# Listing is fail-closed: authorization/network errors are never treated as 'not found'.
if (@(Get-TagReleases).Count -ne 0) { throw 'This tag already has a Release or Draft. Inspect it manually; no assets were changed.' }
$notes = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\docs\Release-Notes.md') -Raw
$notes = $notes.Replace('{{TAG}}', $VersionTag).Replace('{{COMMIT}}', $ExpectedCommit)
$notesPath = Join-Path $AssetDirectory 'release-notes.md'
try {
    [IO.File]::WriteAllText($notesPath, $notes, [Text.UTF8Encoding]::new($false))
    $arguments = @('release', 'create', $VersionTag, '--repo', $Repository, '--verify-tag', '--target', $ExpectedCommit, '--draft', '--latest=false', '--title', "SeleVox $VersionTag", '--notes-file', $notesPath)
    if ($version.Prerelease) { $arguments += '--prerelease' }
    Invoke-Gh -Arguments $arguments | Out-Null
} finally { if (Test-Path -LiteralPath $notesPath) { Remove-Item -LiteralPath $notesPath } }
# gh release create outputs a URL, not a JSON ID. Locate exactly one matching
# draft in the authenticated list and validate its identity before retaining its ID.
$created = @(Get-TagReleases)
if ($created.Count -ne 1) { throw 'Created draft not uniquely found; inspect the retained draft manually.' }
$releaseId = [long]$created[0].id
Assert-DraftIdentity $created[0] $releaseId
if ($created[0].assets.Count -ne 0) { throw 'Created draft already has assets; refusing to upload or replace them.' }
# Upload without --clobber; any failure leaves a non-public draft for inspection.
Invoke-Gh -Arguments @('release', 'upload', $VersionTag, (Join-Path $AssetDirectory $version.ZipName), (Join-Path $AssetDirectory 'SHA256SUMS.txt'), '--repo', $Repository) | Out-Null
$release = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$releaseId")) -join "`n" | ConvertFrom-Json
Assert-DraftIdentity $release $releaseId
if ($release.assets.Count -ne 2) { throw 'Draft assets count mismatch; refusing to publish.' }
foreach ($name in @($version.ZipName, 'SHA256SUMS.txt')) {
    $asset = @($release.assets | Where-Object { $_.name -ceq $name })
    $file = Get-Item -LiteralPath (Join-Path $AssetDirectory $name)
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or $asset[0].size -ne $file.Length -or $asset[0].digest -cne $digest) { throw 'Uploaded asset name/size/SHA-256 mismatch; draft remains private.' }
}
# Recheck remote identity immediately before publication.
& (Join-Path $PSScriptRoot 'Test-ReleaseTag.ps1') -VersionTag $VersionTag -ExpectedCommit $ExpectedCommit
# PATCH the same validated ID: tag-based lookup cannot retrieve an unpublished draft.
$latest = if ($version.Prerelease) { 'false' } else { 'true' }
$published = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$releaseId", '--method', 'PATCH', '-F', 'draft=false', '-F', "prerelease=$($version.Prerelease.ToString().ToLowerInvariant())", '-f', "make_latest=$latest")) -join "`n" | ConvertFrom-Json
if ($published.id -ne $releaseId -or $published.draft -ne $false -or $published.tag_name -cne $VersionTag -or $published.target_commitish -cne $ExpectedCommit -or $published.prerelease -ne $version.Prerelease) { throw 'Published release response identity mismatch; inspect remote state before retrying.' }
