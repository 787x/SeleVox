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
$ocr = Join-Path $PublishDirectory 'OcrCuda'
$native = Join-Path (Resolve-Path -LiteralPath $ocr).Path 'selevox_ocr.dll'
# Match OcrNative's restricted search flags when probing the shipped DLL.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class SeleVoxReleaseOcrProbe
{
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint Version();

    public static uint ReadAbi(string path)
    {
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x800);
        if (module == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "OCR module load failed with Win32 error " + error);
        }
        try
        {
            IntPtr export = NativeLibrary.GetExport(module, "ocr_abi_version");
            return Marshal.GetDelegateForFunctionPointer<Version>(export)();
        }
        finally { NativeLibrary.Free(module); }
    }
}
'@
$dependency = Join-Path $ocr 'cublasLt64_13.dll'
$fallback = Join-Path (Split-Path ([IO.Path]::GetFullPath($PublishDirectory))) ('ocr-path-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $fallback | Out-Null
$backup = Join-Path $fallback 'cublasLt64_13.dll'
$originalPath = $env:Path
try {
    Move-Item -LiteralPath $dependency -Destination $backup
    $env:Path = "$fallback;$originalPath"
    Expect-Failure { [SeleVoxReleaseOcrProbe]::ReadAbi($native) } 'Win32 error 126'
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $PublishDirectory } 'Missing or empty OCR runtime file'
} finally {
    $env:Path = $originalPath
    if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $dependency }
    Remove-Item -LiteralPath $fallback
}
if ([SeleVoxReleaseOcrProbe]::ReadAbi($native) -ne 1) { throw 'Packaged OCR ABI mismatch' }
$originalNative = [IO.File]::ReadAllBytes($native)
$changed = [byte[]]$originalNative.Clone()
try {
    $changed[$changed.Length - 1] = $changed[$changed.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($native, $changed)
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-OcrCudaRelease.ps1') -Directory $ocr } 'SHA-256 mismatch'
    $machine = [BitConverter]::ToInt32($changed, 0x3C) + 4
    $changed[$machine] = 0x4C
    $changed[$machine + 1] = 0x01
    [IO.File]::WriteAllBytes($native, $changed)
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-OcrCudaRelease.ps1') -Directory $ocr } 'not x64'
} finally { [IO.File]::WriteAllBytes($native, $originalNative) }
$manifest = Join-Path $ocr 'runtime.json'
$originalManifest = [IO.File]::ReadAllText($manifest)
try {
    [IO.File]::WriteAllText($manifest, $originalManifest.Replace('"13.4.1"', '"13.4.2"'))
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-OcrCudaRelease.ps1') -Directory $ocr } 'version mismatch'
} finally { [IO.File]::WriteAllText($manifest, $originalManifest) }
$forbidden = Join-Path $PublishDirectory ('test-' + [guid]::NewGuid() + '.GGUF')
try {
    [IO.File]::WriteAllText($forbidden, 'Exclusion test marker')
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $PublishDirectory } 'forbidden'
} finally { Remove-Item -LiteralPath $forbidden }
& (Join-Path $PSScriptRoot 'Package-Windows.ps1') -VersionTag $VersionTag -PublishDirectory $PublishDirectory -OutputDirectory $OutputDirectory
& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $OutputDirectory -VersionTag $VersionTag
$zip = Join-Path $OutputDirectory $version.ZipName
$sum = Join-Path $OutputDirectory 'SHA256SUMS.txt'
$originalSum = [IO.File]::ReadAllText($sum)
try {
    [IO.File]::WriteAllText($sum, ('0' * 64) + "  $($version.ZipName)`n")
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $OutputDirectory -VersionTag $VersionTag } 'SHA256SUMS'
} finally { [IO.File]::WriteAllText($sum, $originalSum, [Text.UTF8Encoding]::new($false)) }
$setup = Join-Path $OutputDirectory $version.SetupName
$hidden = "$setup.missing"
try {
    Move-Item -LiteralPath $setup -Destination $hidden
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $OutputDirectory -VersionTag $VersionTag } 'Expected exactly'
} finally { Move-Item -LiteralPath $hidden -Destination $setup }
Expect-Failure { & (Join-Path $PSScriptRoot 'Test-Installer.ps1') -SetupPath $setup -VersionTag 'v9.9.9' } 'version mismatch'
$bytes = [IO.File]::ReadAllBytes($setup)
try {
    $damaged = [byte[]]$bytes.Clone(); $damaged[$damaged.Length - 1] = $damaged[$damaged.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($setup, $damaged)
    Expect-Failure { & (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Directory $OutputDirectory -VersionTag $VersionTag } 'SHA256SUMS'
} finally { [IO.File]::WriteAllBytes($setup, $bytes) }
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
