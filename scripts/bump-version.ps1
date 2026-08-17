<#
.SYNOPSIS
    Moves the product version on by one, the way releases here are numbered.

.DESCRIPTION
    The version is `x.yy`, it starts at 0.01, and every release adds 0.01. `x.99` is
    followed by `(x+1).00` — the two digits after the point are a counter, not a
    fraction, so there is no 0.100.

    It lives in one file, VERSION, at the root of the repository. The .NET build reads
    it from Directory.Build.props and the PWA reads it from vite.config.ts, so nothing
    else has to be edited and nothing can disagree.

    This only edits the file. Committing and tagging are left to you, because the
    release is cut from the tag and pushing a tag is the irreversible half.

.PARAMETER To
    Set the version explicitly instead of adding 0.01 — for correcting a mistake.
    Must still be `x.yy`.

.PARAMETER WhatIf
    Print what the new version would be and change nothing.

.EXAMPLE
    ./scripts/bump-version.ps1
    0.01 -> 0.02

.EXAMPLE
    ./scripts/bump-version.ps1 -To '1.00'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^\d+\.\d{2}$')]
    [string] $To
)

$ErrorActionPreference = 'Stop'

$file = Join-Path (Split-Path $PSScriptRoot -Parent) 'VERSION'

$current = (Get-Content $file -Raw).Trim()
if ($current -notmatch '^(\d+)\.(\d{2})$') {
    throw "VERSION holds '$current', which is not x.yy. Fix it by hand before bumping."
}

if ($To) {
    $next = $To
}
else {
    $major = [int] $Matches[1]
    $minor = [int] $Matches[2]

    # 99 is the last two-digit counter, so the next one rolls the major over.
    if ($minor -ge 99) {
        $major += 1
        $minor = 0
    }
    else {
        $minor += 1
    }

    $next = '{0}.{1:00}' -f $major, $minor
}

if ($next -eq $current) {
    Write-Host "Already $current."
    return
}

if ($PSCmdlet.ShouldProcess($file, "set version to $next")) {
    # No trailing newline games: everything that reads this file trims, and a single
    # terminating newline is what every editor and every diff expects.
    Set-Content -Path $file -Value $next -Encoding utf8 -NoNewline
    Add-Content -Path $file -Value "`n" -Encoding utf8 -NoNewline

    Write-Host "$current -> $next"
    Write-Host ''
    Write-Host 'Next:'
    Write-Host "  git commit -am 'Release $next' && git push"
    Write-Host "  git tag v$next && git push origin v$next"
}
else {
    Write-Host "$current -> $next (not written)"
}
