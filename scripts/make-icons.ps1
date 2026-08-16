<#
.SYNOPSIS
    Regenerates every app icon from the one source artwork.

.DESCRIPTION
    Icons are generated rather than hand-exported so they cannot drift apart. There
    are eight of them across three platforms, and the failure mode for hand-made sets
    is that someone updates four and nobody notices the rest for a year.

    Two things this does that a plain resize does not:

    * Thresholds the source first. The artwork is a compressed raster whose flat
      areas are not actually flat -- black is a spread of near-blacks and the green
      carries ringing around every edge. Resampling that directly muddies the small
      sizes, where the icon spends most of its life. Reducing to exactly two colours
      first and then resampling keeps the edges clean.

    * Drops the prompt line below about 48 pixels. "C:\1_" is legible at 256 and is
      grey mush at 16, and mush next to a taskbar full of crisp icons reads as a
      broken image rather than a small one. Below the cut the mark is just the "1".

.PARAMETER SourcePath
    The artwork. Square, green on black.
#>

[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot "..\assets\logo.png")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$pwaPublic = Join-Path $repo "src\PWA\public"
$assets = Join-Path $repo "assets"

if (-not (Test-Path $SourcePath)) {
    throw "Source artwork not found: $SourcePath"
}

Write-Host "==> Reading $([IO.Path]::GetFileName($SourcePath))"

$artwork = [System.Drawing.Bitmap]::FromFile([string](Resolve-Path $SourcePath))

# The exact green of the artwork, sampled rather than guessed, so the generated
# icons and the source stay the same colour if the artwork is ever re-exported.
$ink = [System.Drawing.Color]::FromArgb(0x05, 0xFC, 0x04)
$paper = [System.Drawing.Color]::FromArgb(0x00, 0x00, 0x00)

function Test-Lit {
    param([System.Drawing.Color]$Colour)

    # Green channel both bright and dominant. A plain brightness test would also
    # catch the ringing around the strokes, which is what we are removing.
    return $Colour.G -gt 100 -and $Colour.G -gt ($Colour.R + 40) -and $Colour.G -gt ($Colour.B + 40)
}

Write-Host "==> Thresholding"

$w = $artwork.Width
$h = $artwork.Height
$lit = New-Object 'bool[,]' $w, $h

$rowHasInk = New-Object 'bool[]' $h
$minX = $w; $maxX = -1; $minY = $h; $maxY = -1

for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
        if (Test-Lit $artwork.GetPixel($x, $y)) {
            $lit[$x, $y] = $true
            $rowHasInk[$y] = $true

            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}

if ($maxX -lt 0) {
    throw "No artwork found in $SourcePath after thresholding -- is it green on black?"
}

$artwork.Dispose()

# Find the gap between the numeral and the prompt line beneath it: the widest run of
# empty rows inside the artwork. Everything above it is the mark used at small sizes.
$bestGapStart = -1
$bestGapLength = 0
$gapStart = -1

for ($y = $minY; $y -le $maxY; $y++) {
    if (-not $rowHasInk[$y]) {
        if ($gapStart -lt 0) { $gapStart = $y }
    }
    elseif ($gapStart -ge 0) {
        $length = $y - $gapStart
        if ($length -gt $bestGapLength) { $bestGapLength = $length; $bestGapStart = $gapStart }
        $gapStart = -1
    }
}

$markMaxY = if ($bestGapStart -gt 0) { $bestGapStart - 1 } else { $maxY }

# The numeral's own horizontal extent, which is narrower than the full artwork.
$markMinX = $w; $markMaxX = -1
for ($y = $minY; $y -le $markMaxY; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
        if ($lit[$x, $y]) {
            if ($x -lt $markMinX) { $markMinX = $x }
            if ($x -gt $markMaxX) { $markMaxX = $x }
        }
    }
}

Write-Host ("    full  x {0}..{1}  y {2}..{3}" -f $minX, $maxX, $minY, $maxY)
Write-Host ("    mark  x {0}..{1}  y {2}..{3}" -f $markMinX, $markMaxX, $minY, $markMaxY)

