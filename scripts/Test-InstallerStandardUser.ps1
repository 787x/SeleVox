#requires -Version 7.2
# Creates a standard account only on an ephemeral Actions VM; never on a developer PC.
param([Parameter(Mandatory)][string]$VersionTag, [Parameter(Mandatory)][string]$SetupDirectory, [Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:SELEVOX_INSTALLER_DISPOSABLE -ne '1') { throw 'Disposable GitHub Actions runner required' }
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$userName = 'svx' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$root = Join-Path ([Environment]::GetFolderPath('CommonDocuments')) ('SeleVox-installer-' + [guid]::NewGuid().ToString('N'))
$created = $false
try {
    New-Item -ItemType Directory -Path $root,(Join-Path $root 'scripts'),(Join-Path $root 'setup'),(Join-Path $root 'publish') | Out-Null
    foreach ($name in @('Test-Installed.ps1', 'Test-Publish.ps1', 'Release-Version.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $root 'scripts') }
    Copy-Item -LiteralPath (Join-Path $SetupDirectory $version.SetupName) -Destination (Join-Path $root 'setup')
    Get-ChildItem -LiteralPath $PublishDirectory -Force | Copy-Item -Destination (Join-Path $root 'publish') -Recurse
    [IO.File]::WriteAllText((Join-Path $root 'version.txt'), $VersionTag)
    $password = ConvertTo-SecureString ('Aa1!' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
    New-LocalUser -Name $userName -Password $password -AccountNeverExpires -PasswordNeverExpires | Out-Null
    $created = $true
    Add-LocalGroupMember -SID ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')) -Member $userName
    $acl = Get-Acl -LiteralPath $root
    $rule = [Security.AccessControl.FileSystemAccessRule]::new("$env:COMPUTERNAME\$userName", 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $root -AclObject $acl
    @'
$ErrorActionPreference = 'Stop'
$env:GITHUB_ACTIONS = 'true'
$env:SELEVOX_INSTALLER_DISPOSABLE = '1'
# CreateProcessWithLogon inherits the parent environment. Resolve the loaded
# profile by the new token SID before any known-folder expansion.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$profileKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $identity.User.Value
$profilePath = Get-ItemPropertyValue -LiteralPath $profileKey -Name ProfileImagePath
$env:USERPROFILE = [Environment]::ExpandEnvironmentVariables($profilePath)
$env:USERNAME = $identity.Name.Split('\')[-1]
$env:USERDOMAIN = $env:COMPUTERNAME
$env:HOMEDRIVE = [IO.Path]::GetPathRoot($env:USERPROFILE).TrimEnd('\')
$env:HOMEPATH = $env:USERPROFILE.Substring($env:HOMEDRIVE.Length)
$env:APPDATA = Join-Path $env:USERPROFILE 'AppData\Roaming'
$env:LOCALAPPDATA = Join-Path $env:USERPROFILE 'AppData\Local'
$env:TEMP = Join-Path $env:LOCALAPPDATA 'Temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:APPDATA,$env:LOCALAPPDATA,$env:TEMP -Force | Out-Null
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Smoke must run without administrator privileges' }
& (Join-Path $PSScriptRoot 'scripts\Test-Installed.ps1') -VersionTag ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'version.txt'))) -SetupDirectory (Join-Path $PSScriptRoot 'setup') -PublishDirectory (Join-Path $PSScriptRoot 'publish')
'@ | Set-Content -LiteralPath (Join-Path $root 'run.ps1') -Encoding utf8
    Start-Service seclogon
    $credential = [Management.Automation.PSCredential]::new("$env:COMPUTERNAME\$userName", $password)
    $process = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList @('-NoProfile', '-File', ('"' + (Join-Path $root 'run.ps1') + '"')) -Credential $credential -LoadUserProfile -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root 'stdout.txt') -RedirectStandardError (Join-Path $root 'stderr.txt') -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $root 'stdout.txt')
    Get-Content -LiteralPath (Join-Path $root 'stderr.txt')
    if ($process.ExitCode -ne 0) { throw "Non-administrator installer smoke failed: $($process.ExitCode)" }
    Write-Host 'Installer smoke passed under a newly created standard user (not Administrators).'
} finally {
    if ($created) { Remove-LocalUser -Name $userName }
    # Leave profile and scratch cleanup to destruction of the disposable VM.
}
