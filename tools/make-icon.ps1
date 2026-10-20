$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [float]($r * 2)
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function P([float]$x, [float]$y) {
    return [System.Drawing.PointF]::new($x, $y)
}

function Render-Icon([int]$size, [string]$pngPath) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [float][Math]::Max(1, $size * 0.025)
    $tile = [System.Drawing.RectangleF]::new($inset, $inset, [float]($size - $inset * 2), [float]($size - $inset * 2))
    $path = New-RoundedPath $tile.X $tile.Y $tile.Width $tile.Height ([float]($size * 0.225))
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Color]::FromArgb(83, 151, 255),
        [System.Drawing.Color]::FromArgb(33, 91, 224),
        [float]52.0)
    $g.FillPath($gradient, $path)
    $edge = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(70, 255, 255, 255), [float][Math]::Max(0.8, $size * 0.022))
    $g.DrawPath($edge, $path)

    $bracket = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(250, 252, 255), [float][Math]::Max(1.1, $size * 0.075))
    $bracket.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bracket.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bracket.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $left = [float]($size * 0.255)
    $top = [float]($size * 0.265)
    $right = [float]($size * 0.745)
    $bottom = [float]($size * 0.735)
    $arm = [float]($size * 0.135)

    $g.DrawLines($bracket, [System.Drawing.PointF[]]@(
        (P $left ($top + $arm)), (P $left $top), (P ($left + $arm) $top)))
    $g.DrawLines($bracket, [System.Drawing.PointF[]]@(
        (P ($right - $arm) $top), (P $right $top), (P $right ($top + $arm))))
    $g.DrawLines($bracket, [System.Drawing.PointF[]]@(
        (P $left ($bottom - $arm)), (P $left $bottom), (P ($left + $arm) $bottom)))
    $g.DrawLines($bracket, [System.Drawing.PointF[]]@(
        (P ($right - $arm) $bottom), (P $right $bottom), (P $right ($bottom - $arm))))

    $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 108, 112))
    $dotSize = [float]($size * 0.195)
    $g.FillEllipse($dotBrush, [float]($size / 2 - $dotSize / 2), [float]($size / 2 - $dotSize / 2), $dotSize, $dotSize)
    $dotRing = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(90, 255, 255, 255), [float][Math]::Max(0.8, $size * 0.018))
    $ring = [float]($dotSize + $size * 0.07)
    $g.DrawEllipse($dotRing, [float]($size / 2 - $ring / 2), [float]($size / 2 - $ring / 2), $ring, $ring)

    $gloss = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(50, 255, 255, 255), [float][Math]::Max(1, $size * 0.035))
    $g.DrawArc($gloss, [float]($inset + $size * 0.09), [float]($inset + $size * 0.07), [float]($size * 0.8), [float]($size * 0.8), 198, 144)

    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $gloss.Dispose()
    $dotRing.Dispose()
    $dotBrush.Dispose()
    $bracket.Dispose()
    $edge.Dispose()
    $gradient.Dispose()
    $path.Dispose()
    $g.Dispose()
    $bitmap.Dispose()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$temp = Join-Path $env:TEMP ('dpsnip-icon-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$images = @()
foreach ($size in $sizes) {
    $png = Join-Path $temp ("icon-{0}.png" -f $size)
    Render-Icon $size $png
    $images += ,@($size, $png)
}

Render-Icon 256 (Join-Path $assets 'DpSnip-256.png')

$icoPath = Join-Path $assets 'DpSnip.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$images.Count)
$offset = 6 + $images.Count * 16
$payloads = @()
foreach ($image in $images) {
    $size = [int]$image[0]
    $bytes = [System.IO.File]::ReadAllBytes([string]$image[1])
    $payloads += ,$bytes
    $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($payload in $payloads) {
    $writer.Write([byte[]]$payload)
}
$writer.Flush()
$writer.Close()
$stream.Close()
Remove-Item -Recurse -Force $temp

Get-Item $icoPath | Select-Object FullName, Length
