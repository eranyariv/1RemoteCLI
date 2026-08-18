# Deployment

How to deploy the hub, configure it, let someone in, and ship a new `1remote.exe`.

The one-time work — creating the Entra app registration and provisioning the resources — is in [Azure setup](azure-setup.md). This document assumes that has been done and covers the parts you do repeatedly.

## What is deployed today

| | |
| --- | --- |
| Subscription | `bbbbbbbb-cccc-dddd-eeee-ffffffffffff` (`owner@example.com`) |
| Resource group | `1remotecli-rg` |
| App Service plan | `1remotecli-plan` — **B1, one instance**, Israel Central |
| Web app | `1remotecli-hub` → https://1remotecli-hub.azurewebsites.net |
| Entra app (API + PWA) | `3db435ae-5e69-483c-a044-d6e8b6262fc6`, authority `.../common` |
| Entra app (agent) | `6a4e3951-3b1f-46f9-b20c-17bd30bf16f5`, authority `.../common` |

The hub serves the phone app from its own origin. There is no second site to deploy, no CDN, no CORS policy: the sign-in redirect, the WebSocket and the push deep link are all `https://1remotecli-hub.azurewebsites.net`.

## Sign in to Azure first

The CLI is scoped to this project so it cannot disturb whatever else you are signed in to:

```powershell
. .\scripts\az-env.ps1
```

Dot-source it — it sets `AZURE_CONFIG_DIR` to a per-project profile and disables the WAM broker, which a personal Microsoft account needs. Every `az` command below assumes you have done this in the same shell.

## Deploy the hub

```powershell
.\scripts\publish-hub.ps1
```

That is the whole thing. It builds the phone app, stages it into the hub's `wwwroot`, publishes the hub, zips it, deploys it, and then *verifies*: polls `/health` until it answers and asserts the root actually serves the app. If the script says it is done, it is done.

Use `-SkipDeploy` to build and stage without touching Azure — for inspecting the payload, or on a machine not signed in.

`-ResourceGroup` and `-WebApp` default to the values above; pass them to deploy a second environment.

Check it by hand any time:

```powershell
$base = 'https://1remotecli-hub.azurewebsites.net'
(Invoke-WebRequest "$base/health").Content        # {"status":"ok",...}
(Invoke-WebRequest "$base/push/vapid").Content     # {"key":"B..."}
(Invoke-WebRequest $base).Content -match 'id="root"'
```

### Do not build the app separately and forget

The one mistake worth naming: publishing the hub without rebuilding the app deploys whatever bundle happened to be in `wwwroot`, which on a developer machine may be a stale or development build. `src/Hub/wwwroot` is gitignored and rebuilt from scratch by the script every time, which is why the script exists.

## Configuration

Everything is App Service application settings. **No secrets in the repo.**

| Setting | Purpose |
| --- | --- |
| `Entra__Allowlist__0`, `__1`, … | Accounts allowed to use the hub. **Empty means nobody.** |
| `Push__Vapid__Subject` | `mailto:` address the push services contact about your key |
| `Push__Vapid__PublicKey` | Handed to the phone at `/push/vapid` |
| `Push__Vapid__PrivateKey` | Signs push messages. Secret. |
| `ASPNETCORE_ENVIRONMENT` | `Production` |

Read them back:

```powershell
az webapp config appsettings list -g 1remotecli-rg -n 1remotecli-hub --only-show-errors -o table
```

> The Azure CLI on Windows prints a `cryptography ... 32-bit Python` warning to stdout, which breaks `ConvertFrom-Json`. `--only-show-errors` suppresses it. If a command's output fails to parse, check whether the command itself actually succeeded before re-running it.

### Add someone to the allowlist

Find the highest index in use, then add the next one. Do **not** reuse an index — you will silently replace whoever was there.

```powershell
az webapp config appsettings set -g 1remotecli-rg -n 1remotecli-hub `
  --settings Entra__Allowlist__1=colleague@example.com --only-show-errors -o none
