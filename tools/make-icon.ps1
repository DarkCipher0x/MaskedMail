# Draws the DarkCipher MaskedMail logo (envelope + masquerade mask in the darkcipher0x teal ring) with WPF and writes
# src\DcxMaskedMail\Assets\logo.png (512 px) and app.ico (16-256 px, PNG frames). ASCII only (PowerShell 5.1).
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\DcxMaskedMail\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework

$teal = [System.Windows.Media.Color]::FromRgb(0x3B, 0xD3, 0xCC)
$tealDim = [System.Windows.Media.Color]::FromRgb(0x1E, 0x8C, 0x86)
$ink = [System.Windows.Media.Color]::FromRgb(0x0B, 0x13, 0x14)

function Brush($c, [double]$a = 1) { $b = New-Object System.Windows.Media.SolidColorBrush $c; $b.Opacity = $a; $b.Freeze(); $b }
function Pen($c, [double]$w, [double]$a = 1) {
    $p = New-Object System.Windows.Media.Pen (Brush $c $a), $w
    $p.LineJoin = 'Round'; $p.StartLineCap = 'Round'; $p.EndLineCap = 'Round'; $p.Freeze(); $p
}
function Geo([string]$d) { [System.Windows.Media.Geometry]::Parse($d) }

function Draw([int]$px) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($px / 512.0), ($px / 512.0)))
    $small = $px -le 48
    $center = New-Object System.Windows.Point 256, 256

    # Ring with a soft glow (the glow is dropped at small sizes, where it only blurs the edge)
    if (-not $small) {
        foreach ($g in @(@(34, 0.06), @(26, 0.10), @(20, 0.16))) { $dc.DrawEllipse($null, (Pen $teal $g[0] $g[1]), $center, 228, 228) }
    }
    $dc.DrawEllipse((Brush $ink 0.0), (Pen $teal ($(if ($small) { 26 } else { 14 }))), $center, 228, 228)

    # Envelope: dark body, teal outline, flap
    $env = New-Object System.Windows.Rect 112, 146, 288, 214
    $dc.DrawRoundedRectangle((Brush $ink), (Pen $teal ($(if ($small) { 22 } else { 14 }))), $env, 24, 24)
    $flapW = $(if ($small) { 20 } else { 13 })
    $dc.DrawGeometry($null, (Pen $teal $flapW), (Geo 'M 128,166 L 256,262 L 384,166'))
    if (-not $small) { $dc.DrawGeometry($null, (Pen $tealDim 10), (Geo 'M 128,344 L 206,282 M 384,344 L 306,282')) }

    # Masquerade mask over the flap: teal, with an ink outline to lift it off the envelope, dark eye holes
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform 0.86, 0.86, 256, 270))
    $mask = Geo 'M 128,252 C 140,222 196,214 236,234 Q 256,244 276,234 C 316,214 372,222 384,252 C 396,284 376,318 336,322 C 306,326 284,310 268,296 Q 256,286 244,296 C 228,310 206,326 176,322 C 136,318 116,284 128,252 Z'
    $dc.DrawGeometry($null, (Pen $ink 18), $mask)
    $dc.DrawGeometry((Brush $teal), $null, $mask)
    $eyes = Geo 'M 162,270 C 176,248 216,248 230,268 C 216,288 176,290 162,270 Z M 282,268 C 296,248 336,248 350,270 C 336,290 296,288 282,268 Z'
    $dc.DrawGeometry((Brush $ink), $null, $eyes)
    if (-not $small) {
        # ribbon ties on both sides
        $dc.DrawGeometry($null, (Pen $teal 10), (Geo 'M 126,258 Q 112,252 104,242 M 126,264 Q 114,272 108,284 M 386,258 Q 400,252 408,242 M 386,264 Q 398,272 404,284'))
    }
    $dc.Pop()
    $dc.Pop()
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $px, $px, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'logo.png'), (Draw 512))

# ICO with PNG frames (supported since Windows Vista)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { , (Draw $s) }
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $ms.ToArray())
foreach ($s in 32, 64) { [System.IO.File]::WriteAllBytes((Join-Path $env:TEMP "dcxmail-icon-$s.png"), (Draw $s)) }
Write-Host "logo.png and app.ico written to $OutDir"
