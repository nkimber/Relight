# Render the original vector in Assets/Relight.svg to a multi-resolution Windows icon.
# Keep these Bezier coordinates in sync with that source. No third-party artwork is used.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot '..\src\Relight.App\Assets\Relight.ico'
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)
    $outer = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $outer.AddBezier(32,3,40,18,54,24,54,39)
    $outer.AddBezier(54,39,54,53,44,61,32,61)
    $outer.AddBezier(32,61,20,61,10,53,10,40)
    $outer.AddBezier(10,40,10,28,19,22,21,13)
    $outer.AddBezier(21,13,23,23,25,25,28,29)
    $outer.AddBezier(28,29,33,22,36,15,32,3)
    $outer.CloseFigure()
    $inner = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $inner.AddBezier(33,29,33,39,24,40,24,47)
    $inner.AddBezier(24,47,24,53,28,56,33,56)
    $inner.AddBezier(33,56,39,56,42,51,42,46)
    $inner.AddBezier(42,46,42,40,37,35,33,29)
    $inner.CloseFigure()
    $flame = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#c76a09'))
    $center = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#fff1c2'))
    $graphics.FillPath($flame, $outer)
    $graphics.FillPath($center, $inner)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $flame.Dispose()
    $center.Dispose()
    $outer.Dispose()
    $inner.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
$output = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally {
    $writer.Dispose()
}
Write-Output "Created $iconPath"
