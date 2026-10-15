#requires -Version 7.2
param([Parameter(Mandatory)][string]$VersionTag, [Parameter(Mandatory)][string]$ExpectedCommit)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$null = Get-ReleaseVersion $VersionTag
# Force-refresh only this remote tag and main; annotated tags are peeled to commits.
git fetch --no-tags origin '+refs/heads/main:refs/remotes/origin/main' "+refs/tags/${VersionTag}:refs/tags/${VersionTag}"
if ($LASTEXITCODE -ne 0) { throw 'Remote tag/main fetch failed.' }
$commit = git rev-parse "refs/tags/${VersionTag}^{commit}"
if ($LASTEXITCODE -ne 0 -or $commit -cne $ExpectedCommit) { throw 'Remote tag commit differs from the checked-out build commit.' }
git merge-base --is-ancestor $commit refs/remotes/origin/main
if ($LASTEXITCODE -ne 0) { throw 'Tag commit is not in current main history.' }
Write-Host "Remote tag $VersionTag resolves to main ancestor $commit"
