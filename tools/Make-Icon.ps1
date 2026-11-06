# Generates the HiddenReader application icon (multi-size .ico).
# Design: dark grey-blue rounded tile, light open book, blue bookmark ribbon.
# ASCII-only on purpose: Windows PowerShell 5.1 parses BOM-less non-ASCII .ps1 with the ANSI codepage.
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\HiddenReader.App\Assets\HiddenReader.ico'),
    [string]$PreviewFile = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-Pen([System.Drawing.Color]$color, [double]$width) {
    $pen = New-Object System.Drawing.Pen($color, ([float]$width))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $pen
}

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- dark grey-blue tile with rounded corners ---
    $r = [Math]::Max(1.0, $S * 0.18)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $tile.AddArc(0, 0, $d, $d, 180, 90)
    $tile.AddArc($S - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($S - $d, $S - $d, $d, $d, 0, 90)
    $tile.AddArc(0, $S - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $S)),
        [System.Drawing.Color]::FromArgb(255, 51, 68, 90),
        [System.Drawing.Color]::FromArgb(255, 29, 40, 54))
    $g.FillPath($grad, $tile)
    $grad.Dispose()
    $tile.Dispose()

    # --- open book: two light pages meeting at the spine ---
    $pageLight = [System.Drawing.Color]::FromArgb(255, 236, 242, 248)
    $pageShade = [System.Drawing.Color]::FromArgb(255, 205, 216, 230)
    $spine = [System.Drawing.Color]::FromArgb(255, 26, 36, 48)

    $spineTop = [float](0.500 * $S)
    $spineTopY = [float](0.255 * $S)
    $spineBottomY = [float](0.695 * $S)
    $outerTopY = [float](0.320 * $S)
    $outerBottomY = [float](0.755 * $S)
    $leftX = [float](0.135 * $S)
    $rightX = [float](0.865 * $S)

    $left = @(
        (New-Object System.Drawing.PointF($spineTop, $spineTopY)),
        (New-Object System.Drawing.PointF($leftX, $outerTopY)),
        (New-Object System.Drawing.PointF($leftX, $outerBottomY)),
        (New-Object System.Drawing.PointF($spineTop, $spineBottomY)))
    $right = @(
        (New-Object System.Drawing.PointF($spineTop, $spineTopY)),
        (New-Object System.Drawing.PointF($rightX, $outerTopY)),
        (New-Object System.Drawing.PointF($rightX, $outerBottomY)),
        (New-Object System.Drawing.PointF($spineTop, $spineBottomY)))

    $brushLeft = New-Object System.Drawing.SolidBrush($pageShade)
    $brushRight = New-Object System.Drawing.SolidBrush($pageLight)
    $g.FillPolygon($brushLeft, [System.Drawing.PointF[]]$left)
    $g.FillPolygon($brushRight, [System.Drawing.PointF[]]$right)
    $brushLeft.Dispose()
    $brushRight.Dispose()

    # spine line keeps the two pages readable as one book, even at 16 px
    $penSpine = New-Pen $spine ([Math]::Max(1.0, $S * 0.045))
    $g.DrawLine($penSpine, [float](0.500 * $S), [float](0.255 * $S), [float](0.500 * $S), [float](0.695 * $S))
    $penSpine.Dispose()

    # --- blue bookmark ribbon hanging over the right page ---
    $bw = [Math]::Max(2.0, $S * 0.085)
    $bx = 0.585 * $S
    $by = 0.235 * $S
    $bh = 0.360 * $S
    $ribbon = @(
        (New-Object System.Drawing.PointF($bx, $by)),
        (New-Object System.Drawing.PointF(($bx + $bw), $by)),
        (New-Object System.Drawing.PointF(($bx + $bw), ($by + $bh))),
        (New-Object System.Drawing.PointF(($bx + $bw / 2), ($by + $bh * 0.82))),
        (New-Object System.Drawing.PointF($bx, ($by + $bh))))
    $brushRibbon = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 61, 123, 232))
    $g.FillPolygon($brushRibbon, [System.Drawing.PointF[]]$ribbon)
    $brushRibbon.Dispose()

    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

# 32bpp DIB entry: BITMAPINFOHEADER + bottom-up BGRA rows + zeroed AND mask.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $imageSize = $stride * $h + $maskStride * $h
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40)
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($stride * $h))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $stride) }
    $zero = New-Object byte[] ($maskStride * $h)
    $bw.Write($zero)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

$entries = @()
$bitmaps = @{}
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $bitmaps[$s] = $bmp
    if ($s -ge 128) { $blob = Get-PngBytes $bmp } else { $blob = Get-DibBytes $bmp }
    $entries += [pscustomobject]@{ Size = $s; Bytes = $blob }
}

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([int16]0)
$bw.Write([int16]1)
$bw.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]$e.Bytes.Length)
    $bw.Write([int]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e.Bytes) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

if ($PreviewFile) {
    # Preview sheet: native sizes on top row, 16/24/32 upscaled 8x with nearest neighbour below.
    $sheetW = 640
    $sheetH = 320
    $sheet = New-Object System.Drawing.Bitmap($sheetW, $sheetH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 120, 120, 128))
    $x = 8
    foreach ($s in $sizes) {
        $g.DrawImage($bitmaps[$s], $x, 8, $s, $s)
        $x += $s + 8
    }
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $x = 8
    foreach ($s in @(16, 24, 32)) {
        $scaled = $s * 8
        $g.DrawImage($bitmaps[$s], $x, 290 - $scaled, $scaled, $scaled)
        $x += $scaled + 16
    }
    $g.Dispose()
    $sheet.Save($PreviewFile, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
}

foreach ($s in $sizes) { $bitmaps[$s].Dispose() }
Write-Output ("wrote " + $OutFile + " (" + $entries.Count + " sizes)")
