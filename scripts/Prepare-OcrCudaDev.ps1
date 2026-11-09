#requires -Version 7.2
param(
    [Parameter(Mandatory)][string]$NativeBin,
    [Parameter(Mandatory)][string]$CudaBin,
    [Parameter(Mandatory)][string]$AppOutput,
    [string[]]$CudaDependencies = @('cublas64_13.dll', 'cublasLt64_13.dll')
)
$ErrorActionPreference = 'Stop'
$output = (Resolve-Path -LiteralPath $AppOutput).ProviderPath
if (!(Test-Path -LiteralPath (Join-Path $output 'SeleVox.App.dll') -PathType Leaf)) {
    throw 'Build SeleVox first and specify its build output (or the test output).'
}
$files = @(Get-Item -LiteralPath (Join-Path $NativeBin 'selevox_ocr.dll'))
foreach ($name in $CudaDependencies) {
    if ($name -notmatch '^(cublas(Lt)?|cudart)64_\d+\.dll$') { throw "Unexpected CUDA DLL name: $name" }
    $files += Get-Item -LiteralPath (Join-Path $CudaBin $name)
}
$destination = Join-Path $output 'OcrCuda'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $destination }
