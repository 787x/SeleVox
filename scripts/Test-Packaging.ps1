#requires -Version 7.2
param([string]$VersionTag = 'v0.0.0-rc.1', [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\win-x64'), [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\release'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    if (-not $failure -or $failure.Exception.Message -notmatch $Pattern) { throw "Expected rejection: $Pattern" }
    Write-Host "Expected rejection passed: $Pattern"
}
$version = Get-ReleaseVersion $VersionTag
foreach ($tag in @('v01.2.3', '1.2.3', 'v1.2', 'v1.2.3-rc.0', 'v1.2.3-rc.01', 'v1.2.3-beta.1', "v1.2.3`n", 'v65535.0.0')) {
    Expect-Failure { Get-ReleaseVersion $tag } 'Invalid version|assembly limit'
}
$stable = Get-ReleaseVersion 'v12.34.56'
if ($stable.Prerelease -or $stable.ZipName -cne 'SeleVox-v12.34.56-win-x64-portable.zip' -or $stable.Numeric -ne '12.34.56.0') { throw 'Stable version mapping failed' }
& (Join-Path $PSScriptRoot 'Package-Windows.ps1') -VersionTag $VersionTag -PublishDirectory $PublishDirectory -OutputDirectory $OutputDirectory
& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $OutputDirectory -VersionTag $VersionTag
$zip = Join-Path $OutputDirectory $version.ZipName
$sum = Join-Path $OutputDirectory 'SHA256SUMS.txt'
$originalSum = [IO.File]::ReadAllText($sum)
try {
    [IO.File]::WriteAllText($sum, ('0' * 64) + "  $($version.ZipName)`n")
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $OutputDirectory -VersionTag $VersionTag } 'SHA256SUMS'
} finally { [IO.File]::WriteAllText($sum, $originalSum, [Text.UTF8Encoding]::new($false)) }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('SeleVox-package-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $badZip = Join-Path $scratch $version.ZipName
    $archive = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Create)
    try { $null = $archive.CreateEntry('unexpected/file.txt') } finally { $archive.Dispose() }
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-Portable.ps1') -ZipPath $badZip -PublishDirectory $PublishDirectory -VersionTag $VersionTag } 'unexpected ZIP entry'
    Copy-Item -LiteralPath $zip -Destination $badZip -Force
    $archive = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('SeleVox/OcrData/eng.traineddata'); $entry.Delete()
    } finally { $archive.Dispose() }
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-Portable.ps1') -ZipPath $badZip -PublishDirectory $PublishDirectory -VersionTag $VersionTag } 'file count'
    Copy-Item -LiteralPath $zip -Destination $badZip -Force
    $archive = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('SeleVox/SeleVox.Core.dll'); $entry.Delete()
        $stream = $archive.CreateEntry('SeleVox/SeleVox.Core.dll').Open()
        try { $stream.WriteByte(0) } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-Portable.ps1') -ZipPath $badZip -PublishDirectory $PublishDirectory -VersionTag $VersionTag } 'path/hash mismatch'
    Expect-Failure { Assert-PublishVersion $PublishDirectory (Get-ReleaseVersion 'v9.9.9') } 'version mismatch'
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($scratch).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package test cleanup path' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $OutputDirectory -VersionTag $VersionTag
