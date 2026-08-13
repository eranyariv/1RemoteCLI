<#
.SYNOPSIS
    Scopes the Azure CLI to this project only.

.DESCRIPTION
    Points AZURE_CONFIG_DIR at a profile directory dedicated to 1RemoteCLI, so
    `az login`, the selected subscription, and cached tokens for this project are
    completely separate from the machine-wide Azure CLI profile in ~/.azure.

    The profile lives OUTSIDE the repository on purpose:
      * it holds refresh tokens, so it must never be committed;
      * git worktrees would otherwise each need their own login.

    Dot-source it, do not run it -- the environment variable has to land in the
    calling shell:

        . .\scripts\az-env.ps1
        az login --allow-no-subscriptions
        az account set --subscription "<name-or-id>"

.PARAMETER Path
    Override the profile directory. Defaults to ~/.azure-profiles/1RemoteCLI.

.PARAMETER Quiet
    Suppress the status banner.
#>
[CmdletBinding()]
param(
    [string] $Path = (Join-Path $HOME '.azure-profiles\1RemoteCLI'),
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

$env:AZURE_CONFIG_DIR = (Resolve-Path -LiteralPath $Path).Path

# The Windows account broker (WAM) binds `az login` to the native Windows account
# picker, which cannot sign in a personal Microsoft account -- selecting
# "Use another account" just dismisses the dialog. Force the loopback browser
# redirect instead. This writes into $AZURE_CONFIG_DIR/config, so the
# machine-wide profile is unaffected.
if (-not (Test-Path -LiteralPath (Join-Path $env:AZURE_CONFIG_DIR 'config'))) {
    az config set core.enable_broker_on_windows=false --only-show-errors 2>$null | Out-Null
}

if ($Quiet) {
    return
}

Write-Host "AZURE_CONFIG_DIR = $env:AZURE_CONFIG_DIR" -ForegroundColor Cyan

$signedIn = $null
try {
    $signedIn = az account show --output json 2>$null | ConvertFrom-Json
}
catch {
    $signedIn = $null
}

if ($signedIn) {
    Write-Host "Signed in as $($signedIn.user.name) -> $($signedIn.name) ($($signedIn.id))" -ForegroundColor Green
}
else {
    Write-Host "Not signed in. Run: az login --allow-no-subscriptions" -ForegroundColor Yellow
}
