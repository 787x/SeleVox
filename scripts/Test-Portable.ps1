#requires -Version 7.2
param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$VersionTag)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
if ([IO.Path]::GetFileName($ZipPath) -cne $version.ZipName) { throw 'ZIP filename does not match tag.' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('SeleVox-unzip-' + [guid]::NewGuid().ToString('N'))
try {
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ZipPath))
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ($name -cnotmatch '^SeleVox/' -or $name.Contains('\') -or $name.Contains(':') -or ($name.Split('/') -contains '..') -or ($name.Split('/') -contains '.') -or -not $seen.Add($name)) { throw 'Unsafe or unexpected ZIP entry.' }
        }
    } finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($ZipPath), $scratch)
    $extracted = Join-Path $scratch 'SeleVox'
    $original = @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)
    $actual = @(Get-ChildItem -LiteralPath $extracted -File -Recurse -Force)
    if ($original.Count -eq 0 -or $original.Count -ne $actual.Count) { throw 'ZIP file count differs from publish directory.' }
    foreach ($file in $original) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $target = Join-Path $extracted $relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw "ZIP path/hash mismatch: $relative" }
    }
    & (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $extracted
    Assert-PublishVersion $extracted $version
    Write-Host "ZIP extraction, all $($original.Count) file hashes, versions and CLI smoke passed."
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($scratch).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe unzip cleanup path' }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
