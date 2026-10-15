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
        foreach ($name in $global:SeleVoxReleaseTest_uploads) {
            $file = Get-Item -LiteralPath (Join-Path $AssetDirectory $name)
            $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
            if ($global:SeleVoxReleaseTest_scenario -eq 'bad-digest') { $digest = 'sha256:' + ('0' * 64) }
            @{ name = $name; size = $file.Length; state = 'uploaded'; digest = $digest }
        }
    } else { @() }
    @{ id = $global:SeleVoxReleaseTest_id; draft = $true; tag_name = $VersionTag; prerelease = $version.Prerelease; target_commitish = $commit; assets = @($assets) }
}
function gh {
    $global:LASTEXITCODE = 0
    $global:SeleVoxReleaseTest_calls.Add(($args -join ' '))
    if ($args[0] -eq 'release' -and $args[1] -eq 'create') {
        # Legacy creation is supported only to demonstrate that the old publisher
        # fails when its post-create list cannot see the successfully created draft.
        $global:SeleVoxReleaseTest_draftCreated = $true
        "https://github.com/$repository/releases/tag/$VersionTag"
        return
    }
    if ($args[0] -ne 'api') { throw 'Unexpected command; release upload/edit/delete must not be used' }
    $endpoint = $args[1]
    if ($endpoint -eq "repos/$repository/releases/tags/$VersionTag") {
        $global:LASTEXITCODE = 1
        'gh: Not Found (HTTP 404)'
        return
    }
    if ($endpoint -eq "repos/$repository/releases?per_page=100") {
        $global:SeleVoxReleaseTest_listCalls++
        if ($global:SeleVoxReleaseTest_scenario -eq 'list-error') { $global:LASTEXITCODE = 1; return }
        if (-not $global:SeleVoxReleaseTest_draftCreated -and $global:SeleVoxReleaseTest_scenario -in @('existing-draft', 'existing-public')) {
            ConvertTo-Json -InputObject @(@(@{ tag_name = $VersionTag; draft = ($global:SeleVoxReleaseTest_scenario -eq 'existing-draft') })) -Depth 5 -Compress
        } else {
            # Always stale after creation, even though POST and GET ID succeed.
            '[[]]'
        }
        return
    }
    if ($endpoint -eq "repos/$repository/releases") {
        if ($args -notcontains 'POST' -or $args -notcontains '--input' -or $global:SeleVoxReleaseTest_draftCreated) { throw 'Unsafe/repeated create arguments' }
        $body = Get-Content -LiteralPath $args[([array]::IndexOf($args, '--input') + 1)] -Raw | ConvertFrom-Json
        if ($body.tag_name -cne $VersionTag -or $body.target_commitish -cne $commit -or $body.draft -isnot [bool] -or -not $body.draft -or $body.prerelease -isnot [bool] -or $body.prerelease -ne $version.Prerelease -or $body.make_latest -isnot [string] -or $body.make_latest -cne 'false' -or $body.name -cne "SeleVox $VersionTag" -or [string]::IsNullOrWhiteSpace($body.body)) { throw 'Unsafe create payload' }
        if ($global:SeleVoxReleaseTest_scenario -eq 'create-error') { $global:LASTEXITCODE = 1; return }
        $global:SeleVoxReleaseTest_draftCreated = $true
        $release = New-MockRelease $false
        switch ($global:SeleVoxReleaseTest_scenario) {
            'missing-create-id' { $release.Remove('id') }
            'zero-create-id' { $release.id = 0 }
            'string-create-id' { $release.id = [string]$release.id }
            'create-bad-tag' { $release.tag_name = 'v9.9.9' }
            'create-bad-commit' { $release.target_commitish = 'b' * 40 }
            'create-not-draft' { $release.draft = $false }
            'create-bad-prerelease' { $release.prerelease = -not $version.Prerelease }
            'create-existing-assets' { $release.assets = @(@{ name = 'unexpected.zip' }) }
        }
        $release | ConvertTo-Json -Depth 5 -Compress
        return
    }
    $uploadPrefix = "https://uploads.github.com/repos/$repository/releases/$global:SeleVoxReleaseTest_id/assets?name="
    if ($endpoint.StartsWith($uploadPrefix, [StringComparison]::Ordinal)) {
        $name = [Uri]::UnescapeDataString($endpoint.Substring($uploadPrefix.Length))
        if (-not $global:SeleVoxReleaseTest_emptyVerified -or $name -cnotin @($version.ZipName, $version.SetupName, 'SHA256SUMS.txt') -or $global:SeleVoxReleaseTest_uploads.Contains($name) -or $args -notcontains 'POST' -or $args -notcontains '--input') { throw 'Unsafe/repeated upload arguments/order' }
        $order = @($version.ZipName, $version.SetupName, 'SHA256SUMS.txt')
        if ($name -cne $order[$global:SeleVoxReleaseTest_uploads.Count]) { throw 'Incorrect upload order' }
        $file = Get-Item -LiteralPath $args[([array]::IndexOf($args, '--input') + 1)]
        $expectedFile = Get-Item -LiteralPath (Join-Path $AssetDirectory $name)
        $mime = if ($name.EndsWith('.zip')) { 'application/zip' } elseif ($name.EndsWith('.exe')) { 'application/vnd.microsoft.portable-executable' } else { 'text/plain' }
        if ($file.FullName -ne $expectedFile.FullName -or $args -notcontains "Content-Length:$($file.Length)" -or $args -notcontains "Content-Type:$mime") { throw 'Incorrect upload binary path/length/type' }
        if ($global:SeleVoxReleaseTest_scenario -eq 'upload-error' -or ($global:SeleVoxReleaseTest_scenario -eq 'third-upload-error' -and $global:SeleVoxReleaseTest_uploads.Count -eq 2) -or ($global:SeleVoxReleaseTest_scenario -eq 'second-upload-error' -and $global:SeleVoxReleaseTest_uploads.Count -eq 1)) { $global:LASTEXITCODE = 1; return }
        $global:SeleVoxReleaseTest_uploads.Add($name)
        '{"state":"uploaded"}'
        return
    }
    if ($endpoint -ne "repos/$repository/releases/$global:SeleVoxReleaseTest_id") { throw 'Unexpected API command/ID' }
    if ($args -contains 'PATCH') {
        if ($global:SeleVoxReleaseTest_uploads.Count -ne 3 -or -not $global:SeleVoxReleaseTest_verified -or $args -notcontains 'draft=false' -or $args -notcontains "prerelease=$($version.Prerelease.ToString().ToLowerInvariant())") { throw 'Unsafe publish arguments/order' }
        $expectedLatest = if ($version.Prerelease) { 'make_latest=false' } else { 'make_latest=true' }
        if ($args -notcontains $expectedLatest -or $args -notcontains '-f') { throw 'Incorrect latest flag' }
        if ($global:SeleVoxReleaseTest_scenario -eq 'patch-error') { $global:LASTEXITCODE = 1; return }
        $global:SeleVoxReleaseTest_published = $true
        $release = New-MockRelease $true
        $release.draft = $false
        $release | ConvertTo-Json -Depth 5 -Compress
        return
    }
    if ($global:SeleVoxReleaseTest_scenario -in @('missing-id-draft', 'get-error') -or ($global:SeleVoxReleaseTest_scenario -eq 'postupload-get-error' -and $global:SeleVoxReleaseTest_uploads.Count -eq 3)) { $global:LASTEXITCODE = 1; return }
    $release = New-MockRelease $true
    if ($global:SeleVoxReleaseTest_uploads.Count -eq 0) {
        if ($global:SeleVoxReleaseTest_scenario -eq 'preupload-existing-assets') { $release.assets = @(@{ name = 'unexpected.zip' }) }
        $global:SeleVoxReleaseTest_emptyVerified = $true
    } else {
        if ($global:SeleVoxReleaseTest_uploads.Count -ne 3) { throw 'Unexpected query after incomplete upload' }
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
    }
    $release | ConvertTo-Json -Depth 5 -Compress
}
$global:SeleVoxReleaseTest_calls = [Collections.Generic.List[string]]::new()
$probe = gh api "repos/$repository/releases/tags/$VersionTag"
if ($LASTEXITCODE -ne 1 -or $probe -notmatch 'HTTP 404') { throw 'Mock must reject Draft lookup by tag' }
$cases = [ordered]@{
    'stale-list-success' = ''
    'existing-draft' = 'already has a Release'
    'existing-public' = 'already has a Release'
    'list-error' = 'GitHub CLI failed: api'
    'create-error' = 'GitHub CLI failed: api'
    'missing-create-id' = 'no valid positive integer ID'
    'zero-create-id' = 'no valid positive integer ID'
    'string-create-id' = 'no valid positive integer ID'
    'create-bad-tag' = 'Draft identity mismatch'
    'create-bad-commit' = 'Draft identity mismatch'
    'create-not-draft' = 'Draft identity mismatch'
    'create-bad-prerelease' = 'Draft identity mismatch'
    'create-existing-assets' = 'already has assets'
    'preupload-existing-assets' = 'already has assets'
    'upload-error' = 'GitHub CLI failed: api'
    'third-upload-error' = 'GitHub CLI failed: api'
    'second-upload-error' = 'GitHub CLI failed: api'
    'missing-id-draft' = 'GitHub CLI failed: api'
    'get-error' = 'GitHub CLI failed: api'
    'postupload-get-error' = 'GitHub CLI failed: api'
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
    $global:SeleVoxReleaseTest_id = 80000 + (++$caseNumber)
    $global:SeleVoxReleaseTest_draftCreated = $false
    $global:SeleVoxReleaseTest_emptyVerified = $false
    $global:SeleVoxReleaseTest_verified = $false
    $global:SeleVoxReleaseTest_published = $false
    $global:SeleVoxReleaseTest_listCalls = 0
    $global:SeleVoxReleaseTest_uploads = [Collections.Generic.List[string]]::new()
    $global:SeleVoxReleaseTest_calls = [Collections.Generic.List[string]]::new()
    $failure = $null
    try {
        & (Join-Path $PSScriptRoot 'Publish-Release.ps1') -VersionTag $VersionTag -ExpectedCommit $commit -AssetDirectory $AssetDirectory -Repository $repository
    } catch { $failure = $_ }
    if ($scenario -eq 'stale-list-success') {
        if ($failure) { throw $failure }
        if (-not $global:SeleVoxReleaseTest_published) { throw 'Successful simulation did not publish from POST ID' }
    } else {
        if (-not $failure -or $failure.Exception.Message -notmatch $cases[$scenario] -or $global:SeleVoxReleaseTest_published) { throw "Failure scenario published or rejected for unexpected reason: $scenario ($failure)" }
        if ($scenario -in @('existing-draft', 'existing-public', 'list-error', 'create-error') -and $global:SeleVoxReleaseTest_draftCreated) { throw 'Preflight/create failure mutated release' }
        if ($scenario -notin @('existing-draft', 'existing-public', 'list-error', 'create-error') -and -not $global:SeleVoxReleaseTest_draftCreated) { throw 'Expected retained draft after failure' }
        if ($scenario -eq 'third-upload-error' -and $global:SeleVoxReleaseTest_uploads.Count -ne 2) { throw 'Both binary uploads should remain intact after checksum upload failure' }
        if ($scenario -eq 'second-upload-error' -and $global:SeleVoxReleaseTest_uploads.Count -ne 1) { throw 'First upload should remain intact after second upload failure' }
    }
    if ($global:SeleVoxReleaseTest_listCalls -ne 1) { throw 'Publisher must only list for preflight, never to discover created ID' }
    if (@($global:SeleVoxReleaseTest_calls | Where-Object { $_ -match '/releases/tags/|^release ' }).Count) { throw 'Production code used a tag-based release command' }
    Write-Host "Offline release command simulation passed: $scenario (public=$global:SeleVoxReleaseTest_published)"
}
# Only reached once all assertions passed; do not leak intentional mock failures
# into Actions' pwsh wrapper, which propagates LASTEXITCODE.
$global:LASTEXITCODE = 0
