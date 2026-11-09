#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$CudaArchitectures = '86;89;120',
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '..\artifacts\ocr-cuda'),
    [ValidateRange(1, 64)][int]$Jobs = 6
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Run from an x64 Visual Studio Developer PowerShell.
Get-Command cl, nvcc, ninja, cmake, ctest, git -ErrorAction Stop | Out-Null
$source = Join-Path $PSScriptRoot '..\src\SeleVox.Ocr.Native'
cmake -S $source -B $BuildDirectory -G Ninja -DCMAKE_BUILD_TYPE=Release "-DCMAKE_CUDA_ARCHITECTURES=$CudaArchitectures"
cmake --build $BuildDirectory --parallel $Jobs
ctest --test-dir $BuildDirectory --output-on-failure
