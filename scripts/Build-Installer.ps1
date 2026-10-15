#requires -Version 7.2
param([Parameter(Mandatory)][string]$VersionTag, [Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$OutputDirectory, [ValidateSet('None', 'Fail', 'Interrupt', 'Verify')][string]$TestFault = 'None')
$ErrorActionPreference = 'Stop'
if ($TestFault -ne 'None' -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:SELEVOX_INSTALLER_DISPOSABLE -ne '1')) { throw 'Fault fixtures require a disposable Actions VM' }
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$version = Get-ReleaseVersion $VersionTag
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must be outside publish directory' }
& (Join-Path $PSScriptRoot 'Test-Publish.ps1') -PublishDirectory $source
Assert-PublishVersion $source $version
$compiler = & (Join-Path $PSScriptRoot 'Install-InnoSetup.ps1')
if (Test-Path -LiteralPath (Join-Path $output $version.SetupName)) { throw 'Refusing to overwrite installer' }
$verification = Join-Path ([IO.Path]::GetTempPath()) ('SeleVox-payload-' + [guid]::NewGuid().ToString('N') + '.iss')
try {
    $inventory = [Collections.Generic.List[string]]::new()
    $inventory.Add('SeleVox payload v1')
    $inventory.Add($version.SemVer)
    $checks = [Collections.Generic.List[string]]::new()
    $checks.Add('procedure VerifyPayload;')
    $checks.Add('begin')
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse -Force | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace("'", "''").Replace('{', '{{')
        $rawRelative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ($rawRelative -match '[=\r\n]' -or $rawRelative.StartsWith('.selevox-', [StringComparison]::OrdinalIgnoreCase) -or $rawRelative -match '(?i)^unins') { throw 'Publish contains a reserved installer path' }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $inventory.Add("$rawRelative=$hash")
        $checks.Add("  if not FileExists(ExpandConstant('{app}\$relative')) then RaiseException('Installed payload is incomplete. Run Setup again after releasing file locks.');")
        $checks.Add("  if CompareText(GetSHA256OfFile(ExpandConstant('{app}\$relative')), '$hash') <> 0 then RaiseException('Installed payload hash mismatch. Run Setup again after releasing file locks.');")
    }
    $checks.Add('end;')
    $manifest = $verification + '.manifest'
    [IO.File]::WriteAllLines($manifest, $inventory, [Text.UTF8Encoding]::new($true))
    $manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllLines($verification, $checks, [Text.UTF8Encoding]::new($true))
    $definition = Join-Path $PSScriptRoot '..\installer\SeleVox.iss'
    if ($TestFault -ne 'None') {
        $definition = $verification + '.fixture.iss'
        $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\installer\SeleVox.iss'))
        $include = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer\UpgradeSafety.iss'))
        $text = $text.Replace('#include "UpgradeSafety.iss"', ('#include "' + $include + '"'))
        $trigger = $verification + '.trigger'
        [IO.File]::WriteAllText($trigger, 'Test-only fault checkpoint; never a product payload file.')
        $extra = 'Source: "' + $trigger + '"; DestDir: "{tmp}"; DestName: "SeleVox-fault-trigger.tmp"; Flags: deleteafterinstall; BeforeInstall: InjectUpgradeFault'
        if ($TestFault -eq 'Verify') { $text = $text.Replace('    VerifyPayload;', "    VerifyPayload;`n    InjectUpgradeFault;") }
        else { $text = $text.Replace('[Icons]', "$extra`n`n[Icons]") }
        $action = if ($TestFault -eq 'Interrupt') { "Log('TEST interruption checkpoint'); Sleep(60000);" } elseif ($TestFault -eq 'Fail') { "Log('TEST injected installation failure'); if not DeleteFile(ExpandConstant('{app}\UpgradeFixture\current-component.dll')) then RaiseException('TEST component removal failed');" } else { "RaiseException('TEST injected installation failure');" }
        $text = $text.Replace('[Code]', "[Code]`nprocedure InjectUpgradeFault;`nbegin`n  $action`nend;`n")
        [IO.File]::WriteAllText($definition, $text, [Text.UTF8Encoding]::new($true))
    }
    & $compiler "/DVersion=$($version.SemVer)" "/DNumericVersion=$($version.Numeric)" "/DVersionTag=$VersionTag" "/DPublishDirectory=$source" "/DOutputDirectory=$output" "/DPayloadVerification=$verification" "/DPayloadManifest=$manifest" "/DPayloadHash=$manifestHash" $definition
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }
} finally { foreach ($path in @($verification, ($verification + '.manifest'), ($verification + '.fixture.iss'), ($verification + '.trigger'))) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } } }
& (Join-Path $PSScriptRoot 'Test-Installer.ps1') -SetupPath (Join-Path $output $version.SetupName) -VersionTag $VersionTag
