#requires -Version 7.2
# Destructive install/uninstall integration test: only a disposable Actions account.
param([Parameter(Mandatory)][string]$VersionTag, [Parameter(Mandatory)][string]$SetupDirectory, [Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:SELEVOX_INSTALLER_DISPOSABLE -ne '1') { throw 'Run only in a disposable GitHub Actions Windows job; never against a developer account.' }
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$setup = (Resolve-Path -LiteralPath (Join-Path $SetupDirectory $version.SetupName)).Path
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{BDC7B521-DF8E-44A7-AD0A-4765EAC38629}_is1'
$data = Join-Path $env:LOCALAPPDATA 'SeleVox'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'SeleVox'
if ((Test-Path -LiteralPath $data) -or (Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $shortcut) -or (Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox -ErrorAction SilentlyContinue)) { throw 'Account is not clean; refusing to touch existing state' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('SeleVox-install-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $root '中文 installed app'
New-Item -ItemType Directory -Path $root,$data | Out-Null
$sentinel = Join-Path $data 'installer-test-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'preserve-me')
function Run-Setup([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer process failed: $($process.ExitCode)" }
}
try {
    Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $target + '"'), ('/LOG="' + (Join-Path $root 'install.log') + '"'))
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'Missing current-user uninstall registration' }
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $installed = Join-Path $target ([IO.Path]::GetRelativePath($source, $file.FullName))
        if (-not (Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Installed file missing or hash mismatch' }
    }
    & (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $target
    foreach ($name in @('SeleVox.lnk', 'Uninstall SeleVox.lnk')) {
        if (-not (Test-Path -LiteralPath (Join-Path $shortcut $name))) { throw 'Missing start menu shortcut' }
    }
    if (Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox -ErrorAction SilentlyContinue) { throw 'Installation enabled startup without consent' }
    if (Get-Process -Name SeleVox.App -ErrorAction SilentlyContinue) { throw 'Silent installation started desktop/services' }
    # Same-AppId upgrade; no second uninstall entry and no data reset.
    Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    Run-Setup (Join-Path $target 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ((Test-Path -LiteralPath (Join-Path $target 'SeleVox.App.exe')) -or (Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $shortcut)) { throw 'Uninstall left program or shortcut/registration' }
    if ([IO.File]::ReadAllText($sentinel) -cne 'preserve-me') { throw 'Uninstall deleted user data' }
    Write-Host 'Actual current-user install, full payload hashes, installed CLI, reinstallation, uninstall and data preservation passed. Runner token is not proof of non-administrator behavior.'
} finally {
    # Preserve failed installations/logs for investigation. Never recurse over user data.
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
    if ((Test-Path -LiteralPath $data) -and -not (Get-ChildItem -LiteralPath $data -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $data }
    if (-not (Test-Path -LiteralPath $uninstallKey)) {
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not [IO.Path]::GetFullPath($root).StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
        Remove-Item -LiteralPath $root -Force -Recurse
    }
}
