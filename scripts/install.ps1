<#
.SYNOPSIS
    Downloads and installs the 1RemoteCLI Windows agent.

.DESCRIPTION
    The one-liner an install lives or dies by:

        irm https://raw.githubusercontent.com/eranyariv/1RemoteCLI/main/scripts/install.ps1 | iex

    Picks the build for this machine's architecture, checks it against the SHA-256
    published with the release, puts it under %LOCALAPPDATA%, and runs
    `1remote install` so the logon task, the Start Menu entries and the PATH entry
    are all registered -- and the agent is started, so the tray icon is there when
    this finishes rather than after the next logon.

    The hash check is not optional and there is no switch to skip it. Nothing else on
    this path verifies anything: the builds are unsigned, and SmartScreen -- which
    people assume is the backstop -- never sees the file, because it inspects
    downloads carrying a mark of the web and Invoke-WebRequest attaches none. The
    checksum published alongside the build is the only thing that establishes that
    the file came from the release it claims to.

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

<#
    The process architecture is the wrong question -- 32-bit PowerShell on an x64
    machine is a real configuration -- so ask the OS.

    Through the environment rather than
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture, which
    cannot be trusted here: PSReadLine ships a public type of exactly that full
    name carrying a single OSDescription property, and the console host loads it
    into every interactive session before this script arrives. The type-name
    lookup finds PSReadLine's, the missing static property evaluates to $null
    with no error, and the switch below lands on `default` with nothing to
    report. Since `irm | iex` runs in the caller's session, a script fetched this
    way inherits whatever assemblies that session happens to have loaded, so any
    bare framework type is a hazard, not just this one.

    PROCESSOR_ARCHITEW6432 exists only in a 32-bit process on 64-bit Windows and
    holds the real OS architecture; PROCESSOR_ARCHITECTURE is the process's own.
    Preferring the first when it is set is what makes this an OS answer.
#>
$reported = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }

