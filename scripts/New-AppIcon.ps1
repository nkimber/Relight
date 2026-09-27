# Render the original vector in Assets/Relight.svg to a multi-resolution Windows icon.
# Keep these Bezier coordinates in sync with that source. No third-party artwork is used.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '..\src\Relight.App\Assets'
$variants = @(
    @{ Name = 'Relight.ico'; Flame = '#c76a09'; Center = '#fff1c2'; Badge = 'none' },
    @{ Name = 'Relight.Attention.ico'; Flame = '#b42318'; Center = '#fff1ee'; Badge = 'attention' },
    @{ Name = 'Relight.Recovering.ico'; Flame = '#1765b3'; Center = '#e9f5ff'; Badge = 'recovering' },
    @{ Name = 'Relight.Paused.ico'; Flame = '#66717c'; Center = '#eef0f2'; Badge = 'paused' }
)
foreach ($variant in $variants) {
$iconPath = Join-Path $assetDirectory $variant.Name
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
    $flame = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($variant.Flame))
    $center = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($variant.Center))
    $graphics.FillPath($flame, $outer)
    $graphics.FillPath($center, $inner)
    if ($variant.Badge -ne 'none') {
        $badge = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($variant.Flame))
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 3.5)
        $graphics.FillEllipse($badge, 43, 43, 19, 19)
        switch ($variant.Badge) {
            'attention' {
                $graphics.DrawLine($pen, 52.5, 47, 52.5, 54)
                $graphics.FillEllipse($white, 50.8, 56.2, 3.4, 3.4)
            }
            'recovering' {
                $graphics.DrawArc($pen, 47, 47, 11, 11, 45, 275)
                $graphics.FillPolygon($white, @(
                    [System.Drawing.PointF]::new(55, 46),
                    [System.Drawing.PointF]::new(60, 48),
                    [System.Drawing.PointF]::new(54, 51)))
            }
            'paused' {
                $graphics.FillRectangle($white, 48, 47, 3, 11)
                $graphics.FillRectangle($white, 54, 47, 3, 11)
            }
        }
        $pen.Dispose()
        $white.Dispose()
        $badge.Dispose()
    }
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
}
