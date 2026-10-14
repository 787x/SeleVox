param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\pr14-portable'))
$ErrorActionPreference = 'Stop'
# Keep the WPF desktop runtime from the App publish; CLI shares the same runtime files.
dotnet publish (Join-Path $PSScriptRoot '..\src\SeleVox.Cli\SeleVox.Cli.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
dotnet publish (Join-Path $PSScriptRoot '..\src\SeleVox.App\SeleVox.App.csproj') -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
