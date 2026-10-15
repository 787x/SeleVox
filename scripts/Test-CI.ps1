#requires -Version 7.2
param(
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '..\TestResults\ci'),
    [string]$ArtifactsPath
)
$ErrorActionPreference = 'Stop'
# Do not inherit local opt-ins, paid-service flags or desktop fixture paths.
Get-ChildItem Env:SELEVOX_* | ForEach-Object { Remove-Item -LiteralPath ("Env:" + $_.Name) }
$extra = @()
if ($ArtifactsPath) { $extra = @('--artifacts-path', $ArtifactsPath) }
dotnet test (Join-Path $PSScriptRoot '..\tests\SeleVox.Tests\SeleVox.Tests.csproj') -c Release --no-build --no-restore --filter 'Category=Offline&Category!=WindowsCredentials' --results-directory $ResultsDirectory --logger 'trx;LogFileName=offline.trx' --logger 'console;verbosity=minimal' --blame-hang-timeout 3m @extra
$testExit = $LASTEXITCODE
& (Join-Path $PSScriptRoot 'Test-CIReport.ps1') -Report (Join-Path $ResultsDirectory 'offline.trx')
if ($testExit -ne 0) { throw "Offline tests failed (exit $testExit)" }
