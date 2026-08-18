<#
.SYNOPSIS
    Downloads and installs the 1RemoteCLI Windows agent.

.DESCRIPTION
    The one-liner an install lives or dies by:

        irm https://raw.githubusercontent.com/eranyariv/1RemoteCLI/main/scripts/install.ps1 | iex

    Picks the build for this machine's architecture, checks it against the SHA-256
    published with the release, puts it under %LOCALAPPDATA%, and runs
    `1remote install` so the logon task, the Start Menu entries and the PATH entry
    are all registered.

    The hash check is not optional and there is no switch to skip it. These builds
    are unsigned, so SmartScreen's warning is the only thing standing between the
    user and a download they cannot verify -- and a warning everyone is told to
    click through protects nobody. The checksum published alongside the build is
    what actually establishes that the file came from the release it claims to.

    Per user, never machine-wide. The agent runs as the signed-in user, holds that
    user's token cache and starts from that user's logon task; installing it into
    Program Files would need elevation to achieve nothing.

.PARAMETER Version
    A specific release, like `0.02`. Defaults to the latest.

.PARAMETER InstallDirectory
    Defaults to %LOCALAPPDATA%\Programs\1RemoteCLI.

.PARAMETER Repository
    owner/name on GitHub. Only useful for testing against a fork.

.EXAMPLE
    irm https://raw.githubusercontent.com/eranyariv/1RemoteCLI/main/scripts/install.ps1 | iex

.EXAMPLE
    .\scripts\install.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\1RemoteCLI'),
    [string] $Repository = 'eranyariv/1RemoteCLI'
)

$ErrorActionPreference = 'Stop'

# Read once at the top so that a partial download or a failed hash check leaves the
# machine exactly as it was found.
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string] $message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

if ($PSVersionTable.Platform -and $PSVersionTable.Platform -ne 'Win32NT') {
    throw 'The 1RemoteCLI agent is a Windows program. Install it on the machine whose terminals you want to reach.'
}

# The process architecture is the wrong question -- 32-bit PowerShell on an x64
# machine is a real configuration -- so ask the OS.
$architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'win-x64' }
    'Arm64' { 'win-arm64' }
    default { throw "There is no 1RemoteCLI build for $_." }
}

$asset = "1remote-$architecture.exe"

Write-Step "Looking for the $(if ($Version) { "v$Version" } else { 'latest' }) release"

$api = if ($Version) {
    "https://api.github.com/repos/$Repository/releases/tags/v$Version"
}
else {
    "https://api.github.com/repos/$Repository/releases/latest"
}

$headers = @{ 'User-Agent' = '1RemoteCLI-install' }

# The repository is public, so this runs without a token. GITHUB_TOKEN is still
# honoured because unauthenticated API calls are rate-limited per IP address, and
# a shared address can exhaust that allowance without the caller doing anything.
if ($env:GITHUB_TOKEN) {
    $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN"
}

try {
    $release = Invoke-RestMethod $api -Headers $headers
}
catch {
    throw "Could not read the release from GitHub: $($_.Exception.Message). If this is a rate limit, set GITHUB_TOKEN to any GitHub token and run it again."
}

$tag = $release.tag_name

<#
    Downloads one asset of the release.

    Through the API rather than `browser_download_url`: that URL is a github.com
    page, and for a caller who cannot see the release it answers with an HTML
    "Page not found" -- which lands on disk as a 200 and only shows up as a hash
    mismatch, or worse, as a 60 KB "executable". The API asset URL with
    `Accept: application/octet-stream` returns the bytes.
#>
function Save-Asset([string] $name, [string] $destination) {
    $found = $release.assets | Where-Object { $_.name -eq $name }

    if (-not $found) {
        throw "Release $tag has no asset called $name."
    }

    Invoke-WebRequest $found.url `
        -Headers ($headers + @{ Accept = 'application/octet-stream' }) `
        -OutFile $destination
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("1remotecli-" + [guid]::NewGuid().ToString('n'))
New-Item $temp -ItemType Directory -Force | Out-Null

try {
    Write-Step "Downloading $asset from $tag"

    $downloaded = Join-Path $temp $asset

    Save-Asset $asset $downloaded
    Save-Asset 'SHA256SUMS.txt' (Join-Path $temp 'SHA256SUMS.txt')

    $expected = $null

    # Parsed by hand rather than with a clever pipeline, because getting this wrong
    # quietly -- matching nothing and comparing against an empty string -- would turn
    # the check into decoration.
    foreach ($line in Get-Content (Join-Path $temp 'SHA256SUMS.txt')) {
        $parts = $line.Trim() -split '\s+', 2

        if ($parts.Count -eq 2 -and $parts[1].Trim() -eq $asset) {
            $expected = $parts[0].Trim().ToLowerInvariant()
        }
    }

    if (-not $expected) {
        throw "SHA256SUMS.txt in $tag does not list $asset, so there is nothing to check the download against."
    }

    $actual = (Get-FileHash $downloaded -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actual -ne $expected) {
        throw "The download does not match the published hash and has NOT been installed.`n  expected  $expected`n  got       $actual"
    }

    Write-Step "Checked against the published SHA-256"

    # Windows will not let the file be replaced while it is running, and the message
    # it gives says nothing about why.
    Get-Process -Name '1remote' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDirectory, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            Write-Step "Stopping the agent that is already running"
            Stop-Process -Id $_.Id -Force
            Start-Sleep -Milliseconds 500
        }

    New-Item $InstallDirectory -ItemType Directory -Force | Out-Null

    $installed = Join-Path $InstallDirectory '1remote.exe'
    Copy-Item $downloaded $installed -Force

    # Downloads carry the mark of the web, and it survives the copy. Left on, every
    # launch of a command-line tool raises a SmartScreen prompt. The file has just
    # been checked against the hash the release publishes, which is a stronger claim
    # than the mark was making.
    Unblock-File $installed

    Write-Step "Installed to $installed"

    & $installed install

    if ($LASTEXITCODE -ne 0) {
        throw "1remote install failed. The executable is in place; run '$installed install' to see why."
    }

    Write-Host ''
    Write-Host 'Open a new terminal, then:' -ForegroundColor Green
    Write-Host '  1remote login     sign in' -ForegroundColor Green
    Write-Host '  1remote claude    run something you want to reach from your phone' -ForegroundColor Green
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
