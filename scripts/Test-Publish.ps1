#requires -Version 7.2
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
function Assert-File([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty publish file: $relative"
    }
}
foreach ($file in @(
    'SeleVox.App.exe', 'SeleVox.Cli.exe', 'SeleVox.Core.dll',
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll',
    'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'wpfgfx_cor3.dll',
    'Tesseract.dll', 'x64/tesseract50.dll', 'x64/leptonica-1.82.0.dll',
    'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
    'OcrData/eng.traineddata', 'OcrData/chi_sim.traineddata',
    'OcrData/LICENSE', 'OcrData/Native-Tesseract-LICENSE', 'OcrData/Tesseract-LICENSE.txt',
    'OcrData/Leptonica-LICENSE.txt', 'OcrData/InteropDotNet-LICENSE.txt', 'OcrData/NOTICE.md',
    'OcrRuntime/NOTICE.md', 'OcrRuntime/Visual-C-Runtime-LICENSE.docx', 'OcrRuntime/Visual-C-Runtime-LICENSE.txt',
    'EdgeTransport/LICENSE', 'EdgeTransport/NOTICE.md'
)) { Assert-File $file }
$frameworkIdentity = $null
foreach ($app in @('SeleVox.App', 'SeleVox.Cli')) {
    foreach ($suffix in @('.dll', '.deps.json', '.runtimeconfig.json')) { Assert-File ($app + $suffix) }
    $config = Get-Content -LiteralPath (Join-Path $root "$app.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
    if (-not $config.runtimeOptions.includedFrameworks -or $config.runtimeOptions.ContainsKey('framework')) { throw "$app is not self-contained" }
    $identity = ($config.runtimeOptions.includedFrameworks | Sort-Object name | ForEach-Object { "$($_.name)/$($_.version)" }) -join ';'
    if ($config.runtimeOptions.tfm -ne 'net10.0' -or $identity -notmatch 'Microsoft.NETCore.App/10\.' -or $identity -notmatch 'Microsoft.WindowsDesktop.App/10\.') { throw "$app requires the .NET 10 desktop runtime pack" }
    if ($frameworkIdentity -and $frameworkIdentity -ne $identity) { throw 'Desktop and CLI runtime pack versions differ' }
    $frameworkIdentity = $identity
    $deps = Get-Content -LiteralPath (Join-Path $root "$app.deps.json") -Raw | ConvertFrom-Json -AsHashtable
    if ($deps.runtimeTarget.name -notmatch '/win-x64$') { throw "$app is not win-x64" }
    # The published manifest contains the resolved runtime, native and localized assets.
    foreach ($library in $deps.targets[$deps.runtimeTarget.name].Values) {
        foreach ($kind in @('runtime', 'native', 'resources')) {
            if (-not $library.ContainsKey($kind)) { continue }
            foreach ($asset in $library[$kind].GetEnumerator()) {
                if ($asset.Key.EndsWith('/_._')) { continue }
                $relative = [IO.Path]::GetFileName($asset.Key)
                if ($kind -eq 'resources') { $relative = Join-Path $asset.Value.locale $relative }
                Assert-File $relative
            }
        }
    }
}
# User state, audio, test output, secrets and driver packages never belong in a release.
foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    if ($item.Name -match '^(settings|agent|samples|workbench|audio-workbench|secrets)\.json|^\.env($|\.)|^appsettings\.(Local|Development)\.json$|(^|[._-])(secrets?|credentials?|api[-_]?key)([._-]|$)|^(clones?|clone-library|clone-samples|driver-audit|TestResults)$|vbcable|vbaudio' -or
        $item.Extension -in @('.wav', '.mp3', '.pcm', '.trx', '.binlog', '.tmp', '.dmp', '.mdmp', '.sys', '.inf', '.cat', '.zip')) {
        throw 'Publish contains a forbidden user-data, test-output, or driver file (name withheld).'
    }
}
# Run in a separate working directory without SDK/global runtime fallback.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('SeleVox-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$process = $null
try {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'SeleVox.Cli.exe'))
    $info.ArgumentList.Add('--help')
    $info.WorkingDirectory = $scratch
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment['DOTNET_ROOT'] = $scratch
    $info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) { throw 'Published CLI --help timed out' }
    $help = $stdout.GetAwaiter().GetResult()
    $null = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or $help -notmatch 'SeleVox' -or $help -notmatch 'mcp') { throw 'Published CLI --help failed' }
    Write-Host 'Publish integrity and actual CLI --help passed.'
} finally {
    if ($process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($scratch).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary smoke path' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
