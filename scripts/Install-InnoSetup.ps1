#requires -Version 7.2
param([string]$ToolsDirectory = (Join-Path $PSScriptRoot '..\artifacts\tools\inno-6.7.3'))
$ErrorActionPreference = 'Stop'
# Official immutable release, SHA-256 from GitHub release asset metadata.
$hash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$directory = [IO.Path]::GetFullPath($ToolsDirectory)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$installer = Join-Path $directory 'innosetup-6.7.3.exe'
if (-not (Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $installer
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'Inno Setup download SHA-256 mismatch' }
$target = Join-Path $directory 'compiler'
$process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/SP-', ('/DIR="' + $target + '"'), '/NOICONS') -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Inno Setup tool installation failed' }
$compiler = Join-Path $target 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Missing Inno Setup compiler' }
Write-Output $compiler
