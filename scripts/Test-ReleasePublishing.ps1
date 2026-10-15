#requires -Version 7.2
# Offline command-boundary simulation; never uses GitHub or creates a Git ref.
param([string]$VersionTag = 'v0.0.0-rc.1', [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\artifacts\release'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$commit = 'a' * 40
$repository = 'fixture/fixture'
function git {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'rev-parse') { $commit }
}
function New-MockRelease([bool]$IncludeAssets) {
    $assets = if ($IncludeAssets) {
        foreach ($name in @($version.ZipName, 'SHA256SUMS.txt')) {
            $file = Get-Item -LiteralPath (Join-Path $AssetDirectory $name)
            $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
            if ($global:SeleVoxReleaseTest_scenario -eq 'bad-digest') { $digest = 'sha256:' + ('0' * 64) }
            @{ name = $name; size = $file.Length; state = 'uploaded'; digest = $digest }
        }
    } else { @() }
    $target = if ($global:SeleVoxReleaseTest_scenario -eq 'bad-commit') { 'b' * 40 } else { $commit }
    @{ id = $global:SeleVoxReleaseTest_id; draft = $true; tag_name = $VersionTag; prerelease = $version.Prerelease; target_commitish = $target; assets = @($assets) }
}
function gh {
    $global:LASTEXITCODE = 0
    $global:SeleVoxReleaseTest_calls.Add(($args -join ' '))
    switch ($args[0]) {
        'api' {
            if ($args[1] -eq "repos/$repository/releases/tags/$VersionTag") {
                # GitHub's tag endpoint cannot retrieve an unpublished Draft.
                $global:LASTEXITCODE = 1
                'gh: Not Found (HTTP 404)'
                return
            }
            if ($args[1] -eq "repos/$repository/releases?per_page=100") {
                if ($global:SeleVoxReleaseTest_scenario -eq 'list-error') { $global:LASTEXITCODE = 1; return }
                if (-not $global:SeleVoxReleaseTest_draftCreated) {
                    if ($global:SeleVoxReleaseTest_scenario -in @('existing-draft', 'existing-public')) {
                        ConvertTo-Json -InputObject @(@(@{ tag_name = $VersionTag; draft = ($global:SeleVoxReleaseTest_scenario -eq 'existing-draft') })) -Depth 5 -Compress
                    } else { '[[]]' }
                } else {
                    if ($global:SeleVoxReleaseTest_scenario -eq 'missing-list-draft') { '[[]]'; return }
                    $draft = New-MockRelease $false
                    if ($global:SeleVoxReleaseTest_scenario -eq 'ambiguous-draft') {
                        ConvertTo-Json -InputObject @(@($draft, $draft)) -Depth 5 -Compress
                    } else {
                        ConvertTo-Json -InputObject @(@($draft)) -Depth 5 -Compress
                    }
                }
            } elseif ($args[1] -eq "repos/$repository/releases/$global:SeleVoxReleaseTest_id") {
                if ($args -contains 'PATCH') {
                    if (-not $global:SeleVoxReleaseTest_uploaded -or -not $global:SeleVoxReleaseTest_verified -or $args -notcontains 'draft=false' -or $args -notcontains "prerelease=$($version.Prerelease.ToString().ToLowerInvariant())") { throw 'Unsafe publish arguments/order' }
                    $expectedLatest = if ($version.Prerelease) { 'make_latest=false' } else { 'make_latest=true' }
                    if ($args -notcontains $expectedLatest -or $args -notcontains '-f') { throw 'Incorrect latest flag' }
                    if ($global:SeleVoxReleaseTest_scenario -eq 'patch-error') { $global:LASTEXITCODE = 1; return }
                    $global:SeleVoxReleaseTest_published = $true
                    $release = New-MockRelease $true
                    $release.draft = $false
                    $release | ConvertTo-Json -Depth 5 -Compress
                } else {
                    if ($global:SeleVoxReleaseTest_scenario -in @('missing-id-draft', 'get-error')) { $global:LASTEXITCODE = 1; return }
                    if (-not $global:SeleVoxReleaseTest_uploaded) { throw 'Expected asset verification after upload' }
                    $release = New-MockRelease $true
                    switch ($global:SeleVoxReleaseTest_scenario) {
                        'wrong-id' { $release.id++ }
                        'changed-commit' { $release.target_commitish = 'b' * 40 }
                        'bad-tag' { $release.tag_name = 'v9.9.9' }
                        'not-draft' { $release.draft = $false }
                        'bad-prerelease' { $release.prerelease = -not $version.Prerelease }
                        'bad-count' { $release.assets = @($release.assets[0]) }
                        'bad-name' { $release.assets[0].name = 'unexpected.zip' }
                        'bad-size' { $release.assets[0].size++ }
                        'bad-state' { $release.assets[0].state = 'new' }
                    }
                    $global:SeleVoxReleaseTest_verified = $true
                    $release | ConvertTo-Json -Depth 5 -Compress
                }
            } else { throw 'Unexpected API command/ID' }
        }
        'release' {
            switch ($args[1]) {
                'create' {
                    if ($args -notcontains '--draft' -or $args -notcontains '--verify-tag' -or $args -notcontains '--latest=false' -or (($args -contains '--prerelease') -ne $version.Prerelease)) { throw 'Unsafe create arguments' }
                    $global:SeleVoxReleaseTest_draftCreated = $true
                    "https://github.com/$repository/releases/tag/$VersionTag"
                }
                'upload' {
                    if (-not $global:SeleVoxReleaseTest_draftCreated -or $args -contains '--clobber' -or $global:SeleVoxReleaseTest_uploaded) { throw 'Unsafe/repeated upload arguments' }
                    if ($global:SeleVoxReleaseTest_scenario -eq 'upload-error') { $global:LASTEXITCODE = 1; return }
                    $global:SeleVoxReleaseTest_uploaded = $true
                }
                default { throw 'Unexpected release command (edit/delete must not be used)' }
            }
        }
        default { throw 'Unexpected gh command' }
    }
}
# Explicitly exercise the real-world 404 behavior of the mock before running production code.
$global:SeleVoxReleaseTest_calls = [Collections.Generic.List[string]]::new()
$probe = gh api "repos/$repository/releases/tags/$VersionTag"
if ($LASTEXITCODE -ne 1 -or $probe -notmatch 'HTTP 404') { throw 'Mock must reject Draft lookup by tag' }
$cases = [ordered]@{
    'success' = ''
    'existing-draft' = 'already has a Release'
    'existing-public' = 'already has a Release'
    'list-error' = 'GitHub CLI failed: api'
    'missing-list-draft' = 'not uniquely found'
    'ambiguous-draft' = 'not uniquely found'
    'bad-commit' = 'Draft identity mismatch'
    'upload-error' = 'GitHub CLI failed: release'
    'missing-id-draft' = 'GitHub CLI failed: api'
    'get-error' = 'GitHub CLI failed: api'
    'wrong-id' = 'Draft identity mismatch'
    'changed-commit' = 'Draft identity mismatch'
    'bad-tag' = 'Draft identity mismatch'
    'not-draft' = 'Draft identity mismatch'
    'bad-prerelease' = 'Draft identity mismatch'
    'bad-count' = 'assets count mismatch'
    'bad-name' = 'asset name/size/SHA-256 mismatch'
    'bad-size' = 'asset name/size/SHA-256 mismatch'
    'bad-state' = 'asset name/size/SHA-256 mismatch'
    'bad-digest' = 'asset name/size/SHA-256 mismatch'
    'patch-error' = 'GitHub CLI failed: api'
}
$caseNumber = 0
foreach ($scenario in $cases.Keys) {
    $global:SeleVoxReleaseTest_scenario = $scenario
    # A different server-assigned fixture ID in every scenario catches fixed-ID assumptions.
    $global:SeleVoxReleaseTest_id = 70000 + (++$caseNumber)
    $global:SeleVoxReleaseTest_draftCreated = $false
    $global:SeleVoxReleaseTest_uploaded = $false
    $global:SeleVoxReleaseTest_verified = $false
    $global:SeleVoxReleaseTest_published = $false
    $global:SeleVoxReleaseTest_calls = [Collections.Generic.List[string]]::new()
    $failure = $null
    try {
        & (Join-Path $PSScriptRoot 'Publish-Release.ps1') -VersionTag $VersionTag -ExpectedCommit $commit -AssetDirectory $AssetDirectory -Repository $repository
    } catch { $failure = $_ }
    if (@($global:SeleVoxReleaseTest_calls | Where-Object { $_ -match '/releases/tags/' }).Count) { throw 'Production code used the Draft-incompatible tag endpoint' }
    if ($scenario -eq 'success') {
        if ($failure) { throw $failure }
        if (-not $global:SeleVoxReleaseTest_published) { throw 'Successful simulation did not publish by ID' }
    } else {
        if (-not $failure -or $failure.Exception.Message -notmatch $cases[$scenario] -or $global:SeleVoxReleaseTest_published) { throw "Failure scenario published or rejected for unexpected reason: $scenario ($failure)" }
        if ($scenario -in @('existing-draft', 'existing-public', 'list-error') -and $global:SeleVoxReleaseTest_draftCreated) { throw 'Preflight failure mutated release' }
        if ($scenario -notin @('existing-draft', 'existing-public', 'list-error') -and -not $global:SeleVoxReleaseTest_draftCreated) { throw 'Expected retained draft after failure' }
    }
    Write-Host "Offline release command simulation passed: $scenario (public=$global:SeleVoxReleaseTest_published)"
}
