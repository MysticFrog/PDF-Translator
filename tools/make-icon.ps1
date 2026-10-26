# Builds Assets\PdfTranslator.ico (16-256 px, PNG-compressed frames) from PDF_TRANSLATE.png.
# The supplied PDF_TRANSLATE.ico has an empty 256x256 frame, so the application uses this one.
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\PDF_TRANSLATE.png'),
    [string]$Target = (Join-Path $PSScriptRoot '..\Assets\PdfTranslator.ico')
)
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
$frames = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $ms.ToArray() }
}
$src.Dispose()

New-Item -ItemType Directory -Force (Split-Path $Target) | Out-Null
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path (Resolve-Path (Split-Path $Target)) (Split-Path $Target -Leaf)), $out.ToArray())
Write-Host "Wrote $Target ($($frames.Count) sizes, $([int]($out.Length / 1024)) KB)"
