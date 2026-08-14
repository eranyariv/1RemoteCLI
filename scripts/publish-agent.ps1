<#
.SYNOPSIS
    Packages the Windows side into a single `1remote.exe`.

.DESCRIPTION
    One self-contained file, no .NET install required, nothing to unpack. That shape
    is chosen for what the binary has to be rather than for elegance: `1remote` is a
    wrapper you type in front of a command you were about to run anyway, so a runtime
    prerequisite would turn "try this" into a support conversation, and a folder of
    DLLs would make it something you install rather than something you drop on the
    PATH.

    ReadyToRun is deliberately OFF and compression is ON, which is the opposite of the
    usual advice. Measured on this project: ReadyToRun uncompressed is 180 MB and
    starts in 118 ms; no ReadyToRun compressed is 70 MB and starts in 168 ms;
    ReadyToRun compressed is the worst of both at 76 MB and 618 ms, because the larger
    ReadyToRun images have to be decompressed on every launch. Fifty milliseconds is
    invisible next to the start-up of anything worth wrapping -- a shell, a coding
    agent -- and a 180 MB download for a personal tool looks broken. So: small.

    The size is dominated by Windows Forms, which is there for one tray icon and
    brings the whole desktop runtime with it. See issue #46.

    The build is NOT signed. There is no code-signing certificate for this project, so
    SmartScreen will warn the first time it runs. The published SHA-256 is printed
    here and recorded in docs/deployment.md so the download can at least be checked
    against the build.

.PARAMETER Output
    Where to put the result. Defaults to `artifacts/` at the repository root.

.PARAMETER Runtime
    Defaults to win-x64. Use win-arm64 for a Snapdragon machine.

.EXAMPLE
    .\scripts\publish-agent.ps1
#>
[CmdletBinding()]
param(
    [string] $Output,
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Daemon\1RemoteCLI.Daemon.csproj'

if (-not $Output) {
    $Output = Join-Path $repo "artifacts\$Runtime"
}

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

Write-Host "==> Publishing 1remote.exe for $Runtime" -ForegroundColor Cyan

dotnet publish $project `
    -c Release `
    -r $Runtime `
    -o $Output `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=none `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

$exe = Join-Path $Output '1remote.exe'

if (-not (Test-Path $exe)) {
    throw "Published, but there is no 1remote.exe in $Output."
}

# The point of a single file is that it is a single file. If publishing ever starts
# leaving loose assemblies behind -- a trimming setting, a native dependency that
# cannot be embedded -- the result still runs from this folder and fails the moment
# somebody copies just the exe onto their PATH, which is exactly how it will be used.
$loose = Get-ChildItem $Output -File | Where-Object { $_.Name -ne '1remote.exe' }

if ($loose) {
    throw "The publish left files beside the executable, so it is not self-contained: $($loose.Name -join ', ')"
}

$item = Get-Item $exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$version = $item.VersionInfo.FileVersion

Write-Host ''
Write-Host "  path     $exe" -ForegroundColor Green
Write-Host ("  size     {0:N1} MB" -f ($item.Length / 1MB)) -ForegroundColor Green
Write-Host "  version  $version" -ForegroundColor Green
Write-Host "  sha256   $hash" -ForegroundColor Green
Write-Host ''
Write-Host 'Unsigned: Windows will warn on first run. See docs/deployment.md.' -ForegroundColor Yellow
