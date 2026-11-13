<#
.SYNOPSIS
  Renders the Flux Browser icon (glass globe on a blue-violet tile) and packs it into a multi-size .ico plus a 512 px .png.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\MyOwnBrowser\Assets')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Render-Icon([int]$size) {
    # Draw at 4x and downsample: smooth edges at every size without relying on GDI+ antialiasing alone.
    $scale = 4
    $s = $size * $scale
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = 0.035 * $s
    $tile = $s - 2 * $pad
    $tilePath = New-RoundedRect $pad $pad $tile $tile (0.225 * $s)

    # Tile: diagonal blue -> violet gradient.
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $tile, $tile
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([System.Drawing.Color]::FromArgb(255, 124, 58, 237)), 52
    $g.FillPath($bg, $tilePath)

    $g.SetClip($tilePath)

    # Cyan glow in the lower-left corner for depth.
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(-0.25 * $s, 0.45 * $s, 0.95 * $s, 0.95 * $s)
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
    $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(150, 34, 211, 238)
    $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 34, 211, 238))
    $g.FillPath($glowBrush, $glow)

    # Frosted globe: translucent disc, bright ring, meridians and parallels.
    $cx = $s / 2; $cy = $s / 2 + 0.005 * $s
    $r = 0.285 * $s
    $disc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disc.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $discBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)), ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(28, 255, 255, 255)), 90
    $g.FillPath($discBrush, $disc)

    $detail = $size -ge 40
    $ringWidth = [Math]::Max(1.6 * $scale, 0.05 * $s)
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(245, 255, 255, 255)), $ringWidth
    $thin = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(215, 255, 255, 255)), ([Math]::Max(1.2 * $scale, 0.03 * $s))
    $thin.StartCap = 'Round'; $thin.EndCap = 'Round'

    $g.DrawEllipse($thin, $cx - 0.125 * $s, $cy - $r, 0.25 * $s, 2 * $r)      # central meridian
    $g.DrawLine($thin, $cx - $r, $cy, $cx + $r, $cy)                            # equator
    if ($detail) {
        $g.DrawArc($thin, $cx - $r * 0.88, $cy - $r * 0.62 - 0.03 * $s, 2 * $r * 0.88, 0.1 * $s, 15, 150)   # parallels
        $g.DrawArc($thin, $cx - $r * 0.88, $cy + $r * 0.62 - 0.07 * $s, 2 * $r * 0.88, 0.1 * $s, 195, 150)
    }
    $g.DrawEllipse($ring, $cx - $r, $cy - $r, 2 * $r, 2 * $r)

    # Glass sheen across the top of the tile.
    $sheen = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sheen.AddEllipse(-0.2 * $s, -0.62 * $s, 1.4 * $s, 1.15 * $s)
    $sheenBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, 0, $s, (0.55 * $s)), ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90
    $g.FillPath($sheenBrush, $sheen)
    $g.ResetClip()

    # Hairline rim so the tile reads on light and dark backgrounds.
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([Math]::Max(1, 0.012 * $s))
    $g.DrawPath($rim, $tilePath)
    $g.Dispose()

    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $og = [System.Drawing.Graphics]::FromImage($out)
    $og.InterpolationMode = 'HighQualityBicubic'
    $og.PixelOffsetMode = 'HighQuality'
    $og.CompositingMode = 'SourceCopy'
    $og.DrawImage($bmp, 0, 0, $size, $size)
    $og.Dispose(); $bmp.Dispose()
    return $out
}

function Get-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $ms = New-Object System.IO.MemoryStream
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
    $bitmap = Render-Icon $size
    [pscustomobject]@{ Size = $size; Data = (Get-PngBytes $bitmap) }
    $bitmap.Dispose()
}

$icoPath = Join-Path $OutputDirectory 'AppIcon.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$offset)
    $offset += $frame.Data.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
$writer.Dispose(); $stream.Dispose()

$large = Render-Icon 512
$large.Save((Join-Path $OutputDirectory 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$large.Dispose()
"Icono generado: $icoPath ($($frames.Count) tamaños) y AppIcon.png"
