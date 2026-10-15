#requires -Version 7.2
function Get-ReleaseVersion([string]$Tag) {
    if ($Tag -cnotmatch '\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-rc\.([1-9][0-9]*))?\z') {
        throw 'Invalid version tag; expected vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-rc.N (no leading zeros).'
    }
    $parts = @($Matches[1], $Matches[2], $Matches[3])
    foreach ($part in $parts) {
        if ([System.Numerics.BigInteger]::Parse($part) -gt 65534) { throw 'Version component exceeds .NET assembly limit 65534.' }
    }
    [pscustomobject]@{ Tag = $Tag; SemVer = $Tag.Substring(1); Numeric = ($parts -join '.') + '.0'; Prerelease = $Tag.Contains('-rc.'); SetupName = "SeleVox-$Tag-win-x64-setup.exe"; ZipName = "SeleVox-$Tag-win-x64-portable.zip" }
}
function Get-VersionProperties($Version) {
    @("-p:Version=$($Version.SemVer)", "-p:AssemblyVersion=$($Version.Numeric)", "-p:FileVersion=$($Version.Numeric)", "-p:InformationalVersion=$($Version.SemVer)", '-p:IncludeSourceRevisionInInformationalVersion=false')
}
function Assert-PublishVersion([string]$Directory, $Version) {
    foreach ($name in @('SeleVox.App', 'SeleVox.Cli', 'SeleVox.Core')) {
        $path = Join-Path $Directory "$name.dll"
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        $assembly = [Reflection.AssemblyName]::GetAssemblyName($path)
        if ($info.FileVersion -ne $Version.Numeric -or $info.ProductVersion -cne $Version.SemVer -or $assembly.Version.ToString() -ne $Version.Numeric) { throw "Release version mismatch: $name" }
        if ($name -ne 'SeleVox.Core') {
            $exe = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory "$name.exe"))
            if ($exe.FileVersion -ne $Version.Numeric -or $exe.ProductVersion -cne $Version.SemVer) { throw "Apphost version mismatch: $name" }
        }
    }
}
