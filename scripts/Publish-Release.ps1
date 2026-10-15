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
$requestPath = [IO.Path]::GetTempFileName()
try {
    $payload = @{ tag_name = $VersionTag; target_commitish = $ExpectedCommit; name = "SeleVox $VersionTag"; body = $notes; draft = $true; prerelease = $version.Prerelease; make_latest = 'false' } | ConvertTo-Json
    [IO.File]::WriteAllText($requestPath, $payload, [Text.UTF8Encoding]::new($false))
    # Recheck immediately before POST: the REST endpoint can create a missing tag.
    & (Join-Path $PSScriptRoot 'Test-ReleaseTag.ps1') -VersionTag $VersionTag -ExpectedCommit $ExpectedCommit
    $created = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases", '--method', 'POST', '--input', $requestPath)) -join "`n" | ConvertFrom-Json
} finally { Remove-Item -LiteralPath $requestPath }
# The successful POST response is authoritative; a fresh Draft may not yet be
# visible in the list endpoint. Never rediscover this ID by tag or list.
$idProperty = $created.PSObject.Properties['id']
if (-not $idProperty -or ($idProperty.Value -isnot [int] -and $idProperty.Value -isnot [long]) -or $idProperty.Value -le 0) { throw 'Created draft response has no valid positive integer ID; inspect the retained draft manually.' }
$releaseId = [long]$idProperty.Value
Assert-DraftIdentity $created $releaseId
if ($created.assets.Count -ne 0) { throw 'Created draft already has assets; refusing to upload or replace them.' }
$release = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$releaseId")) -join "`n" | ConvertFrom-Json
Assert-DraftIdentity $release $releaseId
if ($release.assets.Count -ne 0) { throw 'Draft already has assets; refusing to upload or replace them.' }
foreach ($name in @($version.ZipName, $version.SetupName, 'SHA256SUMS.txt')) {
    $file = Get-Item -LiteralPath (Join-Path $AssetDirectory $name)
    $mime = if ($name.EndsWith('.zip')) { 'application/zip' } elseif ($name.EndsWith('.exe')) { 'application/vnd.microsoft.portable-executable' } else { 'text/plain' }
    $upload = "https://uploads.github.com/repos/$Repository/releases/$releaseId/assets?name=$([Uri]::EscapeDataString($name))"
    # Binary input, exact length, same ID; no tag lookup, overwrite or retry.
    Invoke-Gh -Arguments @('api', $upload, '--method', 'POST', '--header', "Content-Type:$mime", '--header', "Content-Length:$($file.Length)", '--input', $file.FullName) | Out-Null
}
$release = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$releaseId")) -join "`n" | ConvertFrom-Json
Assert-DraftIdentity $release $releaseId
if ($release.assets.Count -ne 3) { throw 'Draft assets count mismatch; refusing to publish.' }
foreach ($name in @($version.ZipName, $version.SetupName, 'SHA256SUMS.txt')) {
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
