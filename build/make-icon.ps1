<#
.SYNOPSIS
    Generates ClaudeSoundtrack.ico from code.

.DESCRIPTION
    The icon is a brass compact disc on a dark plate, matching the emblem in the
    app's own header. Generated rather than hand-drawn so it can be adjusted and
    regenerated without a paint program, and so every size is rendered at its own
    scale instead of being downsampled from one large image - a 256px disc scaled
    to 16px turns into brown mush.

    Detail is therefore dropped as the size falls: the spindle ring and the
    specular highlight only appear where there are enough pixels to carry them.

    Sizes follow what the Windows shell actually asks for: 16 and 20 in lists and
    the taskbar, 24/32/40/48 for various DPI scalings, 64/128/256 for large icons
    and the Alt-Tab switcher.

.EXAMPLE
    .\build\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Output
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Output) {
    $Output = Join-Path $repoRoot 'src\ClaudeSoundtrack.App\ClaudeSoundtrack.ico'
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

# Palette lifted from the app theme so the icon and the window agree.
$brassPale = [System.Drawing.Color]::FromArgb(255, 0xE8, 0xCE, 0x86)
$brass     = [System.Drawing.Color]::FromArgb(255, 0xA8, 0x82, 0x2C)
$brassDark = [System.Drawing.Color]::FromArgb(255, 0x5A, 0x45, 0x18)
$steel     = [System.Drawing.Color]::FromArgb(255, 0x24, 0x1D, 0x18)
$steelDark = [System.Drawing.Color]::FromArgb(255, 0x0F, 0x0C, 0x0A)
$amber     = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xA6, 0x2B)

function New-DiscBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Inset so the antialiased rim is not clipped by the bitmap edge.
    $pad = [Math]::Max(1.0, $size * 0.045)
    $d   = $size - (2 * $pad)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, $d, $d)
    $cx = $size / 2.0
    $cy = $size / 2.0

    # --- disc face: dark, lit from the upper left ---
    $facePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $facePath.AddEllipse($rect)
    $face = New-Object System.Drawing.Drawing2D.PathGradientBrush($facePath)
    $face.CenterPoint  = New-Object System.Drawing.PointF(($rect.X + $d * 0.34), ($rect.Y + $d * 0.30))
    $face.CenterColor  = $steel
    $face.SurroundColors = @($steelDark)
    $g.FillEllipse($face, $rect)
    $face.Dispose()
    $facePath.Dispose()

    # --- brass rim ---
    $rimWidth = [Math]::Max(1.0, $size * 0.085)
    $rimRect = New-Object System.Drawing.RectangleF(
        ($rect.X + $rimWidth / 2), ($rect.Y + $rimWidth / 2),
        ($d - $rimWidth), ($d - $rimWidth))
    $rimBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, $brassPale, $brassDark, 55.0)
    $rimPen = New-Object System.Drawing.Pen($rimBrush, $rimWidth)
    $g.DrawEllipse($rimPen, $rimRect)
    $rimPen.Dispose()
    $rimBrush.Dispose()

    # --- data-track hint: a single amber arc catching the light ---
    # Only where there is room; below 32px it reads as a dirty smudge.
    if ($size -ge 32) {
        $arcInset = $d * 0.20
        $arcRect = New-Object System.Drawing.RectangleF(
            ($rect.X + $arcInset), ($rect.Y + $arcInset),
            ($d - 2 * $arcInset), ($d - 2 * $arcInset))
        $arcPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(150, $amber.R, $amber.G, $amber.B),
            [Math]::Max(1.0, $size * 0.035))
        $g.DrawArc($arcPen, $arcRect, 200.0, 110.0)
        $arcPen.Dispose()
    }

    # --- spindle hole ---
    $holeD = $d * 0.26
    $holeRect = New-Object System.Drawing.RectangleF(
        ($cx - $holeD / 2), ($cy - $holeD / 2), $holeD, $holeD)
    $holeBrush = New-Object System.Drawing.SolidBrush($steelDark)
    $g.FillEllipse($holeBrush, $holeRect)
    $holeBrush.Dispose()

    # Brass ring around the hole, only where it would be more than a pixel.
    if ($size -ge 24) {
        $ringPen = New-Object System.Drawing.Pen($brass, [Math]::Max(1.0, $size * 0.028))
        $g.DrawEllipse($ringPen, $holeRect)
        $ringPen.Dispose()
    }

    # --- specular highlight across the upper left ---
    if ($size -ge 48) {
        $specPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
            [Math]::Max(1.0, $size * 0.03))
        $specRect = New-Object System.Drawing.RectangleF(
            ($rect.X + $d * 0.12), ($rect.Y + $d * 0.12), ($d * 0.76), ($d * 0.76))
        $g.DrawArc($specPen, $specRect, 195.0, 60.0)
        $specPen.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# --- render every size to an in-memory PNG ---
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-DiscBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

# --- pack into an .ico ---
# Every entry is stored PNG-compressed, which Windows Vista and later accept at
# all sizes and which keeps the file small.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([UInt16]0)               # reserved
$w.Write([UInt16]1)               # type 1 = icon
$w.Write([UInt16]$pngs.Count)

# Directory entries come first, so image data starts after all of them.
$offset = 6 + (16 * $pngs.Count)
foreach ($p in $pngs) {
    # 256 is written as 0 in the single byte width/height fields.
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $w.Write([Byte]$dim)          # width
    $w.Write([Byte]$dim)          # height
    $w.Write([Byte]0)             # palette count
    $w.Write([Byte]0)             # reserved
    $w.Write([UInt16]1)           # colour planes
    $w.Write([UInt16]32)          # bits per pixel
    $w.Write([UInt32]$p.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $w.Write($p.Bytes) }

$w.Flush()
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
$w.Dispose()
$out.Dispose()

$kb = [Math]::Round((Get-Item $Output).Length / 1KB, 1)
Write-Host "Wrote $Output" -ForegroundColor Green
Write-Host "  $($pngs.Count) sizes ($($sizes -join ', ')), $kb KB"
