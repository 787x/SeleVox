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
function gh {
    $global:LASTEXITCODE = 0
    $global:SeleVoxReleaseTest_calls.Add(($args -join ' '))
    switch ($args[0]) {
        'api' {
            if ($global:SeleVoxReleaseTest_scenario -eq 'list-error') { $global:LASTEXITCODE = 1; return }
            if ($args[1] -eq "repos/$repository/releases?per_page=100") {
                if ($global:SeleVoxReleaseTest_scenario -in @('existing-draft', 'existing-public')) {
                    ConvertTo-Json -InputObject @(@(@{ tag_name = $VersionTag; draft = ($global:SeleVoxReleaseTest_scenario -eq 'existing-draft') })) -Depth 5 -Compress
                } else { '[[]]' }
            } elseif ($args[1] -eq "repos/$repository/releases/tags/$VersionTag") {
                $assets = foreach ($name in @($version.ZipName, 'SHA256SUMS.txt')) {
                    $file = Get-Item -LiteralPath (Join-Path $AssetDirectory $name)
                    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
                    if ($global:SeleVoxReleaseTest_scenario -eq 'bad-digest') { $digest = 'sha256:' + ('0' * 64) }
                    @{ name = $name; size = $file.Length; state = 'uploaded'; digest = $digest }
                }
                @{ draft = $true; tag_name = $VersionTag; prerelease = $version.Prerelease; target_commitish = $commit; assets = @($assets) } | ConvertTo-Json -Depth 5 -Compress
            } else { throw 'Unexpected API command' }
        }
        'release' {
            switch ($args[1]) {
                'create' {
                    if ($args -notcontains '--draft' -or $args -notcontains '--verify-tag' -or $args -notcontains '--latest=false' -or (($args -contains '--prerelease') -ne $version.Prerelease)) { throw 'Unsafe create arguments' }
                    $global:SeleVoxReleaseTest_draftCreated = $true
                }
                'upload' {
                    if (-not $global:SeleVoxReleaseTest_draftCreated -or $args -contains '--clobber') { throw 'Unsafe upload arguments' }
                    if ($global:SeleVoxReleaseTest_scenario -eq 'upload-error') { $global:LASTEXITCODE = 1 }
                }
                'edit' {
                    if (-not $global:SeleVoxReleaseTest_draftCreated -or $args -notcontains '--draft=false' -or $args -notcontains '--verify-tag') { throw 'Unsafe publish arguments' }
                    $expectedLatest = if ($version.Prerelease) { '--latest=false' } else { '--latest' }
                    if ($args -notcontains $expectedLatest) { throw 'Incorrect latest flag' }
                    $global:SeleVoxReleaseTest_published = $true
                }
                default { throw 'Unexpected release command' }
            }
        }
        default { throw 'Unexpected gh command' }
    }
}
foreach ($scenario in @('success', 'existing-draft', 'existing-public', 'list-error', 'upload-error', 'bad-digest')) {
    $global:SeleVoxReleaseTest_scenario = $scenario
    $global:SeleVoxReleaseTest_draftCreated = $false
    $global:SeleVoxReleaseTest_published = $false
    $global:SeleVoxReleaseTest_calls = [Collections.Generic.List[string]]::new()
    $failure = $null
    try {
        & (Join-Path $PSScriptRoot 'Publish-Release.ps1') -VersionTag $VersionTag -ExpectedCommit $commit -AssetDirectory $AssetDirectory -Repository $repository
    } catch { $failure = $_ }
    if ($scenario -eq 'success') {
        if ($failure) { throw $failure }
        if (-not $global:SeleVoxReleaseTest_published) { throw 'Successful simulation did not publish' }
    } else {
        if (-not $failure -or $global:SeleVoxReleaseTest_published) { throw "Failure scenario published or did not reject: $scenario" }
        if ($scenario -in @('existing-draft', 'existing-public', 'list-error') -and $global:SeleVoxReleaseTest_draftCreated) { throw 'Preflight failure mutated release' }
        if ($scenario -in @('upload-error', 'bad-digest') -and -not $global:SeleVoxReleaseTest_draftCreated) { throw 'Expected retained draft after failure' }
    }
    Write-Host "Offline release command simulation passed: $scenario (public=$global:SeleVoxReleaseTest_published)"
}
