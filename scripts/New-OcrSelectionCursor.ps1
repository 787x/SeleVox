# Rebuild the original pixel artwork without external image tools.
# Each frame has an opaque center at its integer hotspot; transparent padding
# keeps the 23-DIP cross compact. Black / white / cyan strokes remain visible
# against light, dark and textured backgrounds. No system cursor is changed.
param([string]$OutputPath = (Join-Path $PSScriptRoot '../src/SeleVox.App/Assets/OcrSelection.cur'))
$ErrorActionPreference = 'Stop'
$sizes = @(32, 48, 64, 96)
$frames = foreach ($size in $sizes) {
    $buffer = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($buffer)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    1..4 | ForEach-Object { $writer.Write([int]0) }
    $center = [int]($size / 2)
    $scale = $size / 32.0
    $opaque = [bool[,]]::new($size, $size)
    for ($y = $size - 1; $y -ge 0; --$y) {
        for ($x = 0; $x -lt $size; ++$x) {
            $dx = [Math]::Abs($x - $center) / $scale
            $dy = [Math]::Abs($y - $center) / $scale
            $distance = [Math]::Min($dx, $dy)
            $extent = [Math]::Max($dx, $dy)
            $color = [byte[]]@(0, 0, 0, 0)
            if ($distance -le 2 -and $extent -le 11) {
                $opaque[$x,$y] = $true
                $color = [byte[]]@(16, 16, 16, 255)
                if ($distance -le 1 -and $extent -le 10) { $color = [byte[]]@(255, 255, 255, 255) }
                if ($distance -lt 0.5 -and $extent -le 9) { $color = [byte[]]@(255, 200, 0, 255) }
            }
            $writer.Write($color)
        }
    }
    for ($y = $size - 1; $y -ge 0; --$y) {
        $row = [byte[]]::new($maskStride)
        for ($x = 0; $x -lt $size; ++$x) {
            if (!$opaque[$x,$y]) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (128 -shr ($x % 8)) }
        }
        $writer.Write($row)
    }
    $writer.Flush()
    ,$buffer.ToArray()
    $writer.Dispose(); $buffer.Dispose()
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null
$file = [IO.BinaryWriter]::new([IO.File]::Create($OutputPath))
try {
    $file.Write([uint16]0); $file.Write([uint16]2); $file.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; ++$i) {
        $size = $sizes[$i]
        $file.Write([byte]$size); $file.Write([byte]$size); $file.Write([uint16]0)
        $file.Write([uint16]($size / 2)); $file.Write([uint16]($size / 2))
        $file.Write([uint32]$frames[$i].Length); $file.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $file.Write([byte[]]$frame) }
} finally { $file.Dispose() }
