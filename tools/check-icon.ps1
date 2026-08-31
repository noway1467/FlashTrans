Add-Type -AssemblyName System.Drawing
$path = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\FlashTrans\Assets\app.ico'
$d = [IO.File]::ReadAllBytes($path)
$n = [BitConverter]::ToUInt16($d, 4)
for ($i = 0; $i -lt $n; $i++) {
    $o = 6 + $i * 16
    $w = $d[$o]; if ($w -eq 0) { $w = 256 }
    $len = [BitConverter]::ToUInt32($d, $o + 8)
    $off = [BitConverter]::ToUInt32($d, $o + 12)
    $bytes = New-Object byte[] $len
    [Array]::Copy($d, $off, $bytes, 0, $len)
    $ms = New-Object IO.MemoryStream($bytes, $false)
    $bmp = [Drawing.Image]::FromStream($ms)
    $opaque = 0; $total = 0; $maxA = 0
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            $a = $bmp.GetPixel($x, $y).A
            $total++
            if ($a -gt 16) { $opaque++ }
            if ($a -gt $maxA) { $maxA = $a }
        }
    }
    $pct = [Math]::Round(100.0 * $opaque / $total, 1)
    '{0,3}x{1,-3} decoded={2}x{3} opaque={4}% maxAlpha={5}' -f $w, $w, $bmp.Width, $bmp.Height, $pct, $maxA
    $bmp.Dispose(); $ms.Dispose()
}
