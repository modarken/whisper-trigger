Add-Type -AssemblyName System.Drawing

function New-WaveformBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $g.Clear([System.Drawing.Color]::Transparent)

    # Dark slate circle background
    $bg = [System.Drawing.Color]::FromArgb(255, 30, 41, 59)
    $bgBrush = New-Object System.Drawing.SolidBrush($bg)
    $g.FillEllipse($bgBrush, 0, 0, $size - 1, $size - 1)
    $bgBrush.Dispose()

    # Six waveform bars (heights as fraction of circle diameter)
    $barHeights = @(0.28, 0.50, 0.72, 0.82, 0.58, 0.32)
    $numBars    = $barHeights.Count
    $scale      = $size / 16.0
    $barW       = [Math]::Max(1.0, 1.7 * $scale)
    $gap        = [Math]::Max(0.5, 0.9 * $scale)
    $totalW     = $numBars * $barW + ($numBars - 1) * $gap
    $startX     = ($size - $totalW) / 2.0
    $centerY    = $size / 2.0
    $maxH       = $size * 0.72

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    for ($i = 0; $i -lt $numBars; $i++) {
        $h  = $barHeights[$i] * $maxH
        $x  = $startX + $i * ($barW + $gap)
        $y  = $centerY - $h / 2.0
        $r  = [Math]::Min($barW / 2.0, $h / 2.0)

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        if ($r -lt 0.8) {
            $path.AddRectangle([System.Drawing.RectangleF]::new($x, $y, $barW, $h))
        } else {
            $path.AddArc($x, $y, $barW, $r * 2, 180, 180)
            $path.AddLine($x + $barW, $y + $r, $x + $barW, $y + $h - $r)
            $path.AddArc($x, $y + $h - $r * 2, $barW, $r * 2, 0, 180)
            $path.CloseFigure()
        }
        $g.FillPath($white, $path)
        $path.Dispose()
    }

    $white.Dispose()
    $g.Dispose()
    return $bmp
}

function ConvertTo-IcoBytes([System.Drawing.Bitmap[]]$bitmaps) {
    $pngs = @()
    foreach ($bmp in $bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $ms.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $bw  = New-Object System.IO.BinaryWriter($out)

    # ICO header
    $bw.Write([uint16]0)                  # reserved
    $bw.Write([uint16]1)                  # type = ICO
    $bw.Write([uint16]$bitmaps.Count)

    # Directory — offsets start after header (6) + directory entries (16 * N)
    $offset = [uint32](6 + 16 * $bitmaps.Count)
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $w = if ($bitmaps[$i].Width -ge 256) { [byte]0 } else { [byte]$bitmaps[$i].Width }
        $h = if ($bitmaps[$i].Height -ge 256) { [byte]0 } else { [byte]$bitmaps[$i].Height }
        $bw.Write($w)
        $bw.Write($h)
        $bw.Write([byte]0)     # colorCount
        $bw.Write([byte]0)     # reserved
        $bw.Write([uint16]1)   # color planes
        $bw.Write([uint16]32)  # bits per pixel
        $bw.Write([uint32]$pngs[$i].Length)
        $bw.Write($offset)
        $offset += [uint32]$pngs[$i].Length
    }

    foreach ($png in $pngs) { $bw.Write($png) }

    $bw.Flush()
    $bytes = $out.ToArray()
    $bw.Dispose()
    $out.Dispose()
    return $bytes
}

$root    = Split-Path $PSScriptRoot -Parent
$outPath = Join-Path $root "src\WhisperTrigger\icon.ico"

$bitmaps = @(
    (New-WaveformBitmap 16),
    (New-WaveformBitmap 32),
    (New-WaveformBitmap 48)
)

[System.IO.File]::WriteAllBytes($outPath, (ConvertTo-IcoBytes $bitmaps))
foreach ($b in $bitmaps) { $b.Dispose() }

Write-Host "Icon written to $outPath"
