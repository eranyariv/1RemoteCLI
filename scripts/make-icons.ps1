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
        [switch]$MarkOnly,
        # Leaves the background clear instead of painting it black. The app tile wants
        # its black backdrop -- the artwork is a terminal, and a floating glyph would
        # not read as one. A tray icon must not have it: the shell composites it onto
        # whatever colour the taskbar is, and an opaque tile there is a black box.
        [switch]$Transparent
    )

    $background = if ($Transparent) { [System.Drawing.Color]::Transparent } else { $paper }

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
    $g.Clear($background)
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
    $cg.Clear($background)
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

function Save-Ico {
    <#
        Writes a multi-frame .ico. Every frame is stored as PNG, which .ico has allowed
        since Vista and which keeps a 256px frame to a few KB instead of 256.
    #>
    param([object[]]$Frames, [string]$Path)

    $out = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter $out

    # ICONDIR.
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$Frames.Count)

    $offset = 6 + (16 * $Frames.Count)
    foreach ($frame in $Frames) {
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

    foreach ($frame in $Frames) {
        $writer.Write($frame.Bytes)
    }

    $writer.Dispose()
    $out.Dispose()

    Write-Host ("    {0,-28} {1} frames, {2:N1} KB" -f [IO.Path]::GetFileName($Path), $Frames.Count, ((Get-Item $Path).Length / 1KB))
}

function New-Frames {
    param([object[]]$Spec, [switch]$Transparent)

    $frames = @()

    foreach ($frame in $Spec) {
        $bitmap = New-Icon -Size $frame.Size -Margin $frame.Margin -MarkOnly:$frame.MarkOnly -Transparent:$Transparent

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += , @{ Size = $frame.Size; Bytes = $stream.ToArray() }

        $stream.Dispose()
        $bitmap.Dispose()
    }

    return $frames
}

Save-Ico -Frames (New-Frames -Spec $icoFrames) -Path (Join-Path $assets "1remote.ico")

Write-Host "==> Tray icons"

# The tray family comes from its own artwork rather than from logo.png, because the
# tray has to say two things logo.png cannot: how many sessions are live, and whether
# the hub can be reached. assets/tray holds a drawn variant for each combination --
# the plain mark, one per count 1..9, and one ">9", in each connection state -- and
# each becomes its own .ico. Picking a whole prepared image is what lets both survive
# 16 pixels; anything composited at run time is mush at that size.
#
# Transparent, and only the sizes the shell asks for across display scalings, for the
# same reasons as before: the shell composites a tray icon straight onto the taskbar,
# so the black tile that makes the app icon read as a terminal would be a black box
# here, and the tray never goes above 48.
$trayStates = @("connected", "reconnecting", "disconnected")
$trayCounts = @("base", "1", "2", "3", "4", "5", "6", "7", "8", "9", "more")
$traySizes = @(16, 20, 24, 32, 40, 48)
$trayMargin = 0.02
$trayArt = Join-Path $assets "tray"
$trayOut = Join-Path $assets "tray-ico"
New-Item -ItemType Directory -Force -Path $trayOut | Out-Null

