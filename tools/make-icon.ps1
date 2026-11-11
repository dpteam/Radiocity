# Generates assets/radiocity.ico from vector drawing code.
#
# Why a script and not a checked-in binary nobody can edit: the icon is part of
# the build, so it has to be reproducible. Run it after changing the design and
# commit both the script and the result.
#
# The ICO is written by hand in the classic BMP form (BITMAPINFOHEADER + 32-bit
# BGRA + AND mask) rather than PNG-in-ICO, because Windows XP cannot read PNG
# icon entries and XP is an explicit target of this project.

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\assets'))
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Palette: deep navy plate, cyan signal, amber accent for the loudest bar.
$bgTop    = [System.Drawing.Color]::FromArgb(255, 22, 34, 54)
$bgBottom = [System.Drawing.Color]::FromArgb(255, 10, 16, 26)
$cyan     = [System.Drawing.Color]::FromArgb(255, 55, 208, 232)
$cyanDim  = [System.Drawing.Color]::FromArgb(255, 30, 122, 150)
$amber    = [System.Drawing.Color]::FromArgb(255, 245, 165, 36)

function New-IconBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size
    $pad = $s * 0.055
    $inner = $s - (2.0 * $pad)
    $radius = $s * 0.22

    $plate = [System.Drawing.RectangleF]::new([single]$pad, [single]$pad, [single]$inner, [single]$inner)

    # Rounded plate.
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $radius * 2.0
    $path.AddArc($plate.X, $plate.Y, [single]$d, [single]$d, 180, 90)
    $path.AddArc($plate.Right - $d, $plate.Y, [single]$d, [single]$d, 270, 90)
    $path.AddArc($plate.Right - $d, $plate.Bottom - $d, [single]$d, [single]$d, 0, 90)
    $path.AddArc($plate.X, $plate.Bottom - $d, [single]$d, [single]$d, 90, 90)
    $path.CloseFigure()

    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $plate, $bgTop, $bgBottom, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $path)

    # Skyline bars: a city, and a spectrum analyser, and a waveform — one shape,
    # three readings. Heights are relative so they survive being shrunk to 16 px.
    $heights = @(0.20, 0.34, 0.46, 0.27, 0.38, 0.16)
    $count = $heights.Count
    $left = $plate.X + ($plate.Width * 0.16)
    $right = $plate.Right - ($plate.Width * 0.16)
    $span = $right - $left
    $slot = $span / $count
    $barW = $slot * 0.58
    $floorY = $plate.Bottom - ($plate.Height * 0.17)

    $gradRect = [System.Drawing.RectangleF]::new(
        [single]$left, [single]($floorY - ($plate.Height * 0.7)),
        [single]$span, [single]($plate.Height * 0.7))
    $barBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $gradRect, $cyan, $cyanDim, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)

    $amberBrush = [System.Drawing.SolidBrush]::new($amber)

    for ($i = 0; $i -lt $count; $i++) {
        $h = $plate.Height * $heights[$i]
        $x = $left + ($i * $slot) + (($slot - $barW) / 2.0)
        $rect = [System.Drawing.RectangleF]::new([single]$x, [single]($floorY - $h), [single]$barW, [single]$h)
        $b = if ($i -eq 3) { $amberBrush } else { $barBrush }
        $g.FillRectangle($b, $rect)
    }

    # Broadcast arc over the skyline: the "radio" half of the name.
    $penW = [Math]::Max(1.0, $s * 0.052)
    $pen = [System.Drawing.Pen]::new($cyan, [single]$penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $arcX = $plate.X + ($plate.Width * 0.10)
    $arcY = $plate.Y + ($plate.Height * 0.02)
    $arcW = $plate.Width * 0.80
    $arcH = $plate.Height * 0.62
    $g.DrawArc($pen, [single]$arcX, [single]$arcY, [single]$arcW, [single]$arcH, 218, 104)

    $pen2 = [System.Drawing.Pen]::new($cyan, [single]($penW * 0.72))
    $pen2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $arc2X = $plate.X + ($plate.Width * 0.26)
    $arc2Y = $plate.Y + ($plate.Height * 0.17)
    $arc2W = $plate.Width * 0.48
    $arc2H = $plate.Height * 0.40
    $g.DrawArc($pen2, [single]$arc2X, [single]$arc2Y, [single]$arc2W, [single]$arc2H, 218, 104)

    # Ground line, so the skyline does not float.
    $groundPen = [System.Drawing.Pen]::new($cyanDim, [single]([Math]::Max(1.0, $s * 0.030)))
    $g.DrawLine($groundPen, [single]$left, [single]$floorY, [single]$right, [single]$floorY)

    $pen.Dispose(); $pen2.Dispose(); $groundPen.Dispose()
    $barBrush.Dispose(); $amberBrush.Dispose()
    $brush.Dispose(); $path.Dispose(); $g.Dispose()

    return $bmp
}

