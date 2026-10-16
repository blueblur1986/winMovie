Add-Type -AssemblyName System.Drawing

$assetDirectory = Join-Path $PSScriptRoot '..\Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null

function RoundedRect([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$master = [System.Drawing.Bitmap]::new(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($master)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

$outer = RoundedRect 20 20 472 472 110
$outerBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new(20, 20, 472, 472),
    [System.Drawing.Color]::FromArgb(255, 99, 91, 231),
    [System.Drawing.Color]::FromArgb(255, 17, 39, 77), 55)
$g.FillPath($outerBrush, $outer)
$outerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(110, 236, 239, 255), 4)
$g.DrawPath($outerPen, $outer)

$film = RoundedRect 83 116 346 280 46
$filmBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new(83, 116, 346, 280),
    [System.Drawing.Color]::FromArgb(255, 16, 29, 61),
    [System.Drawing.Color]::FromArgb(255, 9, 17, 37), 90)
$g.FillPath($filmBrush, $film)
$filmPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 211, 224, 255), 10)
$g.DrawPath($filmPen, $film)

$screen = RoundedRect 130 147 252 218 22
$screenBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new(130, 147, 252, 218),
    [System.Drawing.Color]::FromArgb(255, 91, 67, 187),
    [System.Drawing.Color]::FromArgb(255, 23, 147, 191), 30)
$g.FillPath($screenBrush, $screen)

$swoosh = [System.Drawing.Drawing2D.GraphicsPath]::new()
$swoosh.AddBezier(144, 331, 192, 281, 280, 305, 370, 191)
$swooshPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(155, 173, 241, 255), 20)
$swooshPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$swooshPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawPath($swooshPen, $swoosh)

foreach ($x in @(96, 397)) {
    foreach ($y in @(158, 221, 284, 347)) {
        $hole = RoundedRect $x $y 18 29 6
        $g.FillPath([System.Drawing.Brushes]::Lavender, $hole)
        $hole.Dispose()
    }
}

$star = [System.Drawing.Drawing2D.GraphicsPath]::new()
$star.AddPolygon([System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(255, 176), [System.Drawing.Point]::new(272, 234),
    [System.Drawing.Point]::new(329, 252), [System.Drawing.Point]::new(272, 270),
    [System.Drawing.Point]::new(255, 328), [System.Drawing.Point]::new(237, 270),
    [System.Drawing.Point]::new(181, 252), [System.Drawing.Point]::new(237, 234)))
$glow = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(95, 91, 225, 255), 22)
$g.DrawPath($glow, $star)
$g.FillPath([System.Drawing.Brushes]::White, $star)

$bar = RoundedRect 145 411 222 19 9
$barBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Rectangle]::new(145, 411, 222, 19),
    [System.Drawing.Color]::FromArgb(255, 138, 199, 255),
    [System.Drawing.Color]::FromArgb(255, 161, 132, 255), 0)
$g.FillPath($barBrush, $bar)

$g.Dispose()

function IconPng([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.DrawImage($master, 0, 0, $size, $size)
    $graphics.Dispose()
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$stream.ToArray()
}

function IconBitmapData([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.DrawImage($master, 0, 0, $size, $size)
    $graphics.Dispose()
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint32]40)
    $writer.Write([int32]$size)
    $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    foreach ($unused in 1..6) { $writer.Write([uint32]0) }
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $pixel = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$pixel.B)
            $writer.Write([byte]$pixel.G)
            $writer.Write([byte]$pixel.R)
            $writer.Write([byte]$pixel.A)
        }
    }
    $maskBytesPerRow = [int](($size + 31) / 32) * 4
    $writer.Write([byte[]]::new($maskBytesPerRow * $size))
    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose()
    $bitmap.Dispose()
    return ,$bytes
}

$png = IconPng 256
[System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'WinMovie.png'), $png)
$sizes = @(16, 32, 48, 256)
$images = @($sizes | ForEach-Object { IconBitmapData $_ })
$iconPath = Join-Path $assetDirectory 'WinMovie.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $writer.Write([byte]($sizes[$i] % 256))
    $writer.Write([byte]($sizes[$i] % 256))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Dispose()
$master.Dispose()
Write-Output "Generated $iconPath"