```

Entries can be an email address, a UPN, or a tenant-qualified object id. The hub matches on either side of the `@`, so an address and a UPN for the same person both work.

Setting app settings restarts the app. Every attached phone reconnects automatically and re-attaches to its session; expect a couple of seconds of *Reconnecting*, not a lost session.

**But a restart does drop every push subscription.** They are held in memory — the accepted cost of the no-database design — so after any restart, redeploy or App Service platform maintenance, notifications stop arriving until each phone opens the app once and re-registers. Nothing tells the user this has happened; their phone just goes quiet. If you restart the hub, tell people to open the app. Persisting only the subscriptions is the natural fix and is listed in spec §9.

An account that is signed in but not on the allowlist gets a clean refusal from the hub, not a sign-in failure. If someone reports "I can log in but there are no machines", check this first.

### VAPID keys

Generated once, at setup. To rotate them:

```powershell
# any machine with the tooling; the pair must be P-256, base64url, no padding
az webapp config appsettings set -g 1remotecli-rg -n 1remotecli-hub `
  --settings Push__Vapid__PublicKey=<new-public> Push__Vapid__PrivateKey=<new-private> `
             Push__Vapid__Subject=mailto:you@example.com --only-show-errors -o none
```

Rotating invalidates every existing push subscription: the browser tied its subscription to the old public key. Everyone has to open the app once and re-enable notifications. Only rotate if the private key leaked.

Never paste a private key into a chat, a ticket or a commit. If you do, rotate it.

## Why the plan stays at one instance

**The hub keeps its registry of connected agents in memory.** Scale out to two instances and half the phones land on the instance that has never heard of their machine, and see nothing. There is no error; the machine list is simply empty for some users some of the time, which is about the worst failure mode available.

So:

- Leave the plan at **capacity 1**.
- Do not enable autoscale.
- Leave **ARR affinity on** — harmless at one instance, and it is the thing that would keep a socket pinned if the count ever changed.
- Deployment slots with swap are fine; two *live* instances are not.

Making the hub scale out is a real change — a backplane, or a registry that is not in-process — not a slider. Treat the slider as broken.

WebSockets must be enabled and Always On must be on. Both are already set; the deploy script does not touch them, so if someone turns them off it will not be noticed until agents stop connecting.

```powershell
az webapp config set -g 1remotecli-rg -n 1remotecli-hub --web-sockets-enabled true --always-on true --only-show-errors -o none
```

## Versioning

One number for the whole product, written `x.yy`, starting at `0.01` and going up by `0.01` every release. `0.99` is followed by `1.00` — the two digits are a counter, not a fraction, so there is no `0.100`.

It lives in one file, `VERSION`, at the root of the repository. `Directory.Build.props` reads it and stamps every .NET assembly; `vite.config.ts` reads the same file and injects it into the PWA. Nothing else carries a version of its own, so the agent, the hub and the app cannot disagree about which build somebody is running — which is the entire point, because the first question any bug report needs answered is "which version?" and the person answering it is looking at a phone.

The user sees it in two places: the settings window's **Version** line, and the footer of the PWA. `1remote --version` prints it, the hub returns it from `/health`, and the agent reports it to the hub on every connect.

`x.yy` is not a version the tooling accepts — NuGet and the assembly metadata want three numeric parts, and `0.01` is not one — so the numeric form is derived: `0.01` is assembly version `0.1.0`, `0.10` is `0.10.0`. The mapping loses nothing because `yy` is always two digits, and a test asserts the two stay in step.

To move it on:

```powershell
.\scripts\bump-version.ps1          # 0.01 -> 0.02
.\scripts\bump-version.ps1 -To 1.00 # or set it outright
```

That edits `VERSION` and nothing else. Committing and tagging are left to you, because pushing the tag is the irreversible half.

## Release the agent

Users install with a one-liner that downloads from a GitHub Release and checks the hash:

```powershell
irm https://raw.githubusercontent.com/eranyariv/1RemoteCLI/main/scripts/install.ps1 | iex
```

Cutting a release is a bump and a tag:

```powershell
.\scripts\bump-version.ps1
git commit -am 'Release 0.02'
git push
git tag v0.02
git push origin v0.02
```

`.github/workflows/release.yml` then builds `win-x64` and `win-arm64` and publishes a release carrying `1remote-win-x64.exe`, `1remote-win-arm64.exe` and `SHA256SUMS.txt`.

The tag does not decide the version — `VERSION` does. The workflow refuses to run if the tag disagrees with the file, rather than producing a release called one thing containing binaries that call themselves another, and it checks the published binary actually prints the version it claims. It can also be run from the Actions tab, which releases whatever `VERSION` says on `main`.

Both architectures are cross-published from the same x64 runner. There is no hosted arm64 runner, and the release does not need one — nothing about the publish is architecture-specific, and the tests that would exercise the code already run in CI.

**While the repository is private**, the one-liner needs a token that can read releases:

```powershell
$env:GITHUB_TOKEN = gh auth token
```

Making the repository public would remove that step; nothing else about the flow depends on it.

## Package the agent by hand

```powershell
.\scripts\publish-agent.ps1
```

Produces one self-contained `1remote.exe` in `artifacts/win-x64/` — no .NET install needed on the target machine, nothing to unpack — and prints its SHA-256. `-Runtime win-arm64` for a Snapdragon machine. This is the same script the release workflow runs, so a release is never built by a path nobody has run locally, and it takes no version argument because the build reads `VERSION`.

It is roughly 13 MB. It was 70 until [#46](https://github.com/eranyariv/1RemoteCLI/issues/46) removed the Windows Forms reference held for a single tray icon — which brought the entire Windows Desktop runtime with it — and turned on trimming. Both settings live in `src/Daemon/1RemoteCLI.Daemon.csproj`, not in this script, so a release built by the workflow is the same size as one built by hand.

### It is not signed

There is no code-signing certificate for this project, so SmartScreen warns on first run on every machine. The SHA-256 published beside each download is what stands in for a signature:

```powershell
Get-FileHash .\1remote-win-x64.exe -Algorithm SHA256
```

`install.ps1` checks it automatically and refuses to install on a mismatch, with no switch to skip it — a check everyone is told to click through protects nobody.

That is still a genuinely weaker guarantee than a signature: it only proves the file matches the one the workflow built, not who built it. If this ever leaves a small trusted group, buy a certificate.

### Install path and upgrades

`install.ps1` puts the executable at `%LOCALAPPDATA%\Programs\1RemoteCLI\1remote.exe` and runs `1remote install`, which registers the logon task, the Start menu entries and the `PATH` entry.

If you install by hand, the location is your choice but it has to be **stable**. `1remote install` registers a scheduled task pointing at wherever the executable is at that moment; move it afterwards and the agent stops starting at logon with no error.

To upgrade, re-run the one-liner: it stops the running agent — the file is locked while it runs — overwrites in place, and re-registers.

By hand:

1. Quit the agent from the tray icon, or `Get-Process 1remote | Stop-Process`.
2. Overwrite `1remote.exe` **in place**.
3. Start it again — `1remote agent`, or log out and back in.

There is no per-version state: same path, so the scheduled task still points at it; the token cache and its format are unchanged; running sessions belong to the wrapper processes and die with the shells they wrap, which they would have anyway.

Only re-run `1remote install` if you moved the executable.

## Icons

Every icon — six PNGs for the phone app and browser, and the `.ico` compiled into the executable — is generated from one tracked source, `assets/logo.png`:

```powershell
.\scripts\make-icons.ps1
```

Then rebuild the agent and redeploy the hub to ship them. Change the artwork and re-run the script; do not hand-edit the outputs, because there are eight of them and the usual failure is that four get updated and the rest quietly stay a year behind.

The tray icon is deliberately not generated from the logo. It is a status indicator, not a brand mark: it shows connected, connecting and disconnected as distinct *shapes* so it stays readable at 16px and without relying on colour. See `src/Daemon/Tray/TrayIcon.cs`.

## Rolling back

The deployment is a zip push, so the fastest rollback is to redeploy from a known-good commit:

```powershell
git checkout <good-sha>
.\scripts\publish-hub.ps1
```

The hub holds no persistent state — no database, no migrations, nothing to undo. Rolling back is just running the older code. Sessions survive it the same way they survive a restart: agents and phones reconnect within a few seconds.

## Health

There is no monitoring wired up. `/health` returns status and version and is what the deploy script polls; point an uptime check at it if this becomes something people depend on. Hub logs go to App Service's log stream:

```powershell
az webapp log tail -g 1remotecli-rg -n 1remotecli-hub --only-show-errors
```

Agent-side logging is in [Logging](logging.md).
