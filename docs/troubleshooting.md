# Troubleshooting

Work down the list for your symptom. Most problems are one of three things: the agent is not running, the account is not on the allowlist, or the app is in a browser tab when it needs to be on the home screen.

## Where the logs are

**Agent** — `%LOCALAPPDATA%\1RemoteCLI\logs\agent-<date>.log`, one file per day.

```powershell
Get-Content "$env:LOCALAPPDATA\1RemoteCLI\logs\agent-$(Get-Date -f yyyy-MM-dd).log" -Tail 50 -Wait
```

Turn the volume up by restarting the agent with `ONEREMOTE_LOG_LEVEL=debug` (or `trace`). Details in [Logging](logging.md).

**Hub** — App Service log stream, and it is worth watching while reproducing:

```powershell
. .\scripts\az-env.ps1
az webapp log tail -g 1remotecli-rg -n 1remotecli-hub --only-show-errors
```

## The session says it is not shared

```
1remote: the agent is not running, so this session could not be shared.
```

The agent is a separate process from the wrapper, and the wrapper will not start it for you — silently running an unshared session you believed was shared is worse than saying so.

```powershell
1remote agent          # start it now, in this window
1remote install        # and have it start at every logon
```

If you already ran `1remote install` and it still is not running at logon, see [the agent does not start at logon](#the-agent-does-not-start-at-logon).

## My machine does not appear on the phone

In order:

**1. Is the agent running?** Look for the tray icon, or:

```powershell
Get-Process 1remote -ErrorAction SilentlyContinue
```

**2. Is the PC signed in?**

```powershell
1remote status
```

`Not signed in` or `The cached sign-in no longer works` → `1remote login`.

**3. Is the account on the hub's allowlist?** This is the most common cause and it does not look like an authorization problem from the phone — you sign in successfully and the machine list is just empty.

The hub says exactly who it refused and what to add. Watch the log stream while you reconnect:

```
Refused you@example.com (<key>): '<key>' is not on this hub's allowlist.
Add "<key>" to Entra:Allowlist to admit them.
```

Then follow [add someone to the allowlist](deployment.md#add-someone-to-the-allowlist). Note that **an empty allowlist denies everyone** — a hub with no configuration admits nobody, deliberately.

**4. Are the phone and the PC on the same account?** They must be. A machine only appears to the account that registered it; that partitioning is structural, not a filter.

**5. Is the hub up?**

```powershell
(Invoke-WebRequest 'https://1remotecli-hub.azurewebsites.net/health').Content
```

**6. Is something eating the WebSocket?** Corporate proxies and some VPNs allow HTTPS but break WebSocket upgrades. The agent log shows the connection attempt and the failure. Test from the same network with a phone on cellular to tell the two sides apart.

## `1remote` is not recognized

The `PATH` entry only reaches terminals opened *after* it was added, so the terminal you installed from will never see it. Open a new one.

If a new terminal still cannot find it, check the entry is there:

```powershell
[Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Select-String 1RemoteCLI
```

If it is missing, `1remote install` either was never run or could not write it — it reports that step like every other, so run it again from wherever the executable lives and read the output. Builds before the `PATH` step existed never added it at all; re-running a current build is the fix.

## The agent does not start at logon

Almost always because **the executable moved after `1remote install` ran**. The scheduled task holds the absolute path from that moment. Put it back, or re-run `1remote install` from wherever it lives now.

Check the task:

```powershell
Get-ScheduledTask -TaskName *1RemoteCLI* | Format-List TaskName, State
(Get-ScheduledTask -TaskName *1RemoteCLI*).Actions | Format-List Execute, Arguments
```

On a managed machine, policy sometimes refuses task registration outright. `1remote install` notices and falls back to a `Run` registry key, and its output says which one it used — it prints `ok` or `FAIL` per step and never stops at the first failure. If both failed, run `1remote agent` by hand, or from Startup.

## Sign-in problems

**The browser opened and nothing happened.** The sign-in completes on a loopback address, so a browser that opened on a different profile — or a redirect blocked by an extension — leaves the CLI waiting. Close it, run `1remote login` again, and complete it in the window that opens.

**It signed in as the wrong account, without asking.** Fixed, but worth recognising in older builds. Sign-in used to let the browser choose, so on a machine already signed in to a work account Entra returned a token for that account with no prompt at all — a sign-in that *succeeds*, prints an unexpected address, and only fails later when the hub refuses it. `1remote login` now asks which account every time. If a build still does this, clearing cookies for `login.microsoftonline.com` is the only other way out.

**I want to use a different account.** Double-click the tray icon to open **Settings…**, then **Sign out** and **Sign in**. Or `1remote switch-account` from a terminal, which does both in one step. Plain `1remote login` will not do it on its own: it succeeds silently on the account you already have, because its first move is to try the cached one. Signing out first is what forces the browser to ask.

The tray menu's first line, and the top of the settings window, always say which account is signed in. If it is not the one you expected, that is the answer to most of the questions on this page.

**`login` says the hub refused this account.** The token is genuine; the hub's allowlist does not contain it. Either sign in as a listed account, or add this one — the hub logs the exact key to add each time it turns somebody away:

```
Refused someone@example.com (<tid>:<oid>): '<tid>:<oid>' is not on this hub's allowlist.
Add "<tid>:<oid>" to Entra:Allowlist to admit them.
```

Add it with `Entra__Allowlist__<n>` in the App Service configuration. Note this is a *refusal*, not a failure to authenticate; a hub the CLI simply could not reach says so instead, and does not treat it as a sign-in problem.

**`AADSTS90023: Tokens issued for the 'Single-Page Application' client-type...`.** A redirect URI platform collision, not an account problem. Entra ignores the port when matching loopback redirects, and where a request could match either platform SPA wins — so if the agent and the PWA are on one registration, the PWA's `localhost` SPA entries capture the agent's redirect and the code comes back typed as single-page. They are therefore **two separate registrations**. If this returns, something added an SPA redirect to the agent app or a public client redirect to the API app. See [Why two registrations](azure-setup.md#why-two-registrations).

**`sign-in failed (...)`.** The code and message come straight from Entra. `AADSTS50020` generally means the account cannot use this application; `AADSTS65001` means consent was never granted. Both are [Azure setup](azure-setup.md) problems, not client ones.

**`AADSTS90204: A transient error has occurred`.** Usually is what it says — retry before doing anything else. It is worth one look at the app registration only if it repeats: the same code is also what Entra returns for a malformed authorize request, most often a duplicated scope. `1remote` requests exactly one scope and MSAL adds `openid`/`profile`/`offline_access` itself, so a genuine duplicate would have to come from a change to `AuthConfig.Scopes`.

**The phone says the redirect URI does not match**, or Entra answers `invalid_request: The provided value for the input parameter 'redirect_uri' is not valid`. The PWA signs in against whatever origin it was served from, so **every origin needs its own SPA redirect entry** — `https://1remotecli.yariv.org/` and `https://1remotecli-hub.azurewebsites.net/` are two origins, not one, and a trailing slash is part of the match. This is the failure mode of putting a custom domain in front of the hub and stopping there: the domain serves the app perfectly and only sign-in is broken. Check what is registered:

```powershell
(az ad app list --display-name "1RemoteCLI" -o json | ConvertFrom-Json).spa.redirectUris
```

See [Adding a custom domain later](azure-setup.md#adding-a-custom-domain-later).

**It worked yesterday and now it does not.** Tokens refresh silently, including mid-connection, so this usually means the cache is unreadable rather than expired. It is DPAPI-encrypted for your Windows user on this machine — restoring a profile, copying it to another PC, or a password reset that invalidates DPAPI will all break it. `1remote logout` then `1remote login`.

## Notifications never arrive

**On iPhone or iPad, the app must be on the home screen.** Apple does not deliver web push to a Safari tab. Worse, `Notification.requestPermission()` will happily return `granted` in a tab and then nothing is ever delivered — so "I allowed notifications" does not mean it is working. Add it to the home screen, open it from there, and enable notifications again. See [Using it from your phone](phone-setup.md).

Then, in order:

1. **Has the hub restarted since you last opened the app?** Push subscriptions are held in memory, so a redeploy, a configuration change or App Service platform maintenance silently drops all of them. Nothing warns you — the phone just goes quiet. **Open the app once and it re-registers.** If notifications stopped working for everybody at the same moment, this is why.
2. **iOS 16.4 or later.** No web push at all before that.
3. **Did you ever tap "Don't Allow"?** The page cannot ask a second time. iOS: Settings → Notifications → 1RemoteCLI. Chrome: site settings → Notifications → reset.
4. **Is the hub configured for push?**

   ```powershell
   (Invoke-WebRequest 'https://1remotecli-hub.azurewebsites.net/push/vapid').Content
   ```

   A 404 or an empty key means `Push__Vapid__*` is not set — see [VAPID keys](deployment.md#vapid-keys).
5. **Were the VAPID keys rotated?** Every existing subscription is dead. Open the app and re-enable notifications.
6. **Focus modes and Low Power Mode** delay or suppress delivery. This is the OS, and there is nothing the app can do about it.

Notifications fire when a session goes quiet at a prompt. A program that prints continuously while waiting will not trigger one, by design — otherwise anything with a spinner would notify constantly.

## The screen looks wrong

**Garbled or misplaced output.** The agent keeps its own model of the terminal, so this is a real emulator bug and worth reporting. Use the record button in the session toolbar to capture a trace, then stop it — the browser saves a `.trace.json`, which on a phone lands in Files. Attach that to the issue: it contains the exact byte stream, which is the only thing that makes such a bug reproducible.

**"Some output was missed".** Not a bug. The program produced more than the buffer holds while nobody was attached, so scrollback before that point is gone. What you are looking at is current.

**Wrong size, or the desktop window suddenly reflowed.** Attaching from a phone genuinely reshapes the terminal — the program is told it has a phone-sized screen, which is the only way its own line wrapping comes out right. So a program running in a wide window at your desk *will* visibly reflow when you attach. On detach the previous shape is handed back, so walking away does not leave a 45-column program stranded in a wide window.

## Reconnecting, forever

The banner clearing and coming straight back means the connection is establishing and then dropping. Check the hub is healthy and, if it was just deployed or reconfigured, wait — an App Service restart takes a few seconds and every client reconnects on its own.

If it never settles, check the agent log for the disconnect reason. A token that has become unacceptable is refused without being fatal, so the connection retries rather than dies; `1remote status` will show the underlying problem.

## Nothing here matches

There is a **Send feedback** link in both clients — the agent's settings window, and the footer of the phone app. Both open your mail client addressed to `eran@yariv.org` with the version already in the subject, which is the fastest way to say something and the one that does not need a GitHub account.

For anything that needs a back-and-forth, open an issue at https://github.com/eranyariv/1RemoteCLI/issues with:

- what you ran and what happened
- the tail of the agent log with `ONEREMOTE_LOG_LEVEL=debug`
- `1remote --version` and your Windows build (`winver`)
- for a rendering problem, a recorded trace
