<#
.SYNOPSIS
    Draws the Apple Drive icon and writes every size the app and its MSIX package need.

.DESCRIPTION
    Output: src/AppleDrive.App/Assets/*.png (MSIX visual assets) and src/AppleDrive.App/Assets/AppleDrive.ico
    (the .exe icon). The icon is a white photo frame with a download arrow on a blue gradient.
    Run again after changing the drawing:

        powershell -ExecutionPolicy Bypass -File tools/Generate-Icons.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\AppleDrive.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundedRectangle([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Draws the mark into a square of side $s at ($ox, $oy). $plate draws the blue rounded background.
function Draw-Mark([System.Drawing.Graphics]$g, [single]$ox, [single]$oy, [single]$s, [bool]$plate) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    if ($plate) {
        $background = New-RoundedRectangle $ox $oy $s $s ($s * 0.22)
        $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF($ox, $oy)),
            (New-Object System.Drawing.PointF(($ox + $s), ($oy + $s))),
            [System.Drawing.Color]::FromArgb(255, 0, 120, 212),
            [System.Drawing.Color]::FromArgb(255, 0, 178, 202))
        $g.FillPath($gradient, $background)
    }

    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen($white, [single]($s * 0.055))
    $pen.LineJoin = 'Round'
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'

    # Photo frame with a mountain and a sun.
    $frame = New-RoundedRectangle ($ox + $s * 0.20) ($oy + $s * 0.20) ($s * 0.60) ($s * 0.44) ($s * 0.06)
    $g.DrawPath($pen, $frame)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($white)), $ox + $s * 0.58, $oy + $s * 0.29, $s * 0.09, $s * 0.09)
    $mountain = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($ox + $s * 0.27), ($oy + $s * 0.57))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.41), ($oy + $s * 0.40))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.51), ($oy + $s * 0.50))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.57), ($oy + $s * 0.45))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.73), ($oy + $s * 0.57))))
    $g.DrawLines($pen, $mountain)

    # Download arrow into a tray below the frame.
    $g.DrawLine($pen, $ox + $s * 0.50, $oy + $s * 0.68, $ox + $s * 0.50, $oy + $s * 0.84)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($ox + $s * 0.43), ($oy + $s * 0.77))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.50), ($oy + $s * 0.84))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.57), ($oy + $s * 0.77)))))
}

function Save-Png([int]$width, [int]$height, [string]$name, [single]$markScale = 1.0, [bool]$plate = $true) {
    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [Math]::Min($width, $height) * $markScale
    Draw-Mark $g (($width - $side) / 2) (($height - $side) / 2) $side $plate
    $g.Dispose()
    $path = Join-Path $assets $name
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return $path
}

# MSIX visual assets (scale-200 is what the manifest names without a qualifier resolve to on most displays).
Save-Png 88 88 'Square44x44Logo.scale-200.png' | Out-Null
Save-Png 300 300 'Square150x150Logo.scale-200.png' 0.8 | Out-Null
Save-Png 620 300 'Wide310x150Logo.scale-200.png' 0.8 | Out-Null
Save-Png 100 100 'StoreLogo.scale-200.png' | Out-Null
Save-Png 1240 600 'SplashScreen.scale-200.png' 0.7 | Out-Null
foreach ($size in 16, 24, 32, 48, 256) {
    Save-Png $size $size "Square44x44Logo.targetsize-$($size).png" | Out-Null
    Save-Png $size $size "Square44x44Logo.targetsize-$($size)_altform-unplated.png" | Out-Null
}

# .ico with PNG-compressed entries (supported since Windows Vista).
$icoSizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $icoSizes) { , [System.IO.File]::ReadAllBytes((Save-Png $size $size "ico-$size.png")) }
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $size = $icoSizes[$i]
    $writer.Write([byte]($size % 256)); $writer.Write([byte]($size % 256))
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppleDrive.ico'), $stream.ToArray())
foreach ($size in $icoSizes) { Remove-Item (Join-Path $assets "ico-$size.png") }
"Wrote icons to $assets"
