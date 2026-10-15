#requires -Version 7.2
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\win-x64'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
    throw 'Publish requires an empty output directory. Choose a fresh directory to avoid stale or personal files.'
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ('SeleVox-publish-' + [guid]::NewGuid().ToString('N'))
try {
    # Publish independently; never silently overwrite shared dependencies.
    foreach ($project in @('Cli', 'App')) {
        dotnet publish (Join-Path $PSScriptRoot "..\src\SeleVox.$project\SeleVox.$project.csproj") -c Release -r win-x64 --self-contained true -p:SeleVoxCombinedPublish=true --artifacts-path (Join-Path $stage 'build') -o (Join-Path $stage $project)
        if ($LASTEXITCODE -ne 0) { throw "$project publish failed" }
    }
    $merged = Join-Path $stage 'merged'
    New-Item -ItemType Directory -Path $merged -Force | Out-Null
    foreach ($project in @('Cli', 'App')) {
        $root = Join-Path $stage $project
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
            $target = Join-Path $merged $relative
            if (Test-Path -LiteralPath $target) {
                if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                    throw "Conflicting shared publish dependency: $relative"
                }
            } else {
                New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target
            }
        }
    }
    & (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $merged
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Get-ChildItem -LiteralPath $merged -Force | Copy-Item -Destination $output -Recurse
    Write-Host "Validated Windows x64 self-contained publish: $output"
} finally {
    # Remove only this invocation's random staging directory under the system temp directory.
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($stage).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary staging path' }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
