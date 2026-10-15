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
$shortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs, [Environment+SpecialFolderOption]::Create)) 'SeleVox'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory, [Environment+SpecialFolderOption]::Create)) 'SeleVox.lnk'
if ((Test-Path -LiteralPath $data) -or (Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $shortcut) -or (Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox -ErrorAction SilentlyContinue)) { throw 'Account is not clean; refusing to touch existing state' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('SeleVox-install-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $root '中文 installed app'
New-Item -ItemType Directory -Path $root,$data | Out-Null
$sentinel = Join-Path $data 'installer-test-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'preserve-me')
function Run-Setup([string]$File, [string[]]$Arguments, [bool]$ExpectFailure = $false) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($ExpectFailure) {
        if ($process.ExitCode -eq 0) { throw 'Installer accepted locked program files' }
    } elseif ($process.ExitCode -ne 0) { throw "Installer process failed: $($process.ExitCode)" }
}
$migratedCommand = '"' + (Join-Path $target 'SeleVox.App.exe') + '" --background'
$portableCommand = '"C:\SeleVox old portable\SeleVox.App.exe" --background'
$settingsFixture = Join-Path $data 'settings.json'
try {
    # Never install into the preserved user-data directory, even via an alias.
    $dataLog = Join-Path $root 'data-rejection.log'
    Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $data + '"'), ('/LOG="' + $dataLog + '"')) $true
    if ([IO.File]::ReadAllText($dataLog) -notmatch 'Choose an empty program directory outside user data') { throw 'User-data directory rejection failed for an unexpected reason' }
    $fileSystem = New-Object -ComObject Scripting.FileSystemObject
    $shortData = $fileSystem.GetFolder($data).ShortPath
    Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $shortData + '\nested-app"')) $true
    Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $target + '"'), ('/LOG="' + (Join-Path $root 'install.log') + '"'))
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'Missing current-user uninstall registration' }
    if ((Get-ItemPropertyValue -LiteralPath $uninstallKey -Name DisplayVersion) -cne $version.SemVer) { throw 'Installed version mismatch' }
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $installed = Join-Path $target ([IO.Path]::GetRelativePath($source, $file.FullName))
        if (-not (Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Installed file missing or hash mismatch' }
    }
    & (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $target
    foreach ($name in @('SeleVox.lnk', 'Uninstall SeleVox.lnk')) {
        if (-not (Test-Path -LiteralPath (Join-Path $shortcut $name))) { throw 'Missing start menu shortcut' }
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcutTarget = $shell.CreateShortcut((Join-Path $shortcut 'SeleVox.lnk')).TargetPath
    $expectedTarget = Join-Path $target 'SeleVox.App.exe'
    if (-not (Test-Path -LiteralPath $shortcutTarget -PathType Leaf) -or $fileSystem.GetFile($shortcutTarget).ShortPath -ine $fileSystem.GetFile($expectedTarget).ShortPath) { throw "Shortcut does not target installed app: '$shortcutTarget' expected '$expectedTarget'" }
    if (Test-Path -LiteralPath $desktopShortcut) { throw 'Default installation created optional desktop shortcut' }
    if (Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox -ErrorAction SilentlyContinue) { throw 'Installation enabled startup without consent' }
    if (Get-Process -Name SeleVox.App -ErrorAction SilentlyContinue) { throw 'Silent installation started desktop/services' }
    $lockedPath = Join-Path $target 'SeleVox.Core.dll'
    $before = (Get-FileHash -LiteralPath $lockedPath).Hash
    $lock = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $lockedLog = Join-Path $root 'locked.log'
    try { Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/LOG="' + $lockedLog + '"')) $true }
    finally { $lock.Dispose() }
    if ([IO.File]::ReadAllText($lockedLog) -notmatch 'Installation files are in use or inaccessible') { throw 'Upgrade failed for a reason other than the held file lock' }
    if ((Get-FileHash -LiteralPath $lockedPath).Hash -ne $before) { throw 'Rejected upgrade changed locked payload' }
    # Synthetic Portable startup and settings belong only to this clean test account.
    $settingsText = '{"StartWithWindows":true}'
    [IO.File]::WriteAllText($settingsFixture, $settingsText)
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -LiteralPath $runKey -Name SeleVox -Value $portableCommand
    # Same-AppId upgrade; no second uninstall entry and no data reset.
    Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    if ((Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox) -cne $migratedCommand) { throw 'Portable startup was not migrated' }
    if ([IO.File]::ReadAllText($settingsFixture) -cne $settingsText) { throw 'Upgrade changed saved startup preference' }
    Run-Setup (Join-Path $target 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ((Test-Path -LiteralPath (Join-Path $target 'SeleVox.App.exe')) -or (Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $shortcut)) { throw 'Uninstall left program or shortcut/registration' }
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        if (Test-Path -LiteralPath (Join-Path $target ([IO.Path]::GetRelativePath($source, $file.FullName)))) { throw 'Uninstall left a managed payload file' }
    }
    if ([IO.File]::ReadAllText($sentinel) -cne 'preserve-me') { throw 'Uninstall deleted user data' }
    if (Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox -ErrorAction SilentlyContinue) { throw 'Uninstall left owned startup value' }
    Run-Setup $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=desktopicon', ('/DIR="' + $target + '"'))
    if (-not (Test-Path -LiteralPath $desktopShortcut)) { throw 'Explicit desktop shortcut task was not honored' }
    Set-ItemProperty -LiteralPath $runKey -Name SeleVox -Value $portableCommand
    Run-Setup (Join-Path $target 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ((Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox) -cne $portableCommand) { throw 'Uninstall removed a different Portable startup value' }
    if (Test-Path -LiteralPath $desktopShortcut) { throw 'Uninstall left the optional desktop shortcut' }
    Write-Host 'Actual current-user install, payload hashes, installed CLI, locked upgrade rejection, startup migration, owned/foreign Run cleanup, reinstallation, uninstall and data preservation passed.'
} finally {
    if (Test-Path -LiteralPath (Join-Path $root 'install.log')) { Get-Content -LiteralPath (Join-Path $root 'install.log') -Tail 45 }
    # Preserve failed installations/logs for investigation. Never recurse over user data.
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
    if (Test-Path -LiteralPath $settingsFixture) { Remove-Item -LiteralPath $settingsFixture }
    $run = Get-ItemPropertyValue -LiteralPath $runKey -Name SeleVox -ErrorAction SilentlyContinue
    if ($run -ceq $portableCommand -or $run -ceq $migratedCommand) { Remove-ItemProperty -LiteralPath $runKey -Name SeleVox }
    if ((Test-Path -LiteralPath $data) -and -not (Get-ChildItem -LiteralPath $data -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $data }
    if (-not (Test-Path -LiteralPath $uninstallKey)) {
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not [IO.Path]::GetFullPath($root).StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
        Remove-Item -LiteralPath $root -Force -Recurse
    }
}