function Get-Bounds {
    <#
        The opaque extent of a bitmap, optionally limited to the rows above `MaxY`.
        Returns $null when nothing in range is opaque.
    #>
    param([System.Drawing.Bitmap]$Bitmap, [int]$MaxY = [int]::MaxValue)

    $x1 = $Bitmap.Width; $x2 = -1; $y1 = $Bitmap.Height; $y2 = -1
    $bottom = [Math]::Min($Bitmap.Height - 1, $MaxY)

    $data = $Bitmap.LockBits(
        (New-Object System.Drawing.Rectangle 0, 0, $Bitmap.Width, $Bitmap.Height),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $bytes = New-Object 'byte[]' ($data.Stride * $Bitmap.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $Bitmap.UnlockBits($data)

    for ($y = 0; $y -le $bottom; $y++) {
        $row = $y * $data.Stride
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            # BGRA little-endian: alpha is the fourth byte of each pixel.
            if ($bytes[$row + ($x * 4) + 3] -lt 128) { continue }

            if ($x -lt $x1) { $x1 = $x }
            if ($x -gt $x2) { $x2 = $x }
            if ($y -lt $y1) { $y1 = $y }
            if ($y -gt $y2) { $y2 = $y }
        }
    }

    if ($x2 -lt 0) { return $null }

    return New-Object System.Drawing.Rectangle $x1, $y1, ($x2 - $x1 + 1), ($y2 - $y1 + 1)
}

function Get-Union {
    param([System.Drawing.Rectangle[]]$Rectangles)

    $union = $Rectangles[0]
    foreach ($rectangle in $Rectangles) {
        $union = [System.Drawing.Rectangle]::Union($union, $rectangle)
    }

    return $union
}

$trayBitmaps = [ordered]@{}
foreach ($state in $trayStates) {
    foreach ($count in $trayCounts) {
        $path = Join-Path $trayArt "$state-$count.png"
        if (-not (Test-Path $path)) { throw "Tray artwork not found: $path" }
        $trayBitmaps["$state-$count"] = [System.Drawing.Bitmap]::FromFile([string](Resolve-Path $path))
    }
}

# The artwork is the full lockup: the numeral, a count plate beside it, and "CLI"
# underneath. "CLI" is grey mush below 48 pixels and the tray never gets that big, so
# the same cut make-icons applies to the app icon applies here -- everything above the
# widest band of empty rows in the plain variant. On the counted variants that band is
# where the plate sits, and on the reconnecting ones the state badge fills the corner,
# so the cut is measured once, on the plain connected mark, and reused everywhere. The
# variants are drawn in register, so one measurement is the right one for all of them.
$scan = $trayBitmaps["connected-base"]
$baseBounds = Get-Bounds -Bitmap $scan

# Rows the artwork occupies, then the widest empty run inside it.
$rowInk = New-Object 'bool[]' $scan.Height
for ($y = 0; $y -lt $scan.Height; $y++) {
    for ($x = 0; $x -lt $scan.Width; $x++) {
        if ($scan.GetPixel($x, $y).A -ge 128) { $rowInk[$y] = $true; break }
    }
}

$bestStart = -1; $bestLength = 0; $gapStart = -1
for ($y = $baseBounds.Top; $y -le $baseBounds.Bottom; $y++) {
    if (-not $rowInk[$y]) {
        if ($gapStart -lt 0) { $gapStart = $y }
    }
    elseif ($gapStart -ge 0) {
        $length = $y - $gapStart
        if ($length -gt $bestLength) { $bestLength = $length; $bestStart = $gapStart }
        $gapStart = -1
    }
}

$markMaxY = if ($bestStart -gt 0) { $bestStart + $bestLength - 1 } else { $baseBounds.Bottom }
Write-Host ("    lockup cut above y {0}" -f $markMaxY)

$trayBounds = [ordered]@{}
foreach ($key in $trayBitmaps.Keys) {
    $trayBounds[$key] = Get-Bounds -Bitmap $trayBitmaps[$key] -MaxY $markMaxY
}

# Within a state, every counted variant is framed by the union of all of them, so the
# mark holds still as the count ticks over instead of breathing between 1 and 9. The
# plain mark keeps its own tighter frame: an idle tray is the common case and gets the
# whole 16 pixels, and the resize that comes with the first session reads as the change
# it is. Framing is per state rather than across all of them because a state badge is
# drawn where the plain mark has nothing, and letting that shrink every other icon
# would spend the count's pixels on a corner it does not use.
$trayFrames = [ordered]@{}
foreach ($state in $trayStates) {
    $counted = Get-Union -Rectangles (
        $trayCounts | Where-Object { $_ -ne "base" } | ForEach-Object { $trayBounds["$state-$_"] })

    $trayFrames["$state-base"] = $trayBounds["$state-base"]
    foreach ($count in $trayCounts | Where-Object { $_ -ne "base" }) {
        $trayFrames["$state-$count"] = $counted
    }

    Write-Host ("    {0,-13} plain {1}  counted {2}" -f $state, $trayBounds["$state-base"], $counted)
}

function New-TrayFrame {
    param([System.Drawing.Bitmap]$Source, [System.Drawing.Rectangle]$Crop, [int]$Size)

    $canvas = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $available = $Size * (1.0 - (2.0 * $trayMargin))
    $ratio = [Math]::Min($available / $Crop.Width, $available / $Crop.Height)
    $drawW = [Math]::Max(1, [int][Math]::Round($Crop.Width * $ratio))
    $drawH = [Math]::Max(1, [int][Math]::Round($Crop.Height * $ratio))

    $target = New-Object System.Drawing.Rectangle `
        ([int][Math]::Round(($Size - $drawW) / 2.0)), `
        ([int][Math]::Round(($Size - $drawH) / 2.0)), `
        $drawW, `
        $drawH

    # DrawImage samples outside the source rectangle unless told not to, which drags
    # the transparent pixels around the artwork into its edges and leaves a halo.
    $wrap = New-Object System.Drawing.Imaging.ImageAttributes
    $wrap.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)

    $g.DrawImage($Source, $target, $Crop.X, $Crop.Y, $Crop.Width, $Crop.Height, [System.Drawing.GraphicsUnit]::Pixel, $wrap)

    $wrap.Dispose()
    $g.Dispose()

    return $canvas
}

foreach ($state in $trayStates) {
    foreach ($count in $trayCounts) {
        $key = "$state-$count"

        $frames = @()
        foreach ($size in $traySizes) {
            $bitmap = New-TrayFrame -Source $trayBitmaps[$key] -Crop $trayFrames[$key] -Size $size

            $stream = New-Object System.IO.MemoryStream
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += , @{ Size = $size; Bytes = $stream.ToArray() }

            $stream.Dispose()
            $bitmap.Dispose()
        }

        # The file name is the resource name: the daemon project globs this folder and
        # embeds each file as 1RemoteCLI.Tray.<filename>.ico, so a new state or count
        # needs no build edit at all -- drop the artwork in assets/tray, re-run this,
        # and it ships.
        $token = if ($count -eq "base") { "Base" } elseif ($count -eq "more") { "More" } else { $count }
        $face = $state.Substring(0, 1).ToUpperInvariant() + $state.Substring(1)
        Save-Ico -Frames $frames -Path (Join-Path $trayOut "$face.$token.ico")
    }
}

foreach ($bitmap in $trayBitmaps.Values) { $bitmap.Dispose() }

Write-Host ""
Write-Host "Done. Rebuild the agent and re-run scripts/publish-hub.ps1 to ship these."