function New-Icon {
    <#
        Draws the chosen region centred on a square of the given size, with `Margin`
        as the fraction of the square left empty on the tightest axis.
    #>
    param(
        [int]$Size,
        [double]$Margin,
        [switch]$MarkOnly
    )

    $left = if ($MarkOnly) { $markMinX } else { $minX }
    $right = if ($MarkOnly) { $markMaxX } else { $maxX }
    $bottom = if ($MarkOnly) { $markMaxY } else { $maxY }

    $srcW = $right - $left + 1
    $srcH = $bottom - $minY + 1

    # Render the region at a whole multiple of the target and let the resize do the
    # averaging: sharp two-colour edges downsampled in one step, rather than a chain
    # of resamplings each softening the last.
    $scale = [Math]::Max(1, [int][Math]::Floor(2048 / [Math]::Max($srcW, $srcH)))

    $stage = New-Object System.Drawing.Bitmap ($srcW * $scale), ($srcH * $scale)
    $g = [System.Drawing.Graphics]::FromImage($stage)
    $g.Clear($paper)
    $brush = New-Object System.Drawing.SolidBrush $ink

    for ($y = $minY; $y -le $bottom; $y++) {
        # Run-length along each row: one FillRectangle per run instead of per pixel.
        $runStart = -1
        for ($x = $left; $x -le $right + 1; $x++) {
            $on = ($x -le $right) -and $lit[$x, $y]

            if ($on -and $runStart -lt 0) {
                $runStart = $x
            }
            elseif (-not $on -and $runStart -ge 0) {
                $g.FillRectangle(
                    $brush,
                    ($runStart - $left) * $scale,
                    ($y - $minY) * $scale,
                    ($x - $runStart) * $scale,
                    $scale)
                $runStart = -1
            }
        }
    }

    $brush.Dispose()
    $g.Dispose()

    $canvas = New-Object System.Drawing.Bitmap $Size, $Size
    $cg = [System.Drawing.Graphics]::FromImage($canvas)
    $cg.Clear($paper)
    $cg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $cg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $available = $Size * (1.0 - (2.0 * $Margin))
    $ratio = [Math]::Min($available / $stage.Width, $available / $stage.Height)
    $drawW = [int][Math]::Round($stage.Width * $ratio)
    $drawH = [int][Math]::Round($stage.Height * $ratio)

    $cg.DrawImage(
        $stage,
        [int][Math]::Round(($Size - $drawW) / 2.0),
        [int][Math]::Round(($Size - $drawH) / 2.0),
        $drawW,
        $drawH)

    $cg.Dispose()
    $stage.Dispose()

    return $canvas
}

function Save-Png {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)

    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host ("    {0,-28} {1}x{1}" -f [IO.Path]::GetFileName($Path), $Bitmap.Width)
}

New-Item -ItemType Directory -Force -Path $pwaPublic, $assets | Out-Null

Write-Host "==> Phone and browser icons"

# Margins differ by purpose rather than taste. A favicon is 16 physical pixels and
# wants every one of them; a maskable icon is cropped to whatever shape the launcher
# feels like, so its content has to sit inside the inner 80% or the launcher will
# shave the ends off the numeral.
$targets = @(
    @{ Name = "icon-192.png"; Size = 192; Margin = 0.06; MarkOnly = $false }
    @{ Name = "icon-512.png"; Size = 512; Margin = 0.06; MarkOnly = $false }
    @{ Name = "icon-maskable-512.png"; Size = 512; Margin = 0.20; MarkOnly = $false }
    @{ Name = "apple-touch-icon.png"; Size = 180; Margin = 0.08; MarkOnly = $false }
    @{ Name = "favicon-32.png"; Size = 32; Margin = 0.04; MarkOnly = $true }
    @{ Name = "favicon-48.png"; Size = 48; Margin = 0.04; MarkOnly = $true }
)

foreach ($target in $targets) {
    $bitmap = New-Icon -Size $target.Size -Margin $target.Margin -MarkOnly:$target.MarkOnly
    Save-Png -Bitmap $bitmap -Path (Join-Path $pwaPublic $target.Name)
    $bitmap.Dispose()
}

Write-Host "==> Windows application icon"

# 16 and 32 are the taskbar and Alt-Tab; 256 is what Explorer shows at large sizes.
# The prompt line only survives from 64 up.
$icoFrames = @(
    @{ Size = 16; Margin = 0.02; MarkOnly = $true }
    @{ Size = 20; Margin = 0.02; MarkOnly = $true }
    @{ Size = 24; Margin = 0.02; MarkOnly = $true }
    @{ Size = 32; Margin = 0.03; MarkOnly = $true }
    @{ Size = 40; Margin = 0.03; MarkOnly = $true }
    @{ Size = 48; Margin = 0.03; MarkOnly = $true }
    @{ Size = 64; Margin = 0.06; MarkOnly = $false }
    @{ Size = 128; Margin = 0.06; MarkOnly = $false }
    @{ Size = 256; Margin = 0.06; MarkOnly = $false }
)

$frames = @()
foreach ($frame in $icoFrames) {
    $bitmap = New-Icon -Size $frame.Size -Margin $frame.Margin -MarkOnly:$frame.MarkOnly

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $frame.Size; Bytes = $stream.ToArray() }

    $stream.Dispose()
    $bitmap.Dispose()
}

$icoPath = Join-Path $assets "1remote.ico"
$out = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter $out

# ICONDIR. Every frame is stored as PNG, which .ico has allowed since Vista and which
# keeps a 256px frame to a few KB instead of 256.
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $writer.Write([byte]($(if ($frame.Size -ge 256) { 0 } else { $frame.Size })))
    $writer.Write([byte]($(if ($frame.Size -ge 256) { 0 } else { $frame.Size })))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frame.Bytes.Length)
    $writer.Write([UInt32]$offset)

    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write($frame.Bytes)
}

$writer.Dispose()
$out.Dispose()

Write-Host ("    {0,-28} {1} frames, {2:N1} KB" -f "1remote.ico", $frames.Count, ((Get-Item $icoPath).Length / 1KB))

Write-Host ""
Write-Host "Done. Rebuild the agent and re-run scripts/publish-hub.ps1 to ship these."
