<#
.SYNOPSIS
    Builds the phone app into the hub and publishes both to Azure App Service.

.DESCRIPTION
    The app is served by the hub, from the hub's own origin. That is what makes the
    SignalR endpoint same-origin -- no CORS, one certificate, one redirect URI
    registered in Entra, and one thing to deploy rather than two that can drift apart.

    So a hub deployment is: build the app, stage it into the hub's wwwroot, publish
    the hub, zip, push. Doing that by hand is four commands with one easy mistake in
    it -- forgetting the app build, which deploys a hub carrying whatever bundle was
    left in wwwroot last time, possibly a development one.

    Nothing here needs a secret. Configuration (allowlist, VAPID) is set separately
    with `az webapp config appsettings set`; see docs/deployment.md.

.PARAMETER ResourceGroup
    Defaults to the group in docs/azure-setup.md.

.PARAMETER WebApp
    Defaults to the web app in docs/azure-setup.md.

.PARAMETER SkipDeploy
    Build and stage everything, then stop. Useful for inspecting the payload, and for
    running the whole thing on a machine that is not signed in to Azure.

.EXAMPLE
    . .\scripts\az-env.ps1
    .\scripts\publish-hub.ps1
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = '1remotecli-rg',
    [string] $WebApp = '1remotecli-hub',
    [switch] $SkipDeploy
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$pwa = Join-Path $repo 'src\PWA'
$hub = Join-Path $repo 'src\Hub\1RemoteCLI.Hub.csproj'
$wwwroot = Join-Path $repo 'src\Hub\wwwroot'

$staging = Join-Path ([IO.Path]::GetTempPath()) "1remote-hub-$(Get-Date -Format yyyyMMdd-HHmmss)"
$publish = Join-Path $staging 'publish'
$zip = Join-Path $staging 'hub.zip'

function Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

Step 'Building the phone app'

Push-Location $pwa
try {
    # `npm ci` rather than `npm install`: a deployment should build the lockfile's
    # dependency tree, not resolve a fresh one that nobody has tested.
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }

    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'The app build failed.' }
}
finally {
    Pop-Location
}

Step 'Staging it into the hub'

# Emptied rather than merged. A stale file from a previous build stays cached on
# every phone that ever fetched it, and an asset nobody references is impossible to
# notice.
if (Test-Path $wwwroot) {
    Remove-Item $wwwroot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
Copy-Item -Path (Join-Path $pwa 'dist\*') -Destination $wwwroot -Recurse -Force

$index = Join-Path $wwwroot 'index.html'
if (-not (Test-Path $index)) {
    throw "The app build produced no index.html. Nothing would be served."
}

Step 'Publishing the hub'

dotnet publish $hub -c Release -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if (-not (Test-Path (Join-Path $publish 'wwwroot\index.html'))) {
    throw 'The published output carries no app. Check that wwwroot was staged before publishing.'
}

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force

$size = '{0:N1} MB' -f ((Get-Item $zip).Length / 1MB)
Write-Host "Package: $zip ($size)" -ForegroundColor Green

if ($SkipDeploy) {
    Write-Host 'Skipping deployment as asked.' -ForegroundColor Yellow
    return
}

Step "Deploying to $WebApp"

# --clean mirrors the package rather than merging into whatever is already there.
# Without it the deploy only ever adds and overwrites, so a renamed or deleted asset
# is served forever -- and the symptom, a phone still showing the old file, looks
# like device caching and gets misdiagnosed as one.
az webapp deploy --resource-group $ResourceGroup --name $WebApp --src-path $zip --type zip --clean true --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'The deployment failed.' }

Step 'Verifying'

$host_ = az webapp show -g $ResourceGroup -n $WebApp --query defaultHostName -o tsv --only-show-errors
$base = "https://$host_"

# App Service restarts the worker after a zip deploy, so the first request can arrive
# before the app is listening.
$deadline = (Get-Date).AddMinutes(3)
$health = $null

while ((Get-Date) -lt $deadline) {
    try {
        $health = Invoke-RestMethod "$base/health" -TimeoutSec 20
        break
    }
    catch {
        Start-Sleep -Seconds 5
    }
}

if (-not $health) {
    throw "$base/health never answered. Check the App Service log stream."
}

Write-Host "health: $($health.status), version $($health.version)" -ForegroundColor Green

$app = Invoke-WebRequest $base -UseBasicParsing -TimeoutSec 30
if ($app.Content -notmatch '<div id="root"') {
    throw "$base served something that is not the app."
}

Write-Host "app:    served from $base" -ForegroundColor Green

try {
    Invoke-RestMethod "$base/push/vapid" -TimeoutSec 20 | Out-Null
    Write-Host 'push:   configured' -ForegroundColor Green
}
catch {
    Write-Host 'push:   not configured -- notifications are off (see docs/deployment.md)' -ForegroundColor Yellow
}