# Classic ICO: one directory entry per size, each holding a bottom-up 32-bit
# DIB plus a 1-bpp AND mask. The mask is unused for 32-bit icons but the header
# still reserves space for it, and some loaders (XP's included) read it.
function Add-IcoEntry($stream, [System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $pixels = $size * $size * 4
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $maskSize = $maskRow * $size

    $bw = [System.IO.BinaryWriter]::new($stream)

    # BITMAPINFOHEADER: height is doubled to cover the mask.
    $bw.Write([uint32]40)
    $bw.Write([int32]$size)
    $bw.Write([int32]($size * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)
    $bw.Write([uint32]($pixels + $maskSize))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # BGRA, bottom-up.
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }

    # AND mask: 1 = transparent.
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = [byte[]]::new($maskRow)
        for ($x = 0; $x -lt $size; $x++) {
            if ($bmp.GetPixel($x, $y).A -lt 128) {
                $idx = [int][Math]::Floor($x / 8)
                $row[$idx] = $row[$idx] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row)
    }

    $bw.Flush()
    return ($pixels + $maskSize + 40)
}

$sizes = @(16, 24, 32, 48, 64, 128)
$bitmaps = @()
foreach ($s in $sizes) { $bitmaps += , (New-IconBitmap $s) }

# Build each entry in memory first so the directory can hold real byte counts.
$payloads = @()
foreach ($b in $bitmaps) {
    $ms = [System.IO.MemoryStream]::new()
    $null = Add-IcoEntry $ms $b
    $payloads += , $ms.ToArray()
    $ms.Dispose()
}

$icoPath = Join-Path $outDir 'radiocity.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = [System.IO.BinaryWriter]::new($fs)

$w.Write([uint16]0)                  # reserved
$w.Write([uint16]1)                  # type: icon
$w.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = [byte]0
    if ($s -lt 256) { $dim = [byte]$s }

    $w.Write($dim)                   # width (0 means 256)
    $w.Write($dim)                   # height (0 means 256)
    $w.Write([byte]0)                # palette size
    $w.Write([byte]0)                # reserved
    $w.Write([uint16]1)              # colour planes
    $w.Write([uint16]32)             # bits per pixel
    $w.Write([uint32]$payloads[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $payloads[$i].Length
}

foreach ($p in $payloads) { $w.Write($p) }
$w.Flush(); $w.Dispose(); $fs.Dispose()

foreach ($b in $bitmaps) { $b.Dispose() }

# A PNG copy for docs and for anything that wants a raster preview.
$png = New-IconBitmap 256
$pngPath = Join-Path $outDir 'radiocity.png'
$png.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

$icoInfo = Get-Item $icoPath
Write-Host ("wrote {0} ({1} bytes, {2} sizes: {3})" -f $icoInfo.FullName, $icoInfo.Length, $sizes.Count, ($sizes -join ', '))
Write-Host ("wrote {0}" -f $pngPath)
