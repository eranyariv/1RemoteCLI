# Getting started

Fifteen minutes, most of it waiting for a download. At the end you will be answering a prompt on your PC from your phone.

You need a Windows 10 2004 (build 19041) machine or newer — that is where ConPTY became reliable — and a phone. Nothing to install on the phone: the app is a web page you add to your home screen.

## Before you start

Someone has to have deployed a hub and added your Microsoft account to its allowlist. If that is also you, do [Deployment](deployment.md) first and come back. If it is not, ask them for two things:

- the **hub address** — the default is compiled in, so usually you need nothing here
- confirmation that **your account is on the allowlist**, because if it is not, sign-in will succeed and then the hub will refuse you, which is a confusing five minutes

## 1. Install it

Run this in PowerShell:

```powershell
irm https://raw.githubusercontent.com/eranyariv/1RemoteCLI/main/scripts/install.ps1 | iex
```

The same applies to upgrades: run those from the CLI too, or the new build arrives untrusted and you are back where you started.

That picks the build for your architecture, **checks it against the SHA-256 published with the release**, puts it in `%LOCALAPPDATA%\Programs\1RemoteCLI`, registers the logon task and the Start menu entries, adds it to your `PATH` — and starts the agent, so its icon appears in the notification area straight away rather than after your next logon.

No GitHub account and no token: the repository is public, so the script and the release assets both download anonymously. The only reason to set `GITHUB_TOKEN` is the GitHub API rate limit, which is counted per IP address for unauthenticated callers; the script uses the token if it finds one and does not need it otherwise.

Open a new terminal — the `PATH` change only reaches terminals opened after it — and check:

```powershell
1remote --version
```

It prints the product version — `0.01` — which is the same number the agent's settings window shows and the same one the phone app shows in its footer. There is only ever one.

Both clients also carry a **Send feedback** link beside it, which opens your mail client with that version already in the subject.

### By hand instead

