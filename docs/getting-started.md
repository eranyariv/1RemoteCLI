# Getting started

Fifteen minutes, most of it waiting for a download. At the end you will be answering a prompt on your PC from your phone.

You need a Windows 10 2004 (build 19041) machine or newer — that is where ConPTY became reliable — and a phone. Nothing to install on the phone: the app is a web page you add to your home screen.

## Before you start

Someone has to have deployed a hub and added your Microsoft account to its allowlist. If that is also you, do [Deployment](deployment.md) first and come back. If it is not, ask them for two things:

- the **hub address** — the default is compiled in, so usually you need nothing here
- confirmation that **your account is on the allowlist**, because if it is not, sign-in will succeed and then the hub will refuse you, which is a confusing five minutes

## 1. Put the executable somewhere permanent

Download `1remote.exe` from the release and put it where it is going to live. Not Downloads.

```powershell
$dir = "$env:LOCALAPPDATA\Programs\1RemoteCLI"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Move-Item .\1remote.exe $dir -Force
```

**This matters more than it looks.** `1remote install` registers a scheduled task pointing at wherever the executable is at that moment. Move the file afterwards and the agent silently stops starting at logon, with no error anywhere — you just find out days later that your phone shows no machines.

Add it to your `PATH` so you can type `1remote` from anywhere:

```powershell
$dir  = "$env:LOCALAPPDATA\Programs\1RemoteCLI"
$path = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($path -notlike "*$dir*") {
    [Environment]::SetEnvironmentVariable('Path', "$path;$dir", 'User')
}
```

Open a new terminal for that to take effect, then check it:

```powershell
1remote --version
```

Windows will very likely stop you the first time — see [SmartScreen](#windows-blocked-the-download) below.

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

## 3. Start the agent

```powershell
1remote install
```

That registers the agent to start at every logon and adds a Start menu shortcut, then you are done with setup forever. Log out and back in, or just start it now:

```powershell
1remote agent
```

You will get a tray icon. That is the agent: one per machine, owning the connection to the hub and the list of live sessions. Sessions are *not* shared unless it is running — the wrapper will tell you so rather than quietly running unshared.

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

## Command reference

| Command | What it does |
| --- | --- |
| `1remote <program> [args...]` | Run a program in a shareable session |
| `1remote agent` | Start the per-machine agent in the foreground |
| `1remote login` | Sign in |
| `1remote logout` | Forget the cached sign-in |
| `1remote status` | Show who is signed in |
| `1remote install` | Start the agent at every logon |
| `1remote uninstall` | Stop starting the agent at logon |

| Option | |
| --- | --- |
| `--name <text>` | Friendly name for the session, shown on the phone. Defaults to the program name. |
| `--no-agent` | Run without the agent. The session is **not** shareable. |
| `--version`, `-h`/`--help` | As you would expect. |

| Environment variable | |
| --- | --- |
| `ONEREMOTE_HUB` | Point the agent at a different hub. Mainly for developers running one locally. |
| `ONEREMOTE_LOG_LEVEL` | `trace`, `debug`, `info`, `warn`, `error`. |
| `ONEREMOTE_LOG_DIR` | Where log files go. Defaults to `%LOCALAPPDATA%\1RemoteCLI\logs`. |

## Windows blocked the download

The executable is not code-signed, so SmartScreen will show **"Windows protected your PC"** the first time you run it. This is expected and it is not a false positive in the interesting sense: the binary genuinely has no publisher identity attached to it.

Verify it is the file that was published before you get past the warning:

```powershell
Get-FileHash .\1remote.exe -Algorithm SHA256
```

Compare that against the SHA-256 published with the release. If it matches, click **More info → Run anyway**.

If you would rather not trust a hash from the same place as the binary, build it yourself — `scripts\publish-agent.ps1` produces exactly what the release contains, and prints the hash.

## Uninstalling

```powershell
1remote uninstall     # stop starting at logon
1remote logout        # forget the cached sign-in
```

Then delete `%LOCALAPPDATA%\Programs\1RemoteCLI` and `%LOCALAPPDATA%\1RemoteCLI`. `uninstall` deliberately leaves the executable where you put it; it does not delete files it did not create.
