#requires -Version 7.2
param([Parameter(Mandatory)][string]$Directory, [string]$ExpectedCommit)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$pinned = @{
    'cublas64_13.dll' = '1119DBCA0A808E0C8850BB4221E330DAF0B5DDB348D62D37B6AC533854723DF9'
    'cublasLt64_13.dll' = '0F5BC315FEF706B5626248EBD74E8A48A9E82FA2F0CAD38330F5F70984913107'
    'msvcp140.dll' = '7C26614E1D733892C2DEAC7E245CE115504B1D80592DD0A01B08E3E5A55F89CA'
    'vcruntime140.dll' = 'D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7'
    'vcruntime140_1.dll' = 'A7146C08F89FE5B04541AB507CDB59FF7B44534D4BA3C668A426C6450A03434E'
}
$required = @($pinned.Keys) + @('selevox_ocr.dll', 'NOTICE.txt', 'llama.cpp-LICENSE.txt', 'libcublas-LICENSE.txt', 'cuda_cudart-LICENSE.txt')
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'runtime.json') -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.commit -notmatch '^[a-f0-9]{40}$' -or ($ExpectedCommit -and $manifest.commit -cne $ExpectedCommit) -or
    $manifest.llama -cne '873e5d8e39feb34a376e0efd01bf3f665dfffeb5' -or $manifest.cuda -cne '13.4.1' -or
    $manifest.architectures -cne '86;89;120' -or $manifest.abi -ne 1) { throw 'OCR runtime provenance or version mismatch' }
if ($manifest.files.Count -ne $required.Count) { throw 'Unexpected OCR runtime manifest entries' }
foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $item.Name -notin ($required + 'runtime.json')) { throw 'Unexpected entry in OcrCuda' }
}
foreach ($name in $required) {
    $path = Join-Path $Directory $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty OCR runtime file: $name"
    }
    if ($name.EndsWith('.dll')) {
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path))
        try {
            if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid OCR PE: $name" }
            $reader.BaseStream.Position = 0x3C
            $offset = $reader.ReadUInt32()
            if ($offset -lt 64 -or $offset -gt $reader.BaseStream.Length - 24) { throw "Invalid OCR PE: $name" }
            $reader.BaseStream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "OCR DLL is not x64: $name" }
        } finally { $reader.Dispose() }
    }
    $hash = (Get-FileHash -LiteralPath $path).Hash
    if ($manifest.files[$name] -ne $hash) { throw "OCR runtime SHA-256 mismatch: $name" }
    if ($pinned.ContainsKey($name) -and $pinned[$name] -ne $hash) { throw "OCR dependency version or source mismatch: $name" }
}
Write-Host "Verified OcrCuda files for commit $($manifest.commit)"
