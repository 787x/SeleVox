#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '../artifacts/ocr-release'),
    [ValidateRange(1, 64)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Split-Path $PSScriptRoot
$nativeSource = Join-Path $repo 'src/SeleVox.Ocr.Native'
if (!(Select-String -LiteralPath (Join-Path $nativeSource 'ocr.h') -Pattern '^#define OCR_ABI_VERSION 1u$' -Quiet)) {
    throw 'The release pipeline requires OCR ABI 1'
}
$work = [IO.Path]::GetFullPath($WorkDirectory)
$downloads = Join-Path $work 'downloads'
$toolkit = Join-Path $work 'toolkit'
$build = Join-Path $work 'build'
$stage = Join-Path $work 'OcrCuda'
foreach ($path in @($toolkit, $build, $stage)) {
    if (Test-Path -LiteralPath $path) { throw "Choose a fresh build directory: $path already exists" }
}
New-Item -ItemType Directory -Force -Path $downloads, $toolkit, $stage | Out-Null
$archives = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'OcrCudaArchives.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($component in $archives.Keys) {
    $version, $sha = $archives[$component]
    $name = "$component-windows-x86_64-$version-archive.zip"
    $url = "https://developer.download.nvidia.com/compute/cuda/redist/$component/windows-x86_64/$name"
    $zip = Join-Path $downloads $name
    if (!(Test-Path -LiteralPath $zip)) { Invoke-WebRequest $url -OutFile $zip }
    if ((Get-FileHash -LiteralPath $zip).Hash -ne $sha) { throw "CUDA archive SHA-256 mismatch: $name" }
    Write-Host "$sha  $url"
    # Preserve each component's top-level license before merging toolkit files.
    if ($component -in @('libcublas', 'cuda_cudart')) {
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $license = @($archive.Entries | Where-Object FullName -Match '^[^/]+/LICENSE(?:\.txt)?$')
            if ($license.Count -ne 1) { throw "Cannot identify NVIDIA license in $name" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($license[0], (Join-Path $stage "$component-LICENSE.txt"))
        } finally { $archive.Dispose() }
    }
    tar.exe -xf $zip -C $toolkit --strip-components 1
}

