# Generates src/FlashTrans/Assets/app.ico (multi-size, PNG-compressed entries).
# Run manually only when the icon needs to change:
#   powershell -NoProfile -File tools/make-icon.ps1
# ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src\FlashTrans\Assets'
$out    = Join-Path $outDir 'app.ico'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$sizes  = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$accent = [System.Drawing.Color]::FromArgb(255, 0x4C, 0x8D, 0xFF)
$deep   = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x53, 0xB3)
$glyph  = [string][char]0x8BD1        # U+8BD1 = the Chinese character for "translate"

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded plate with a diagonal gradient
    $radius = [Math]::Max(2, [int]($size * 0.22))
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect  = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $accent, $deep, 55.0)
    $g.FillPath($brush, $path)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    if ($size -ge 32) {
        # big sizes: draw the glyph
        $fontSize = [float]($size * 0.62)
        $font = $null
        foreach ($family in @('Microsoft YaHei', 'SimHei', 'SimSun')) {
            try {
                $font = New-Object System.Drawing.Font($family, $fontSize,
                    [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
                break
            } catch { }
        }
        if ($null -eq $font) {
            $font = New-Object System.Drawing.Font([System.Drawing.FontFamily]::GenericSansSerif,
                $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        }
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment     = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $box = New-Object System.Drawing.RectangleF(0, [float]($size * 0.02), $size, $size)
        $g.DrawString($glyph, $font, $white, $box, $fmt)
        $fmt.Dispose(); $font.Dispose()
    }
    else {
        # small sizes: a bolt reads better than a dense glyph
        $pts = @(
            (New-Object System.Drawing.PointF([float]($size * 0.58), [float]($size * 0.13))),
            (New-Object System.Drawing.PointF([float]($size * 0.27), [float]($size * 0.56))),
            (New-Object System.Drawing.PointF([float]($size * 0.46), [float]($size * 0.56))),
            (New-Object System.Drawing.PointF([float]($size * 0.40), [float]($size * 0.87))),
            (New-Object System.Drawing.PointF([float]($size * 0.72), [float]($size * 0.44))),
            (New-Object System.Drawing.PointF([float]($size * 0.52), [float]($size * 0.44)))
        )
        $g.FillPolygon($white, [System.Drawing.PointF[]]$pts)
    }

    $white.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$entries = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# ICO container: 6-byte header + 16 bytes per directory entry + payloads
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$entries.Count)

    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim)          # width  (0 means 256)
        $bw.Write([byte]$dim)          # height
        $bw.Write([byte]0)             # palette entries
        $bw.Write([byte]0)             # reserved
        $bw.Write([uint16]1)           # color planes
        $bw.Write([uint16]32)          # bits per pixel
        $bw.Write([uint32]$e.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Bytes) }
}
finally { $bw.Dispose(); $fs.Dispose() }

$kb = [Math]::Round((Get-Item $out).Length / 1024, 1)
Write-Host ("Wrote {0} ({1} sizes, {2} KB)" -f $out, $entries.Count, $kb)