$architecture = switch ($reported) {
    'AMD64' { 'win-x64' }
    'ARM64' { 'win-arm64' }
    default {
        throw @"
There is no 1RemoteCLI build for this machine.
  PROCESSOR_ARCHITECTURE = '$env:PROCESSOR_ARCHITECTURE'
  PROCESSOR_ARCHITEW6432 = '$env:PROCESSOR_ARCHITEW6432'
  PowerShell             = $($PSVersionTable.PSVersion)
There are builds for x64 and Arm64 Windows. If this machine is one of those,
please report the three lines above at https://github.com/$Repository/issues.
"@
    }
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

<#
    The latest tag, so that knowing which release is current does not require the
    API.

    /releases/latest redirects to /releases/tag/vX.Y, so following it and reading
    where it landed is the whole trick. A HEAD request, because the answer is in the
    URL and the page body is several hundred KB of HTML nobody wants.

    Reading where it landed is the part that needs care: Windows PowerShell 5.1 --
    what `irm ... | iex` runs on a default Windows install -- exposes it as
    BaseResponse.ResponseUri, and PowerShell 7 as BaseResponse.RequestMessage.
    RequestUri. Asking for the redirect directly, with -MaximumRedirection 0, looks
    tidier and is worse: 5.1 raises an InvalidOperationException carrying no response
    at all, so there is nothing left to read the Location header from.
#>
function Get-LatestTag {
    try {
        $response = Invoke-WebRequest "https://github.com/$Repository/releases/latest" `
            -Headers $headers -UseBasicParsing -Method Head -ErrorAction Stop
    }
    catch {
        return $null
    }

    $landed = $null

    try { $landed = $response.BaseResponse.ResponseUri } catch { }
    if (-not $landed) { try { $landed = $response.BaseResponse.RequestMessage.RequestUri } catch { } }

    if (-not $landed) {
        return $null
    }

    $tag = ($landed.ToString().TrimEnd('/') -split '/')[-1]

    # A repository with no releases at all redirects nowhere, leaving the URL we
    # asked for and a "tag" of 'latest'.
    if ($tag -eq 'latest') { $null } else { $tag }
}

<#
    Two ways to find the release, because the first one has an allowance and the
    second does not.

    The API is preferred: it lists the assets, so a download is either the asset we
    asked for or a clean error. But it permits 60 anonymous calls an hour per IP
    address, counted across everyone behind it -- so on an office network the very
    first thing a new user runs can fail because of strangers, and telling them to go
    and mint a GITHUB_TOKEN before they can install anything is not an answer.

    The fallback is the plain download URL, which needs no API, no token and has no
    allowance. Its weakness is the one described on Save-Asset below: to a caller who
    cannot see the release it answers with an HTML "Page not found" and a 200, which
    lands on disk looking like a file. The published hash catches that for the
    executable, and Save-Asset checks the checksum file itself, so the failure says
    what it is instead of arriving as a mismatch.
#>
$release = $null

try {
    $release = Invoke-RestMethod $api -Headers $headers
    $tag = $release.tag_name
}
catch {
    $apiError = $_.Exception.Message

    if ($Version) {
        $tag = "v$Version"
    }
    else {
        # Where /releases/latest redirects to is the latest tag -- the one thing the
        # API was actually needed for.
        $tag = Get-LatestTag

        if (-not $tag) {
            throw "Could not read the release from GitHub, either through the API ($apiError) or by following https://github.com/$Repository/releases/latest. If this machine reaches the internet through a proxy, that is the thing to look at."
        }
    }

    Write-Host "    the GitHub API would not answer ($apiError)" -ForegroundColor DarkGray
    Write-Host "    downloading $tag directly instead, which needs no API" -ForegroundColor DarkGray
}

<#
    Downloads one asset of the release.

    Through the API where the API answered, because `browser_download_url` is a
    github.com page and, for a caller who cannot see the release, it serves an HTML
    "Page not found" as a 200 -- which lands on disk as a file and shows up only as a
    hash mismatch, or worse, as a 60 KB "executable". The API asset URL with
    `Accept: application/octet-stream` returns the bytes.

    Where the API did not answer there is no such luxury, so the same risk is met by
    checking what arrived: the executable against its published hash below, and
    SHA256SUMS.txt here, because a page of HTML would otherwise be reported as "the
    release does not list this asset" and send the reader looking in the wrong place.
#>
function Save-Asset([string] $name, [string] $destination) {
    if ($release) {
        $found = $release.assets | Where-Object { $_.name -eq $name }

        if (-not $found) {
            throw "Release $tag has no asset called $name."
        }

        Invoke-WebRequest $found.url `
            -Headers ($headers + @{ Accept = 'application/octet-stream' }) `
            -OutFile $destination
    }
    else {
        Invoke-WebRequest "https://github.com/$Repository/releases/download/$tag/$name" `
            -Headers $headers -OutFile $destination
    }

    if (-not (Test-Path $destination) -or (Get-Item $destination).Length -eq 0) {
        throw "Downloading $name from $tag produced nothing."
    }
}

$temp = Join-Path $env:TEMP ("1remotecli-" + [guid]::NewGuid().ToString('n'))
New-Item $temp -ItemType Directory -Force | Out-Null

try {
    Write-Step "Downloading $asset from $tag"

    $downloaded = Join-Path $temp $asset

    Save-Asset $asset $downloaded
    Save-Asset 'SHA256SUMS.txt' (Join-Path $temp 'SHA256SUMS.txt')

    $sums = Get-Content (Join-Path $temp 'SHA256SUMS.txt') -Raw

    # A download URL that cannot find the release answers with a web page and a 200,
    # so say so here rather than letting a page of HTML be reported as a release that
    # forgot to list its own asset.
    if ($sums -match '^\s*<') {
        throw "GitHub served a web page instead of the checksums for $tag. If that tag exists and is public, this is probably a proxy or a captive network sitting in front of github.com."
    }

    $expected = $null

    # Parsed by hand rather than with a clever pipeline, because getting this wrong
    # quietly -- matching nothing and comparing against an empty string -- would turn
    # the check into decoration.
    foreach ($line in ($sums -split "`r?`n")) {
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

    <#
        Windows will not let the file be replaced while it is running, and the message
        it gives says nothing about why.

        Anything named 1remote counts, not only the copies whose path can be read.
        Path comes from MainModule.FileName, which a normal shell cannot read for a
        process running with rights it does not have -- so requiring it to match the
        install directory silently skips the agent most likely to be holding the file
        open, and the install then fails on Copy-Item with no idea what stopped it.
    #>
    $running = @(Get-Process -Name '1remote' -ErrorAction SilentlyContinue | Where-Object {
            $path = try { $_.Path } catch { $null }
            -not $path -or $path.ToLowerInvariant().StartsWith($InstallDirectory.ToLowerInvariant())
        })

    if ($running) {
        Write-Step "Stopping the agent that is already running"

        foreach ($process in $running) {
            try {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
            catch {
                Write-Host "    could not stop process $($process.Id): $($_.Exception.Message)" -ForegroundColor DarkGray
            }
        }

        Start-Sleep -Milliseconds 500
    }

    New-Item $InstallDirectory -ItemType Directory -Force | Out-Null

    $installed = Join-Path $InstallDirectory '1remote.exe'

    <#
        Retried, because a process that has just been killed can keep its image open
        for a moment and a single immediate attempt loses a race it would win a second
        later. If it never wins, the message names what is holding the file: an
        elevated agent cannot be stopped from an ordinary shell, and "another process"
        does not tell anybody that.
    #>
    foreach ($attempt in 1..5) {
        try {
            Copy-Item $downloaded $installed -Force -ErrorAction Stop
            break
        }
        catch {
            if ($attempt -eq 5) {
                $holding = @(Get-Process -Name '1remote' -ErrorAction SilentlyContinue |
                    ForEach-Object { "      PID $($_.Id), started $($_.StartTime.ToString('HH:mm'))" })

                throw @"
$installed is in use and could not be replaced.

$(if ($holding) { "These are still running:`n" + ($holding -join "`n") } else { 'Nothing named 1remote appears to be running, so something else is holding it open.' })

An agent started from an elevated prompt cannot be stopped by this one. Close it, or
run the install from an elevated PowerShell, and try again. Logging off and back on
also clears it, since the agent starts from a logon task.
"@
            }

            Start-Sleep -Seconds 1
        }
    }

    # Belt and braces. Invoke-WebRequest attaches no mark of the web, so there is
    # normally nothing here to clear -- but a download that acquired one some other
    # way would survive the copy and make every launch of a command-line tool raise a
    # SmartScreen prompt. The file has just been checked against the hash the release
    # publishes, which is a stronger claim than the mark would be making.
    Unblock-File $installed

    Write-Step "Installed to $installed"

    <#
        Windows sometimes refuses the first launch. On a machine managed by an
        organisation that is the attack surface reduction rule "Use advanced
        protection against ransomware": it surfaces as "Access is denied" plus a
        Windows Security popup naming powershell.exe, which does not mention the
        executable it actually stopped. Rule C1DB55AB-C21A-4637-BB3F-A12568109D35,
        event 1121.

        Up to 0.08 this was self-inflicted and near-certain on such a machine: the
        release was a compressed single-file bundle, which is a self-extracting
        high-entropy blob and therefore the shape of a packer. Compression is off
        from 0.09 and both machines that used to refuse now accept it. What is left
        here is for the case where something refuses it anyway, where retrying is
        often enough -- the verdict can lift within seconds, once the file has been
        submitted and come back clean.

        Each blocked attempt costs a couple of seconds inside Windows, so this is
        bounded by attempts rather than by a deadline.
    #>
    $attempts = 6
    $ran = $false

    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            & $installed install
            $ran = $true
            break
        }
        catch {
            if ($attempt -eq $attempts) {
                throw @"
Windows would not let '$installed' start, after $attempts attempts: $($_.Exception.Message)

The download is installed and its hash matched the release, so the file is fine.
Something on this machine is refusing to run it -- on a managed machine, usually the
attack surface reduction rule "Use advanced protection against ransomware". Windows
Security > Protection history will name it, and will blame powershell.exe rather than
the file it stopped.

Worth trying, in this order:

  Wait. Some machines relent after twenty minutes or so; others never do. Once it is
  allowed, this finishes the install:
      & "$installed" install

  Run this same install from inside Copilot CLI or Claude Code, as a shell command.
  Windows partly judges a file by the process that wrote it, and a trusted writer
  improves the odds -- though measured carefully it is not reliable on its own.

  Run scripts\diagnose-launch.ps1 from the repository, which reports which protection
  is refusing it and how long it has been doing so.

If none of that helps, an administrator has to allow it.
"@
            }

            if ($attempt -eq 1) {
                Write-Step 'Windows is checking the download; waiting for it to allow the first run'
            }

            Start-Sleep -Milliseconds 500
        }
    }

    if ($ran -and $LASTEXITCODE -ne 0) {
        throw "1remote install failed. The executable is in place; run '$installed install' to see why."
    }

    Write-Host ''
    Write-Host 'The agent is running: its icon is in the notification area, by the clock.' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Open a new terminal, then:' -ForegroundColor Green
    Write-Host '  1remote login     sign in' -ForegroundColor Green
    Write-Host '  1remote claude    run something you want to reach from your phone' -ForegroundColor Green
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
