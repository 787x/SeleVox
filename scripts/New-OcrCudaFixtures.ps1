#requires -Version 7.2
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\ocr-fixtures'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function New-OcrFixture([string]$Name, [string]$Text, [string]$FontName, [float]$FontSize) {
    $path = Join-Path $OutputDirectory "$Name.png"
    $bitmap = [Drawing.Bitmap]::new(780, 240)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $font = [Drawing.Font]::new($FontName, $FontSize, [Drawing.GraphicsUnit]::Pixel)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $y = 24
        foreach ($line in $Text.Split("`n")) {
            $graphics.DrawString($line, $font, [Drawing.Brushes]::Black, 24, $y)
            $y += $FontSize + 12
        }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        [IO.File]::WriteAllText("$path.txt", $Text)
    } finally {
        $font.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-OcrFixture 'mixed' "SeleVox OCR Test`n订单编号 A-2048`n总计：128.50 元" 'Microsoft YaHei UI' 36
New-OcrFixture 'code' "int count = 42;`nreturn count + 1;" 'Consolas' 28
New-OcrFixture 'small' "Small text check`nVersion 1.24.509" 'Segoe UI' 15
New-OcrFixture 'blank' '' 'Segoe UI' 15