# Import the runner's MSVC x64 environment. Log the exact compiler and tools used.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'MSVC x64 build tools are required' }
$dev = Join-Path $vs 'Common7/Tools/VsDevCmd.bat'
cmd.exe /d /c "call `"$dev`" -no_logo -arch=x64 -host_arch=x64 && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}
$env:CUDA_PATH = $toolkit
$env:CUDA_PATH_V13_4 = $toolkit
$env:Path = "$(Join-Path $toolkit 'bin');$(Join-Path $toolkit 'bin/x64');$env:Path"
$nvcc = Join-Path $toolkit 'bin/nvcc.exe'
$nvccVersion = & $nvcc --version | Out-String
if ($nvccVersion -notmatch '\bV13\.4\.59\b') { throw 'Unexpected CUDA compiler version' }
Write-Host $nvccVersion
Write-Host "MSVC $env:VCToolsVersion, Windows SDK $env:WindowsSDKVersion"
cmake --version
ninja --version
$architectures = '86;89;120'
$llama = '873e5d8e39feb34a376e0efd01bf3f665dfffeb5'
Write-Host "llama.cpp $llama, CUDA architectures $architectures"
$timer = [Diagnostics.Stopwatch]::StartNew()
$llamaSource = Join-Path $work 'llama.cpp'
if (Test-Path -LiteralPath $llamaSource) { throw 'Choose a fresh llama.cpp source directory' }
git init --quiet $llamaSource
git -C $llamaSource fetch --depth=1 https://github.com/ggml-org/llama.cpp.git $llama
git -C $llamaSource checkout --quiet --detach FETCH_HEAD
if ((git -C $llamaSource rev-parse HEAD) -ne $llama) { throw 'Unexpected llama.cpp commit' }
$configuration = cmake -S $nativeSource -B $build -G Ninja -DCMAKE_BUILD_TYPE=Release `
    "-DCMAKE_CUDA_ARCHITECTURES=$architectures" "-DCMAKE_CUDA_COMPILER=$nvcc" "-DCUDAToolkit_ROOT=$toolkit" `
    "-DFETCHCONTENT_SOURCE_DIR_LLAMA_CPP=$llamaSource" '-DCMAKE_CUDA_FLAGS=-Xcompiler=/utf-8'
$configuration | ForEach-Object { Write-Host $_ }
if (!($configuration -match 'Using CMAKE_CUDA_ARCHITECTURES=86;89;120a CMAKE_CUDA_ARCHITECTURES_NATIVE=')) {
    throw 'Unexpected effective GGML CUDA architectures'
}
cmake --build $build --parallel $Jobs --target selevox_ocr_test
Copy-Item -LiteralPath (Join-Path $llamaSource 'LICENSE') -Destination (Join-Path $stage 'llama.cpp-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repo 'docs/OcrCuda-NOTICE.txt') -Destination (Join-Path $stage 'NOTICE.txt')
Copy-Item -LiteralPath (Join-Path $build 'bin/selevox_ocr.dll') -Destination $stage
foreach ($name in @('cublas64_13.dll', 'cublasLt64_13.dll')) {
    $files = @(Get-ChildItem -LiteralPath $toolkit -Recurse -File -Filter $name)
    if ($files.Count -ne 1) { throw "Expected one toolkit copy of $name" }
    Copy-Item -LiteralPath $files[0].FullName -Destination $stage
}
foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    Copy-Item -LiteralPath (Join-Path $repo "src/SeleVox.App/OcrRuntime/$name") -Destination $stage
}
# Run with application-local dependencies and no Toolkit on PATH.
Copy-Item -LiteralPath (Join-Path $build 'bin/selevox_ocr_test.exe') -Destination $stage
try {
    $buildPath = $env:Path
    $env:Path = "$env:SystemRoot/System32"
    & (Join-Path $stage 'selevox_ocr_test.exe') --self-test
} finally {
    $env:Path = $buildPath
    Remove-Item -LiteralPath (Join-Path $stage 'selevox_ocr_test.exe')
}
$dlls = @(Get-ChildItem -LiteralPath $stage -Filter '*.dll' -File)
$names = $dlls.Name
foreach ($dll in $dlls) {
    $report = dumpbin.exe /dependents $dll.FullName | Out-String
    Write-Host $report
    $imports = [regex]::Matches($report, '(?im)^\s+([\w.-]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value }
    foreach ($dependency in $imports) {
        if ($dependency -in $names -or $dependency -match '^api-ms-win-(core|crt)-' -or
            $dependency -in @('kernel32.dll', 'kernelbase.dll', 'ntdll.dll', 'advapi32.dll', 'bcrypt.dll', 'user32.dll', 'ucrtbase.dll', 'msvcrt.dll', 'ole32.dll', 'shell32.dll', 'psapi.dll', 'version.dll', 'nvcuda.dll')) { continue }
        throw "Unapproved dependency $dependency in $($dll.Name)"
    }
    if ($dll.Name -eq 'selevox_ocr.dll' -and 'cublas64_13.dll' -notin $imports) { throw 'Missing cuBLAS import' }
    if ($dll.Name -eq 'cublas64_13.dll' -and 'cublasLt64_13.dll' -notin $imports) { throw 'Missing cuBLASLt import' }
}
$exports = dumpbin.exe /exports (Join-Path $stage 'selevox_ocr.dll') | Out-String
foreach ($name in @('abi_version', 'create', 'recognize', 'cancel', 'destroy', 'error_message', 'free_text')) {
    if ($exports -notmatch "(?m)\socr_$name\s*$") { throw "Missing OCR export ocr_$name" }
}
$files = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $stage -File | Sort-Object Name) {
    $files[$file.Name] = (Get-FileHash -LiteralPath $file.FullName).Hash
    Write-Host "$($files[$file.Name])  $($file.Name)  $($file.Length) bytes  $($file.VersionInfo.FileVersion)"
}
$commit = git -C $repo rev-parse HEAD
@{ commit = $commit; llama = $llama; cuda = '13.4.1'; architectures = $architectures; abi = 1; files = $files } |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $stage 'runtime.json')
& (Join-Path $PSScriptRoot 'Test-OcrCudaRelease.ps1') -Directory $stage -ExpectedCommit $commit
Write-Host "Native build and validation elapsed: $($timer.Elapsed)"
