#requires -Version 7.2
param([Parameter(Mandatory)][string]$VersionTag, [Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
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
    $checks = [Collections.Generic.List[string]]::new()
    $checks.Add('procedure VerifyPayload;')
    $checks.Add('begin')
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace("'", "''").Replace('{', '{{')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $checks.Add("  if not FileExists(ExpandConstant('{app}\$relative')) then RaiseException('Installed payload is incomplete. Run Setup again after releasing file locks.');")
        $checks.Add("  if CompareText(GetSHA256OfFile(ExpandConstant('{app}\$relative')), '$hash') <> 0 then RaiseException('Installed payload hash mismatch. Run Setup again after releasing file locks.');")
    }
    $checks.Add('end;')
    [IO.File]::WriteAllLines($verification, $checks, [Text.UTF8Encoding]::new($true))
    & $compiler "/DVersion=$($version.SemVer)" "/DNumericVersion=$($version.Numeric)" "/DVersionTag=$VersionTag" "/DPublishDirectory=$source" "/DOutputDirectory=$output" "/DPayloadVerification=$verification" (Join-Path $PSScriptRoot '..\installer\SeleVox.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }
} finally { if (Test-Path -LiteralPath $verification) { Remove-Item -LiteralPath $verification } }
& (Join-Path $PSScriptRoot 'Test-Installer.ps1') -SetupPath (Join-Path $output $version.SetupName) -VersionTag $VersionTag
