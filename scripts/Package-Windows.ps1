#requires -Version 7.2
param([Parameter(Mandatory)][string]$VersionTag, [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\win-x64'), [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\release'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($source.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be outside publish directory.' }
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) { throw 'Package output must be empty.' }
& (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $source
Assert-PublishVersion $source $version
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zip = Join-Path $output $version.ZipName
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "SeleVox/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
& (Join-Path $PSScriptRoot 'Test-Portable.ps1') -ZipPath $zip -PublishDirectory $source -VersionTag $VersionTag
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $($version.ZipName)`n", [Text.UTF8Encoding]::new($false))
Write-Host "Validated portable package: $zip"