Download `1remote-win-x64.exe` (or `-win-arm64`) from the [latest release](https://github.com/eranyariv/1RemoteCLI/releases/latest) and check it against the hash in `SHA256SUMS.txt`:

```powershell
Get-FileHash .\1remote-win-x64.exe -Algorithm SHA256
```

Then put it where it is going to live — **not Downloads** — and install from there:

```powershell
$dir = "$env:LOCALAPPDATA\Programs\1RemoteCLI"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Move-Item .\1remote-win-x64.exe "$dir\1remote.exe" -Force
& "$dir\1remote.exe" install
```

**Where you put it matters more than it looks.** `1remote install` registers a scheduled task pointing at wherever the executable is at that moment. Move the file afterwards and the agent silently stops starting at logon, with no error anywhere — you just find out days later that your phone shows no machines. Move it back, or run `1remote install` again from the new location.

## 2. Sign in

```powershell
1remote login
```

A browser opens; sign in with the Microsoft account that is on the allowlist. Both personal Microsoft accounts and work/school accounts work.

The token is cached under `%LOCALAPPDATA%\1RemoteCLI`, encrypted with DPAPI scoped to your Windows user — another account on the same PC cannot read it, and neither can it be copied to a different machine and reused. You will not have to do this again; the agent refreshes silently, including mid-connection.

Confirm:

```powershell
1remote status
```

## 3. The agent

Installing started it and registered it to start again at every logon, so there is nothing to do here: you should already have a tray icon.

That icon is the agent — one per machine, owning the connection to the hub and the list of live sessions. Sessions are *not* shared unless it is running, and the wrapper will tell you so rather than quietly running unshared.

If there is no icon, start it by hand and see what it says:

```powershell
1remote agent
```

That runs in the foreground, which is also how you read its output when something is wrong. See [the agent does not start at logon](troubleshooting.md#the-agent-does-not-start-at-logon).

## 4. Share a session

Put `1remote` in front of whatever you were going to run:

```powershell
1remote claude
1remote copilot
1remote pwsh
```

It behaves exactly like running the program directly. Same colours, same keystrokes, same Ctrl+C, same exit code. The difference is that it is now also visible from your phone.

Name it if you are going to have several:

```powershell
1remote --name "nightly build" -- pwsh -NoLogo -File .\build.ps1
```

Everything after the program name is passed through untouched. Use `--` to end 1remote's own options explicitly when the program takes flags that look like 1remote's.

To run under the wrapper without sharing — occasionally useful for comparing behaviour — pass `--no-agent`.

## 5. Open it on your phone

Go to **https://1remotecli-hub.azurewebsites.net** in Safari or Chrome and sign in with the same account.

Your machine is in the list, with the sessions under it. Tap one and you are attached: you get the current screen immediately, not a blank terminal waiting for the next byte.

Now go and [add it to your home screen and turn on notifications](phone-setup.md) — without that last step you have to remember to check, which defeats the point.

## 6. The settings window

Double-click the tray icon, or right-click it and choose **Settings…**. Everything the
agent knows about itself is on one window:

- **Who is signed in**, and whether the hub connection is up. If your phone cannot see
  this machine, the second line says why and what it means — a machine that is
  reconnecting keeps its sessions running, and it says so.
- **The sessions on this machine**, live: what each one is, how long it has been there,
  and whether it is sitting at a prompt waiting for an answer. It is the same judgement
  that decides whether to notify your phone.
- **Start when I sign in to Windows** — a checkbox, and it is read from Task Scheduler
  and the registry every time the window opens rather than remembered. If somebody
  turned the agent off in Task Manager's Startup tab, this shows it.
- **Wrap a desktop shortcut…**, below.
- The version, the logs and **Send feedback…**.

The tray menu itself is deliberately short: the account, **Settings…**, the web app and
**Quit**. Anything that lives in the window does not also live on the menu, so there is
only ever one answer to "am I signed in".

Both follow the light or dark theme you chose in Windows Settings, and switch the moment
you change it — no restart.

## 7. Wrap a desktop shortcut

If you start your CLI from a shortcut on your desktop rather than by typing its name,
nothing above helps: there is no command line to put `1remote` in front of. So make a
copy of the shortcut that goes through 1remote.

In the settings window, click **Wrap a desktop shortcut…** and pick the `.lnk`. Or from
a terminal:

```powershell
1remote wrap-shortcut "$env:USERPROFILE\Desktop\Claude Code.lnk"
```

You get **Claude Code (1Remote).lnk** beside the original — same icon, same working
directory, same arguments, and the session shows up on your phone named after the
shortcut. The original is left alone; delete it or keep it as you prefer. Use
`--output <path>` to put the copy somewhere else.

Some shortcuts are refused, and each one says why:

| | |
| --- | --- |
| Store or packaged apps | They carry an app identity rather than a program to run, so there is nothing to start. Wrap the tool's own `.exe` or `.cmd`. |
| Shortcuts that run as administrator | The agent is per-user and unelevated, and an elevated session could not reach it. |
| Shortcuts that are already wrapped | Otherwise you get a session inside a session, and two entries on your phone for one terminal. |

Wrapping the same shortcut twice never overwrites the first result — you get
`(1Remote) (2)` — and wrapping a windowed program is allowed but warned about, because
its session will be an empty terminal.

## Command reference

| Command | What it does |
| --- | --- |
| `1remote <program> [args...]` | Run a program in a shareable session |
| `1remote agent` | Start the per-machine agent in the foreground |
| `1remote login` | Sign in |
| `1remote switch-account` | Forget the current account and sign in as a different one |
| `1remote logout` | Forget the cached sign-in |
| `1remote status` | Show who is signed in |
| `1remote install` | Start the agent now and at every logon, and put `1remote` on your `PATH` |
| `1remote uninstall` | Undo `install` |
| `1remote wrap-shortcut <path.lnk>` | Copy a desktop shortcut into one that shares its session |

| Option | |
| --- | --- |
| `--name <text>` | Friendly name for the session, shown on the phone. Defaults to the program name. |
| `--no-agent` | Run without the agent. The session is **not** shareable. |
| `--output <path>` | Where `wrap-shortcut` writes. Defaults to beside the original. |
| `--version`, `-h`/`--help` | As you would expect. |

| Environment variable | |
| --- | --- |
| `ONEREMOTE_HUB` | Point the agent at a different hub. Mainly for developers running one locally. |
| `ONEREMOTE_LOG_LEVEL` | `trace`, `debug`, `info`, `warn`, `error`. |
| `ONEREMOTE_LOG_DIR` | Where log files go. Defaults to `%LOCALAPPDATA%\1RemoteCLI\logs`. |

## Windows blocked the download

This one only applies if you took the **by hand** route above: downloaded the executable in a browser and ran it from Explorer. The one-line install does not go through it — `Invoke-WebRequest` does not attach a mark of the web to what it downloads, and SmartScreen only inspects files that carry one.

If you did download it by hand, SmartScreen will show **"Windows protected your PC"** the first time you run it. That is expected, and it is not a false positive in the interesting sense: the executable genuinely has no publisher identity attached to it, because there is no code-signing certificate for this project.

Verify it is the file that was published before you get past the warning:

```powershell
Get-FileHash .\1remote.exe -Algorithm SHA256
```

Compare that against the SHA-256 published with the release. If it matches, click **More info → Run anyway**.

If you would rather not trust a hash from the same place as the binary, build it yourself — `scripts\publish-agent.ps1` produces exactly what the release contains, and prints the hash.

## Windows blocked the install

Different problem, and up to version 0.08 it was ours rather than yours. The install script downloads, checks the hash, copies the file — and then fails on the last line:

```
Program '1remote.exe' failed to run: Access is denied
```

with a Windows Security popup at the same moment. Nothing is wrong with the download. On a machine managed by an organisation this is the attack surface reduction rule **"Use advanced protection against ransomware"**. Windows Security → **Protection history** is where to confirm it: the entry blames `powershell.exe` under *App or process blocked*, and names the executable it actually stopped further down, under *Affected items*.

**The installer now works around this on its own,** so you may never see it. When it cannot start the file, it hands the launch to Task Scheduler instead and carries on. That works because of what the rule is really judging — see below.

Two things were blamed for this before, and both were wrong in instructive ways.

Up to 0.08 the builds were published as a *compressed* single-file bundle, a self-extracting high-entropy blob and structurally what a packer looks like — exactly what a rule aimed at ransomware watches for. Turning compression off in 0.09 helped a great deal, and for a while it looked like the whole answer ([#101](https://github.com/eranyariv/1RemoteCLI/issues/101)). It was not. It took the block from near-certain down to intermittent.

What remains is **reputation**. The rule allows an executable that is signed by a trusted publisher, or that enough machines have already seen. Every new release of an unsigned program is neither, which is why this can return with each release even though nothing about the program changed. Measured on one machine: a build refused at 12:12 ran, untouched and with no exclusion applied, at 13:44. It is also why re-downloading does not help — a byte-identical copy of an installed, happily running agent is refused the moment it is written under a new name.

The other half of it is the **launcher**, not just the file. Measured on the same machine, on one file, ten seconds apart: PowerShell was refused, and Task Scheduler ran the same file to a clean exit with nothing logged against it. That is what the installer's workaround relies on, and it is why the agent itself — which starts from a logon task — runs perfectly well on machines that refuse to install it.

If you are stuck anyway, the executable is already in place and this finishes the half-done install:

```powershell
& "$env:LOCALAPPDATA\Programs\1RemoteCLI\1remote.exe" install
```

If the machine is yours, the remedy that actually applies is to allow the one file, from an **elevated** PowerShell:

```powershell
Add-MpPreference -AttackSurfaceReductionOnlyExclusions "$env:LOCALAPPDATA\Programs\1RemoteCLI\1remote.exe"
```

Otherwise, waiting works often enough, though "twenty minutes" was too optimistic — plan for an hour or two, and some machines never relent. `scripts\diagnose-launch.ps1` will tell you which protection is refusing it and how long it has been doing so. If none of that helps, an administrator has to allow it.

The real fix is to sign the builds, which is [#93](https://github.com/eranyariv/1RemoteCLI/issues/93). That is the same unsignedness behind the SmartScreen warning above, though the two are different problems and reach you by different routes. For a while we said signing would not have fixed this one; that was wrong, and the reputation measurements above are why. See [deployment](deployment.md#it-is-not-signed).

## Uninstalling

```powershell
1remote uninstall     # stop starting at logon, and come off your PATH
1remote logout        # forget the cached sign-in
```

Then delete `%LOCALAPPDATA%\Programs\1RemoteCLI` and `%LOCALAPPDATA%\1RemoteCLI`. `uninstall` deliberately leaves the executable where you put it; it does not delete files it did not create.
