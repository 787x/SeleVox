#requires -Version 7.2
# Runs only after the ordinary smoke, in its newly created standard-user account.
param([Parameter(Mandatory)][string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:SELEVOX_INSTALLER_DISPOSABLE -ne '1') { throw 'Disposable Actions account required' }
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$fixtures = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$keyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{BDC7B521-DF8E-44A7-AD0A-4765EAC38629}_is1'
$data = Join-Path $env:LOCALAPPDATA 'SeleVox'
if ((Test-Path -LiteralPath $keyPath) -or (Test-Path -LiteralPath $data)) { throw 'Upgrade account is not clean' }
$root = Join-Path $env:TEMP ('SeleVox-upgrades-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $root '中文 upgrade app'
New-Item -ItemType Directory -Path $root,$data | Out-Null
$programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'SeleVox'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:operation = 0
function Read-Run {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { if ($key) { $key.GetValue('SeleVox') } } finally { if ($key) { $key.Dispose() } }
}
function Tree-Hashes([string]$Directory) {
    $files = [ordered]@{}
    if (Test-Path -LiteralPath $Directory) { foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName) { $files[[IO.Path]::GetRelativePath($Directory, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName).Hash } }
    $files
}
function Snapshot {
    $registry = [ordered]@{}
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{BDC7B521-DF8E-44A7-AD0A-4765EAC38629}_is1')
    try { if ($key) { foreach ($name in $key.GetValueNames() | Sort-Object) { $registry[$name] = $key.GetValue($name) } } } finally { if ($key) { $key.Dispose() } }
    [ordered]@{ Program = (Tree-Hashes $target); Registry = $registry; Run = (Read-Run); Shortcuts = (Tree-Hashes $programs); Data = (Tree-Hashes $data) } | ConvertTo-Json -Depth 8 -Compress
}
function Setup-Path([string]$Tag, [string]$Variant = '') {
    $v = Get-ReleaseVersion $Tag
    $directory = if ($Variant) { Join-Path $fixtures $Variant } else { Join-Path $fixtures "$Tag/setup" }
    Join-Path $directory $v.SetupName
}
function Invoke-Setup([string]$Tag, [string]$Reject = '', [string]$Variant = '') {
    $script:operation++
    $log = Join-Path $root "setup-$script:operation.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="'+$target+'"'), ('/LOG="'+$log+'"'))
    $before = if ($Reject) { Snapshot } else { $null }
    $process = Start-Process -FilePath (Setup-Path $Tag $Variant) -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $text = if (Test-Path -LiteralPath $log) { [IO.File]::ReadAllText($log) } else { '' }
    if ($Reject) {
        if ($process.ExitCode -eq 0 -or $text -notmatch $Reject) { Get-Content -LiteralPath $log -Tail 40; throw "Expected rejection '$Reject' for $Tag; exit=$($process.ExitCode)" }
        if ((Snapshot) -cne $before) { throw "Rejected $Tag changed installed files, uninstall info, shortcuts, startup or user data" }
        Write-Host "Actual Setup safely rejected: $Tag ($Reject); complete installed-state snapshot unchanged."
    } elseif ($process.ExitCode -ne 0) { Get-Content -LiteralPath $log -Tail 45; throw "Setup $Tag failed: $($process.ExitCode)" }
}
function Assert-Installed([string]$Tag) {
    $v = Get-ReleaseVersion $Tag
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $fixtures "$Tag/publish") -File -Recurse -Force) {
        $path = Join-Path $target ([IO.Path]::GetRelativePath((Join-Path $fixtures "$Tag/publish"), $file.FullName))
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Wrong installed payload after $Tag" }
    }
    if ((Get-ItemPropertyValue -LiteralPath $keyPath -Name DisplayVersion) -cne $v.SemVer) { throw 'Uninstall version mismatch' }
    if ((Test-Path -LiteralPath (Join-Path $target '.selevox-upgrade-pending')) -or (Test-Path -LiteralPath (Join-Path $target '.selevox-upgrade-state'))) { throw 'Successful installation retained recovery state' }
    if ([IO.File]::ReadAllText((Join-Path $target 'user-owned.txt')) -cne 'user-owned-keep') { throw 'Unknown program-directory file was removed or modified' }
    if ((Tree-Hashes $data | ConvertTo-Json -Compress) -cne $script:dataSnapshot) { throw 'Upgrade modified settings, Agent, workbench or clone fixture bytes' }
    if ((Read-Run) -cne ('"'+(Join-Path $target 'SeleVox.App.exe')+'" --background')) { throw 'Upgrade changed saved startup choice' }
    Write-Host "Actual Setup installed and verified all $Tag payload hashes; unknown file, user data and startup preserved."
}
function Uninstall {
    $process = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $keyPath)) { throw 'Upgrade-test uninstall failed' }
}
function Assert-PendingAppBlocked {
    if (-not (Test-Path -LiteralPath (Join-Path $target '.selevox-upgrade-pending'))) { throw 'Interrupted installation lost recovery marker' }
    if (-not (Test-Path -LiteralPath (Join-Path $target 'SeleVox.App.exe'))) { Write-Host 'Pending installation has no runnable desktop; repair required.'; return }
    $app = Start-Process -FilePath (Join-Path $target 'SeleVox.App.exe') -ArgumentList '--agent-background' -WindowStyle Hidden -PassThru
    try { if (-not $app.WaitForExit(15000)) { throw 'Incomplete installation started a resident desktop instance' } }
    finally { if (-not $app.HasExited) { $app.Kill($true); $app.WaitForExit() }; $app.Dispose() }
}
try {
    foreach ($relative in @('settings.json', 'agent.json', 'audio-workbench.json', 'clone-library/sample.wav')) {
        $path = Join-Path $data $relative
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'test-owned-preserved-'+$relative)
    }
    $script:dataSnapshot = Tree-Hashes $data | ConvertTo-Json -Compress
    Invoke-Setup 'v0.0.1'
    [IO.File]::WriteAllText((Join-Path $target 'user-owned.txt'), 'user-owned-keep')
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -LiteralPath $runKey -Name SeleVox -Value ('"'+(Join-Path $target 'SeleVox.App.exe')+'" --background')
    Assert-Installed 'v0.0.1'
    Invoke-Setup 'v0.0.1'; Assert-Installed 'v0.0.1'
    # Unrecognized and inconsistent version metadata must not reach file copying.
    Set-ItemProperty -LiteralPath $keyPath -Name DisplayVersion -Value 'unknown-version'
    Invoke-Setup 'v0.0.2-rc.1' 'Installed version is unrecognized'
    Set-ItemProperty -LiteralPath $keyPath -Name DisplayVersion -Value '0.0.2-rc.1'
    Invoke-Setup 'v0.0.2-rc.1' 'metadata is inconsistent'
    Set-ItemProperty -LiteralPath $keyPath -Name DisplayVersion -Value '0.0.1'
    $record = Join-Path $target '.selevox-payload'
    $recordBytes = [IO.File]::ReadAllBytes($record)
    try { [IO.File]::AppendAllText($record, 'tampered'); Invoke-Setup 'v0.0.2-rc.1' 'metadata is inconsistent' }
    finally { [IO.File]::WriteAllBytes($record, $recordBytes) }
    Invoke-Setup 'v0.0.2-rc.1'; Assert-Installed 'v0.0.2-rc.1'
    Invoke-Setup 'v0.0.1' 'Downgrade refused: installed 0.0.2-rc.1, attempted 0.0.1'
    $obsolete = Join-Path $target 'UpgradeFixture/obsolete-component.dll'
    $bytes = [IO.File]::ReadAllBytes($obsolete)
    try { [IO.File]::AppendAllText($obsolete, 'user-modification'); Invoke-Setup 'v0.0.2-rc.2' 'retired program component was modified' }
    finally { [IO.File]::WriteAllBytes($obsolete, $bytes) }
    $lock = [IO.File]::Open($obsolete, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Invoke-Setup 'v0.0.2-rc.2' 'Installation files are in use' } finally { $lock.Dispose() }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SeleVoxUpgradeAcl {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static bool DeleteAccessDenied(string path) {
        var handle = CreateFileW(path, 0x80010000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return Marshal.GetLastWin32Error() == 5;
        CloseHandle(handle); return false;
    }
}
'@
    $parentPath = Split-Path $obsolete
    $parentAcl = Get-Acl -LiteralPath $parentPath
    $restrictedParent = Get-Acl -LiteralPath $parentPath
    $restrictedParent.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User, 'DeleteSubdirectoriesAndFiles', 'Deny'))
    $acl = Get-Acl -LiteralPath $obsolete
    $deny = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User, 'Delete', 'Deny')
    $restricted = Get-Acl -LiteralPath $obsolete; $restricted.AddAccessRule($deny)
    try {
        Set-Acl -LiteralPath $obsolete -AclObject $restricted
        Set-Acl -LiteralPath $parentPath -AclObject $restrictedParent
        if (-not [SeleVoxUpgradeAcl]::DeleteAccessDenied($obsolete)) { throw 'ACL fixture did not deny DELETE access; refusing to claim a permission test' }
        Invoke-Setup 'v0.0.2-rc.2' 'retired program component is locked or lacks delete permission'
    } finally {
        if (Test-Path -LiteralPath $obsolete) { Set-Acl -LiteralPath $obsolete -AclObject $acl }
        Set-Acl -LiteralPath $parentPath -AclObject $parentAcl
    }
    Invoke-Setup 'v0.0.2-rc.2'; Assert-Installed 'v0.0.2-rc.2'
    if (Test-Path -LiteralPath $obsolete) { throw 'Removed old component remained in the runtime directory' }
    Invoke-Setup 'v0.0.2-rc.1' 'Downgrade refused: installed 0.0.2-rc.2, attempted 0.0.2-rc.1'
    Invoke-Setup 'v0.0.2'; Assert-Installed 'v0.0.2'
    Invoke-Setup 'v0.0.2-rc.2' 'Downgrade refused: installed 0.0.2, attempted 0.0.2-rc.2'
    Invoke-Setup 'v0.0.2'; Assert-Installed 'v0.0.2'
    # Failure injected only into a separate CI fixture definition, after payload copy.
    $failed = $false
    try { Invoke-Setup 'v0.0.3-rc.1' '' 'Fail' } catch { $failed = $true }
    if (-not $failed -or [IO.File]::ReadAllText((Join-Path $root "setup-$script:operation.log")) -notmatch 'TEST injected installation failure') { throw 'Injected failure did not exercise its intended checkpoint' }
    Assert-PendingAppBlocked
    Invoke-Setup 'v0.0.2' 'unfinished installation must first be repaired'
    Invoke-Setup 'v0.0.3-rc.1'; Assert-Installed 'v0.0.3-rc.1'
    if (Test-Path -LiteralPath (Join-Path $target 'UpgradeFixture/stable-only.dll')) { throw 'Retired component survived failure recovery' }
    & (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $target
    Uninstall
    if ([IO.File]::ReadAllText((Join-Path $target 'user-owned.txt')) -cne 'user-owned-keep') { throw 'Uninstall removed unknown file after upgrade' }
    # ssPostInstall exceptions are handled as warnings by stock Inno. The
    # product's committed flag must turn them into nonzero exit and no launch.
    $target = Join-Path $root 'late failure install'
    Invoke-Setup 'v0.0.2'
    [IO.File]::WriteAllText((Join-Path $target 'user-owned.txt'), 'user-owned-keep')
    Set-ItemProperty -LiteralPath $runKey -Name SeleVox -Value ('"'+(Join-Path $target 'SeleVox.App.exe')+'" --background')
    $failed = $false
    try { Invoke-Setup 'v0.0.3-rc.1' '' 'Verify' } catch { $failed = $true }
    if (-not $failed -or [IO.File]::ReadAllText((Join-Path $root "setup-$script:operation.log")) -notmatch 'TEST injected installation failure') { throw 'Late verification failure incorrectly reported success or missed its checkpoint' }
    Assert-PendingAppBlocked
    Invoke-Setup 'v0.0.3-rc.1'; Assert-Installed 'v0.0.3-rc.1'
    Uninstall
    # A separate clean directory exercises an actual forced Setup interruption.
    $target = Join-Path $root 'interrupted install'
    Invoke-Setup 'v0.0.2'
    [IO.File]::WriteAllText((Join-Path $target 'user-owned.txt'), 'user-owned-keep')
    Set-ItemProperty -LiteralPath $runKey -Name SeleVox -Value ('"'+(Join-Path $target 'SeleVox.App.exe')+'" --background')
    $log = Join-Path $root 'interruption.log'
    $process = Start-Process -FilePath (Setup-Path 'v0.0.3-rc.1' 'Interrupt') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="'+$target+'"'), ('/LOG="'+$log+'"')) -WindowStyle Hidden -PassThru
    try {
        $ready = $false; $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
            if (Test-Path -LiteralPath $log) {
                $stream = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $reader = [IO.StreamReader]::new($stream)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                if ($text.Contains('TEST interruption checkpoint')) { $ready = $true; break }
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) { throw 'Interrupt fixture never reached its post-copy checkpoint' }
        # Only this test-created Setup process tree is terminated; no user App/tasks.
        $process.Kill($true); $process.WaitForExit()
    } finally { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    Assert-PendingAppBlocked
    Invoke-Setup 'v0.0.3-rc.1'; Assert-Installed 'v0.0.3-rc.1'
    if (Test-Path -LiteralPath (Join-Path $target 'UpgradeFixture/stable-only.dll')) { throw 'Retired component survived interrupted upgrade recovery' }
    Uninstall
    Write-Host 'REAL MULTI-VERSION SETUP MATRIX PASSED: patch upgrade/downgrade; RC1/RC2; RC/Stable; repair; ownership; locks; ACL; failure and process interruption recovery; unknown files and user-data fixtures preserved.'
} finally {
    # No recursive cleanup or credential access; only this disposable VM owns the
    # installation, synthetic state, and any retained failure evidence.
    if (Test-Path -LiteralPath $root) { Write-Host "Upgrade evidence directory: $root" }
}
