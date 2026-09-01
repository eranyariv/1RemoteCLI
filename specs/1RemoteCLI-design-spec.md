# 1RemoteCLI — Design Specification

| | |
| :--- | :--- |
| **Document version** | 2.0.0 |
| **Status** | Draft — approved for Phase 1 implementation |
| **Supersedes** | 1.0.0 (`1RemoteCLI - functional and technical spec.md`) |
| **Target platform** | Windows 10 / 11 (agent), Azure App Service or Container Apps (hub), iOS Safari / Android Chrome (client) |

---

## 1. Executive summary

Modern development increasingly runs through long-lived, interactive terminal agents — Claude Code, GitHub Copilot CLI, PowerShell automation. These processes routinely pause for minutes or hours waiting for a human to answer a question: *"Allow file edit to src/app.ts? (y/n)"*. If you are not sitting at the machine, the work simply stops.

**1RemoteCLI** is a mobile "steering wheel" for terminal sessions already running on your Windows machines. You start work at your desk as normal; when you walk away, your phone can attach to that same live session, see exactly what is on screen, answer prompts, type commands, and send `Ctrl+C`.

### 1.1 Design principles

1. **Attach, don't spawn.** A phone can attach to a wrapped terminal or continue a recent agent conversation that already exists in the selected local ACP provider. It cannot create a new process or a new agent conversation.
2. **Use the provider's state model.** A terminal's meaningful state is its visible screen, so the agent sends a VT snapshot rather than replaying bytes. An ACP conversation's state is its typed transcript, so the agent sends replaceable transcript events rather than flattening it into terminal text.
3. **Boring infrastructure.** One process per machine, one hub instance, no database, no message broker. The system should be comprehensible in an afternoon.
4. **The user is the security boundary.** One Microsoft identity owns machines, sessions, and clients. Cross-user access is structurally impossible, not merely checked.

### 1.2 Scope

**In scope for v1:** attaching to running terminal sessions from a phone, continuing recent GitHub Copilot or Claude Code conversations through ACP, full interactive control, pasting mobile clipboard text, bounded phone-to-terminal file and photo attachments, bounded phone-to-chat attachments sent as ACP prompt content, reconnection across network changes, push notification when a session needs attention, multiple machines per user.

**Out of scope for v1:** launching processes remotely, general file browsing or machine-to-phone transfer, scrollback history on mobile, sharing a machine with another person, session persistence across a closed desk terminal, end-to-end encryption.

### 1.3 Intended audience and scale

A small trusted group — the author and a handful of colleagues. The system is designed for roughly **10 machines, 20 concurrent sessions, and 5 users**. Section 9 records where this design would need to change to go beyond that.

---

## 2. System architecture

Three components and a wrapper, joined by one identity provider.

```
                  ┌──────────────────────────────────────────┐
                  │        Microsoft Identity Platform       │
                  │      Entra ID + personal MSA ("common")  │
                  └───────────────────┬──────────────────────┘
                                      │  OAuth 2.0 / OIDC — JWT issuance
                  ┌───────────────────┴──────────────────────┐
                  ▼                                          ▼
      ┌───────────────────────┐                  ┌───────────────────────┐
      │   Mobile PWA client   │                  │  Tray agent           │
      │   React + xterm.js    │                  │  1remote.exe agent    │
      └───────────┬───────────┘                  └───────────┬───────────┘
                  │                                          │
                  │ WSS + Bearer JWT                         │ WSS + Bearer JWT
                  │                                          │
                  └───────────►  ┌──────────────┐  ◄─────────┘
                                 │  Relay hub   │
                                 │  ASP.NET 8   │
                                 │  + SignalR   │
                                 └──────────────┘
```

On each Windows machine:

```
┌──────────────────────────── Windows machine (interactive user session) ────┐
│                                                                            │
│   Desk terminal (Windows Terminal)                                         │
│   ┌──────────────────────────────┐                                         │
│   │  > 1remote claude            │   wrapper process, one per session      │
│   │                              │                                         │
│   │  ┌────────────────────────┐  │   owns the ConPTY, tees the byte stream │
│   │  │ ConPTY ── claude.exe   │  │   to the local console and to the agent │
│   │  └────────────────────────┘  │                                         │
│   └───────────────┬──────────────┘                                         │
│                   │ named pipe (ACL: current user SID only)                │
│                   ▼                                                        │
│   ┌────────────────────────────────────────────────┐                       │
│   │  Tray agent — one per machine, hidden          │                       │
│   │   • MSAL token cache (DPAPI, CurrentUser)      │                       │
│   │   • machine identity + session registry        │                       │
│   │   • headless VT emulator, one per session      │──── WSS ──► Relay hub │
│   │   • idle/prompt detection                      │                       │
│   │   • ACP chat discovery + typed transcripts     │                       │
│   └────────────────────────────────────────────────┘                       │
└────────────────────────────────────────────────────────────────────────────┘
```

### 2.1 Why a tray EXE and not a Windows Service

A Windows Service runs in session 0 as `LocalSystem`. Child processes would therefore have no access to the user's `%USERPROFILE%`, git credentials, SSH keys, or the per-user logins that `claude.exe` and `copilot.exe` depend on — and a DPAPI blob written under `CurrentUser` scope during an interactive login could not be decrypted by `LocalSystem` at boot.

The agent is therefore an ordinary Win32 executable running as the interactive user, started at logon by a Scheduled Task, with no console window and a system tray icon. It inherits the user's full environment for free. The cost is that a machine is only reachable while that user is logged on; this is acceptable and is documented as a known limitation (§9).

### 2.2 Why attach-only

Remote spawn means arbitrary executables, arbitrary arguments, and an arbitrary working directory, chosen from a phone. Whoever phishes the Microsoft account owns every paired machine. Containing that requires an allowlist, per-machine consent, audit logging, and a second factor — significant machinery guarding a capability that the primary use case does not need. The motivating scenario is *"I started Claude Code and then walked away"*, which attach-only serves completely.

**Constraint:** Windows cannot retroactively attach a pseudoconsole to a process that is already running under a different console. A session must be *born* under the wrapper to be attachable; an existing Windows Terminal tab cannot be adopted.

ACP conversations are the deliberate exception to the ConPTY constraint. The selected provider owns their persisted history and exposes it through `session/list` and `session/load`; the tray agent starts the provider's local stdio server and can therefore load and continue an existing conversation without adopting its desktop window.

### 2.3 Component and binary inventory

| Component | Project | Produces | Runs |
| :--- | :--- | :--- | :--- |
| Tray agent | `src/Daemon` | `1remote.exe agent` | Once per machine, at logon |
| Wrapper CLI | `src/Daemon` | `1remote.exe <program> [args]` | Once per session, in the desk terminal |
| Login command | `src/Daemon` | `1remote.exe login` | Once, at setup |
| Relay hub | `src/Hub` | `1RemoteCLI.Hub` container | One instance in Azure |
| Mobile client | `src/PWA` | Static SPA bundle | In the browser |

The Windows side is a **single binary** with subcommands, so there is one thing to install, one version number, and one update path.

---

## 3. Identity and authorization

### 3.1 Azure app registration

| Setting | Value |
| :--- | :--- |
| Supported account types | Any Entra ID tenant + personal Microsoft accounts (endpoint `common`) |
| Exposed API scope | `api://<application-id>/Session.Access` |
| PWA redirect URI | `https://<hub-host>/` — SPA platform, authorization code + PKCE |
| Agent redirect URI | `http://localhost/` — public client, loopback with a dynamically chosen port |
| Agent client type | Public client, no secret |

`<application-id>` is the GUID assigned by Azure; it is not a literal string to be copied. The PWA and the agent share one registration, so both sides of a session are provably the same identity.

### 3.2 Token acquisition

**PWA.** `@azure/msal-browser` via `@azure/msal-react`, authorization code flow with PKCE. Tokens are held in memory with a session-storage fallback; refresh happens silently via a hidden iframe, falling back to interactive login.

**Agent.** `1remote login` starts a loopback HTTP listener on an ephemeral port, opens the default browser to the `common` authorize endpoint, and completes the code exchange through MSAL.NET. Loopback redirect is preferred over device code flow: the user is physically at the machine, so there is no reason for the more awkward flow, and device code flow is blocked by Conditional Access in many tenants.

**Token cache.** MSAL.NET's cache is serialized to `%LOCALAPPDATA%\1RemoteCLI\msal.cache`, encrypted with `ProtectedData` under `DataProtectionScope.CurrentUser`. The agent never handles raw refresh tokens; it asks MSAL for an access token and MSAL manages renewal. Because the agent runs as the interactive user, `CurrentUser` scope decrypts correctly at logon.

### 3.3 Token validation at the hub

Every connection presents a bearer access token, validated for:

| Claim | Requirement |
| :--- | :--- |
| Signature | Against the OIDC discovery keys for the token's issuing tenant |
| `iss` | `https://login.microsoftonline.com/{tid}/v2.0`, where `{tid}` matches the token's own `tid`. A `common` app must use a dynamic issuer validator; a static issuer string is wrong and will either reject everyone or accept the wrong tenant. |
| `aud` | The application's own client ID or `api://<application-id>` |
| `scp` | Must contain `Session.Access` |
| `exp` / `nbf` | Within a 60-second clock-skew allowance |
| `tid` + `oid` | Both present; together they form the user key |

### 3.4 The user key

```
UserKey = "{tid}:{oid}"
```

`oid` alone is insufficient for a multi-tenant application — Microsoft's guidance is explicitly the **`tid` + `oid` tuple**. The `sub` claim is deliberately not used: it is pairwise per application *and* per tenant, which makes it unstable for this purpose.

The hub's entire routing registry is partitioned by `UserKey`. A hub method cannot address anything outside the caller's partition because the partition is selected from the *connection's validated principal*, never from a parameter in the request. Cross-user access is therefore a structural impossibility rather than a check that could be forgotten.

### 3.5 Account allowlist

Because the app accepts the `common` endpoint, anyone in the world with a Microsoft account can obtain a structurally valid token. The hub therefore holds an allowlist in configuration:

```jsonc
{
  "Access": {
    "AllowedUsers": [
      // Preferred: immutable tid:oid. The comment records who it is.
      { "userKey": "9188040d-6c67-4c5b-b112-36a304b66dad:8a4f9b21-...", "note": "erany" },
      // Also accepted: matched against the verified preferred_username claim.
      { "email": "colleague@example.com", "note": "pending oid capture" }
    ]
  }
}
```

A connection whose `UserKey` and verified `preferred_username` both fail to match any entry is rejected at handshake. `tid:oid` entries are preferred because email addresses are mutable; the `email` form exists so a new colleague can be onboarded before their `oid` is known, and the hub logs the resolved `UserKey` on first successful connection so the entry can be tightened.

### 3.6 Mid-connection token expiry

SignalR authenticates at the handshake and **does not re-authenticate afterwards**. Left alone, a WebSocket outlives its access token indefinitely — the single most commonly missed authorization gap in SignalR designs. For a phone left attached overnight that is a connection whose authorization was last checked a day ago; revoking someone's access, or removing them from the allowlist, would have no effect on the connection they already hold. Closing that gap is what this section is for.

The hub records each connection's token `exp` at admission and owns its lifetime from there:

1. Sends `TokenExpiring` to the holder **5 minutes** before expiry — comfortably longer than a token acquisition takes even when it has to reach Entra over a bad link.
2. The holder acquires a fresh token and invokes `RefreshToken(token)`.
3. The hub **re-validates the token in full** — signature, issuer, audience, required scope and the allowlist — and additionally asserts that the new token's `UserKey` is **identical** to the connection's existing one.
4. If no valid token has been presented by `exp`, the hub aborts the connection.

Both the PWA and the agent implement this. Clients also supply an `accessTokenFactory` so that automatic reconnects (§4.6) carry a fresh token.

Four decisions are worth stating, because each has a plausible-looking alternative:

**Re-validation is the full check, not a signature check.** Anything less would be a way to launder a weak token onto a connection that was opened with a strong one. The refresh path deliberately reuses the bearer handler's own validation parameters and refetches signing keys from the same configuration manager, so a key rollover at the identity provider does not turn every refresh in flight into a disconnection.

**A refused token is not fatal; a changed identity is.** These are treated differently on purpose:

- *Refused* (expired, malformed, wrong audience, no longer on the allowlist) — the hub refuses the refresh and says why, and the connection **survives**. Killing it here would destroy the one channel over which the holder could learn what went wrong, and would bring forward a disconnection that the token's own expiry was already going to cause anyway. The deadline is unchanged: refuse to refresh, and the connection ends at its original `exp`.
- *Different identity* — the connection is **aborted**. A connection carries attachments, and quietly walking it from one account to another would hand whatever it is watching to somebody who was never granted it. There is no state on such a connection that is safe to keep, including the channel that would have carried the explanation. The holder sees a disconnection, and reconnection re-admits them as whoever they now are.

This asymmetry has a mechanical consequence worth knowing: aborting a connection inside a hub method cancels the in-flight invocation before its return value can be flushed, so an aborting failure can never also be a reported one.

**Expiry is enforced by a sweep, not a timer per connection.** A pass every **30 seconds** over the tracked connections: the work is a dictionary walk, the resolution the problem needs is minutes, and a timer per connection is a leak waiting for the one disposal path somebody forgets. The 30-second interval also guarantees the 5-minute warning arrives with at least 4.5 minutes to spare.

**Expiry is enforced with the same 60-second clock skew the handshake allows.** Being stricter at the sweep than at admission would let the hub accept a token and then immediately kill the connection it had just accepted — which presents to the user as a connection that drops for no reason, on a machine whose clock is merely a little off.

Finally, a token whose `exp` the hub cannot read is **not tracked at all** rather than treated as expiring now. Admission has already decided the token is genuine; disconnecting somebody over a claim the hub failed to parse would turn a hub-side misunderstanding into their outage.

---

## 4. Component design

### 4.1 Wrapper CLI (`1remote <program> [args]`)

The wrapper is what makes a session shareable. It is deliberately thin.

```
1remote claude
1remote pwsh
1remote --name "nightly build" pwsh -NoLogo -File .\build.ps1
```

**Startup**

1. Connect to the agent's named pipe. If the agent is not running, print a clear error and exit — the wrapper never silently degrades into a plain passthrough, because the user would believe the session is shareable when it is not.
2. Create a pseudoconsole with `CreatePseudoConsole`, sized to the current console window.
3. Launch the child process attached to the PTY via `CreateProcess` with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.
4. Put the local console into raw / VT-passthrough mode: enable `ENABLE_VIRTUAL_TERMINAL_PROCESSING` on the output handle and disable line input and echo on the input handle. Save the prior mode and restore it on exit.
5. Send `SessionOpened` to the agent and receive an assigned `sessionId`.

**Steady state** — the wrapper is a tee in both directions:

```
   PTY output ──┬──► local console  (so the desk experience is unchanged)
                └──► named pipe ──► agent

   local stdin ─────► PTY input
   agent (phone) ───► PTY input
```

Input from the phone and input from the keyboard are written to the same PTY handle under a lock, so a byte from one never interleaves inside a multi-byte sequence from the other.

**Shutdown.** When the child exits, the wrapper reports the exit code, closes the pseudoconsole, restores the console mode, sends `SessionClosed`, and exits with the child's exit code — so it composes correctly in scripts.

**Reconnecting after the agent restarts** (issue #174). Once `SessionOpened` has been accepted, losing the pipe — the ordinary shape of an agent update replacing the running process — does not end the session. The wrapper's `AgentPipeClient` dials the same pipe name again, retrying indefinitely with a growing delay, because giving up would strand a session that is otherwise perfectly fine purely because the agent took a while to come back. It re-sends `SessionOpened` carrying the id it was given before, so the phone's open tab and this machine's own session list keep pointing at the same session; the fresh agent's registry is always empty right after a restart, so the id is free and it hands the same one straight back. Before re-opening, it also drops anything still queued for the dead connection and sends one `Output` frame carrying a VT reset plus the screen exactly as it stands, reconstructed from a local mirror fed by every byte the wrapper has sent — so the new agent, and the phone through it, see the current picture rather than a blank one. Local desk output and remote input from the phone are unaffected throughout: they never depend on whether the agent link happens to be up at that instant. This all requires a session to have existed in the first place; a pipe lost before the first `SessionOpened` is accepted, or on the one-shot connection a shortcut launcher uses to create an ACP chat, is still treated as fatal, exactly as before.

**Migration safety.** The very first release with this feature has already-running wrappers on disk that predate it and cannot reconnect. `SessionOpened` carries a `supportsReconnect` flag that only a wrapper built with this feature sets; an older wrapper omits it, which decodes as `false`. §4.2 uses this to decide which live terminal sessions still make a restart unsafe.

The wrapper does **not** parse VT sequences, hold a screen model beyond the mirror that exists solely to reseed a reconnect, or talk to the network otherwise. All remote-facing logic lives in the agent, in one place.

### 4.2 Tray agent (`1remote agent`)

One per machine.

**Machine identity.** On first run the agent generates a GUID and persists it to `%LOCALAPPDATA%\1RemoteCLI\machine.json` along with a friendly display name (defaulting to the computer name, user-editable). The GUID — not the computer name — is the `machineId`. Computer names are neither unique nor unforgeable.

**Named pipe server.** `\\.\pipe\1remotecli-agent-{user-sid}`, with a security descriptor granting access **only to the current user's SID**. Without this ACL any local process, including one running as a different user on a shared machine, could inject keystrokes into a live session. The SID is embedded in the pipe name so that two users logged on to the same machine each get their own agent and their own pipe.

**Tray icon.** The agent's only face, and for most users the entire product on the desktop side. It shows exactly three states, because the only question a user has is whether their phone can see this machine, and there are only three answers:

| State | Icon | Meaning |
| --- | --- | --- |
| Connected | plain green mark | Reachable. Tooltip gives the session count. |
| Reconnecting | orange disc with a bang, over the mark | Trying. Tooltip says local sessions keep working regardless. |
| Signed out | barred circle over the mark | Only the user can fix it. *Sign in* is enabled. |

Distinguishable by shape as well as colour: colour alone fails for red/green colour blindness and at 16 px against a dark taskbar. Connected is deliberately undecorated — it is the state the tray is in almost all the time, and leaving it bare is what makes a decorated tray mean "look at me".

**Session count.** The icon also carries the number of live sessions, so the answer to "is anything running on that machine" needs neither a hover nor a click. Nothing is shown for zero — a permanent "0" says exactly what a bare icon already says, and spends the annotation's one asset, that it means something when it is there. One through nine show the number; ten and above show `>9`, because two digits at 16 px is mush and nobody with ten sessions is reading a tray icon to learn whether it is eleven.

**Both cues are shipped artwork, not drawn at runtime.** `assets/tray` holds a drawn 512 px variant for every combination of state and count — thirty-three of them — and `scripts/make-icons.ps1` renders each into its own `.ico` at 16, 20, 24, 32, 40 and 48, which the daemon embeds and selects. A digit or a badge composited into a corner at 16 px is a smudge whatever care goes into it; picking a whole prepared image is what makes both legible. So `TrayArtwork` chooses a file and scales it, and does no compositing at all.

Within a state the counted variants share one frame, so the mark holds still as the number ticks over; the plain mark keeps a tighter one, because an idle tray is the common case and should get all sixteen pixels. The count sits in a white disc so it survives a dark taskbar as well as a light one, and the state badge takes the opposite corner from the count so neither crowds the other.

**Detail scales with size.** The masters are the full lockup — the numeral, the count plate, and `CLI` beneath — and that is what the 32, 40 and 48 frames carry. The 16, 20 and 24 frames are cropped to the numeral and the plate. This is the same bargain Windows' own icons make, and it is forced rather than chosen: fitting the wordmark in roughly doubles the height of the artwork, so everything above it loses about 45% of its pixels, and at 16 px the count digit stops being a digit. Between a wordmark nobody can read and a count nobody can read, the count is the one the icon exists to show. The tray asks for the small sizes at ordinary scalings, so in the notification area the wordmark appears only at 200%.

Missing artwork degrades one axis at a time rather than to nothing — the count is dropped first, then the state, leaving the plain mark. Losing the number is a shame and losing the badge is worse, but the icon is the only thing telling the user whether their machine is reachable, and that is not something a build slip should be able to take away.

The count is independent of the connection state and is shown in all three: sessions keep running while the hub is unreachable, so the number means the same thing however the connection is doing. It is read from the session registry — the same source as the tooltip and the settings window, so the three can never disagree — and the shell is only asked to repaint when the icon actually changes, so a busy session does not become a repaint per output frame.

The tooltip is the whole diagnostic surface for someone whose phone has stopped seeing a machine, so it leads with the state and the machine name and is truncated to the 127 characters Windows will show — beyond that Windows drops the tooltip entirely rather than truncating it.

Menu: the account line, *Settings…* (the default action), *Open the web app*, *Update to x.yy* when there is one, *Quit*. A single left click, keyboard activation, and right click all open this same menu; with notification-area version 4 behavior the first two arrive as `NIN_SELECT` and `NIN_KEYSELECT`, not raw mouse messages. A double left click opens or focuses Settings. The single-left-click menu is delayed by Windows' configured double-click interval and cancelled when the double click arrives; opening it immediately would block the tray thread in `TrackPopupMenuEx` before the shell could deliver the second click. Keyboard and right-click menus remain immediate. Deliberately short — everything else lives in the settings window, so there is only ever one place that answers "am I signed in".

The native hover tooltip is capped by Windows at 127 characters and does not support rich text or per-line bold styling. Its first line therefore identifies `1RemoteCLI Agent` and the current `x.yy` version in plain text, followed by connection/machine and session/action details. Product and state come before arbitrary machine text so truncation cannot remove the facts needed to diagnose the icon.

The icon runs its own STA thread with its own message pump. The agent's main thread is busy awaiting the pipe server, and the tray is optional decoration: a machine with no interactive desktop, a policy that blocks shell integration or a broken shell must not stop the agent relaying. Failure to create it is logged and ignored.

**Settings window.** A raw Win32 dialog created on the tray thread — no Windows Forms and no WPF, because the Windows Desktop runtime pack was removed from the build and adding it back doubles the download for one dialog. Controls are `CreateWindowExW` children of a registered window class, laid out at fixed offsets scaled by `GetDpiForWindow`, and given a `CreateFontW` "Segoe UI" via `WM_SETFONT` — without which every control draws in the 1990s bitmap system font.

It shows the signed-in account and hub connection in words; live sessions with their program, age and whether each is waiting for input; settings for starting at sign-in, session visibility, automatic updates, and the phone-notification level for this machine; *Wrap a desktop shortcut…*; the version with *Open logs* and *Send feedback…*; and, only when there is something to say about it, a line about updates with an *Update now* button.

The update row is laid out **below** the version line and hidden by default, so appearing and disappearing costs a window resize rather than a `MoveWindow` on every control beneath it. The window is silent about updates until there is something to say: a permanent "no updates available" is one more line to read in a window somebody opened because something else was wrong.

Three decisions are load-bearing:

- The window is created on the **tray thread**, so `TrayCommand.Settings` is dispatched by posting a private `WM_APP` message back to that thread rather than by `Task.Run` as every other command is. A window belongs to the thread that created it.
- `IsDialogMessage` runs in the tray pump before `TranslateMessage`, which is the whole of the dialog's keyboard behaviour: Tab, Space, Escape and Enter.
- A one-second `WM_TIMER` refreshes the connection line and the session list, but **not** the checkbox — reading the real autostart state spawns `schtasks.exe`, so it is read on open and after a toggle only. The checkbox reflects reality rather than what the installer once did, because the failure it exists to explain is somebody having turned the agent off in Task Manager's Startup tab.

The session list is only refilled when its lines actually change, and the scroll position is preserved across refreshes, so a list being read cannot jump under the reader once a second.

**Wrapping a desktop shortcut.** Users who start a CLI from a `.lnk` on the desktop have no command line to put `1remote` in front of. The settings flow reads the shortcut through `IShellLink`/`IPersistFile`, detects its CLI from the complete target and argument string, then shows that answer in a native radio-button dialog. The detected type is preselected, including *Generic* when no match was found, but nothing is written until the user confirms or overrides it. The command-line equivalent requires `--type`, so automation makes the same decision explicitly.

Claude Code, PowerShell, Command Prompt and Generic choices write a sibling — `Claude Code (1Remote).lnk` — that targets the agent with `--name "Claude Code" --type claude-code -- "<original target>" <original args>`, carrying the working directory and icon across. The explicit type travels in the wrapper handshake and becomes the session's initial `cliType`; detection is only the fallback for hand-written wrapper commands.

A GitHub Copilot CLI choice writes an ACP launcher instead. Double-clicking it sends a one-shot `ChatCreate` request to the running agent. The agent owns the already-initialized `copilot --acp --stdio` process, calls `session/new` with the shortcut's working directory, publishes the resulting structured chat to the hub, and returns its machine and session IDs so the launcher can deep-link directly into it. Starting a raw ACP server from the shortcut would leave it waiting on stdio with no client, so that is deliberately not the generated command. The original shortcut is never modified.

The reads are the awkward part: `GetPath` is asked for `SLGP_RAWPATH` and the result expanded by hand, because a shortcut may store `%LOCALAPPDATA%\...` and the shell's own expansion is not guaranteed; and the buffer is cleared before each getter because several return `S_FALSE` and write nothing at all, leaving the previous string in place.

Four cases are refused rather than half-wrapped, each with its own sentence, because "it didn't work" is the one message that cannot be acted on:

| Refusal | Why |
| --- | --- |
| No filesystem target | Store/MSIX shortcuts carry an app identity, not a program. There is nothing to hand to a ConPTY. |
| Already wrapped | A session inside a session, and two entries on the phone for one terminal. |
| `SLDF_RUNAS_USER` | The agent is per-user and unelevated; an elevated wrapper could not reach its pipe. |
| Name collision | Never overwritten and never `X (1Remote) (1Remote)` — numbered instead. |

A windowed program is a warning, not a refusal: the subsystem is read out of the PE header (`e_lfanew`, `PE\0\0`, subsystem `2` = GUI) and the user is told the session will be an empty terminal, then allowed to proceed.

All of this COM is `[GeneratedComInterface]` with activation by hand through `CoCreateInstance`, because built-in COM interop is disabled for trimming; `1remote self-check` therefore round-trips a real shortcut and instantiates the file dialog, since that is exactly the kind of breakage a trimmed publish produces and no unit test would catch.

The binary is a **console** subsystem executable, because the same binary is the wrapper and a windows-subsystem process has no stdout to write a terminal to. "No console window" for the agent therefore comes from the scheduled task's `Hidden` setting, not from the subsystem.

**Autostart (`1remote install`).** A Scheduled Task, not a Windows service — this is the load-bearing decision of the product. The agent must run *as the interactive user, in their session*, with their environment and their token cache; a service runs in session 0 and can neither see nor be seen by the console the user types at.

The task is registered from XML (`schtasks /Create /XML`) rather than from flags, because several required settings have no flag at all. The XML file must be **UTF-16 with a BOM**; `schtasks` rejects UTF-8 with "The task XML is malformed", which points at the XML rather than at the encoding.

The settings that matter, each of which is a default that silently kills a long-running agent hours after a successful install with nothing in any log:

| Setting | Value | Without it |
| --- | --- | --- |
| `LogonType` | `InteractiveToken` | Runs in session 0; cannot see the user's terminals. |
| `StopIfGoingOnBatteries` | `false` | Unplugging the laptop stops the agent. |
| `ExecutionTimeLimit` | `PT0S` | The agent vanishes after three days. |
| `StopOnIdleEnd` / `RunOnlyIfIdle` | `false` | Walking away stops it. |
| `MultipleInstancesPolicy` | `IgnoreNew` | A second agent fights the first for the same pipe. |
| `LogonTrigger/UserId` | this user | Fires for every account that logs on. |
| `Hidden` | `true` | A console window flashes up at every logon. |
| `RunLevel` | `LeastPrivilege` | Prompts at install and puts the agent on a different integrity level from the terminals it wraps. |

The executable path comes from the *process*, not the assembly location: a single-file publish unpacks the managed assembly to a temp directory, and a task pointing there works once and is then gone.

An `HKCU\...\Run` value is the fallback when task registration is refused, which happens by policy on some managed machines. The installer registers **exactly one** of the two, never both — together they race at logon and the loser exits with "an agent is already running", which reads like a bug.

`1remote install` also adds Start menu shortcuts (*Sign in*, *Start agent*), reports every step individually, continues past a failure rather than leaving a machine half-installed silently, and exits non-zero if any step failed. `1remote uninstall` reverses all three and is safe to run on a machine that was never installed.

Its last step **starts the agent**, because a logon trigger on its own means the install produces nothing the user can see until the next logon — no tray icon, no relay, and a phone that lists no machines. It goes through the registered task rather than launching the executable, so what runs now is exactly what will run at every logon; it falls back to starting the process directly where policy registered only the `Run` key, and does nothing at all when an agent is already serving this user. That last case is the common one on an upgrade, and the check is the agent's named pipe rather than the process list, because every wrapped session is also a process called `1remote`.

**Responsibilities.** Session registry, one headless VT emulator per session, hub connection and authentication, output framing and flow control, idle/prompt detection, and routing input from the hub to the correct wrapper pipe.

#### 4.2.1 Self-update

Until this existed, nothing on a machine knew a release had happened. Every fix in 0.09 through 0.12 was for something that stopped the agent working, and each reached a user only if that user happened to re-run the install script — which is to say, only if they were already suffering the problem the fix was for.

**Checking and installing are automatic by default.** Two minutes after start and then every 24 hours, the agent asks which stable release is current and applies a newer one through the verified sequence below. Not at second zero: logon is the busiest moment a machine has and the network is frequently not up yet, so a check at start mostly measures how long wifi took, and its failure would be the first thing the settings window said about a machine that is working perfectly. Opening the settings window also cuts the current wait short, so someone who has come to look at the agent gets an answer that is current rather than one from yesterday.

The Settings tab carries an enabled-by-default *Automatically update 1RemoteCLI* checkbox in `agent-preferences.json`. Turning it off stops automatic installation immediately but leaves periodic discovery, *Check for updates*, and *Update now* available as recovery and diagnostic paths. The deployment-level `settings.json` option `"update": { "check": bool, "intervalHours": number }` and `ONEREMOTE_UPDATE_CHECK=0` remain the separate ways to turn checking off for one installation or one run.

A failed automatic check or install does not stop the agent or require acknowledgement. It is recorded in the update status and file log, then retried after an exponential delay starting at five minutes and capped at four hours. A successful check resets the delay to the ordinary 24-hour interval.

**The website, not the API.** `https://github.com/{repo}/releases/latest` is fetched with redirects disabled and the tag read out of the `Location` header. The API is limited to sixty anonymous calls an hour *per address, counted across everyone behind it* — issue #102 is that allowance being exhausted by strangers on an office network — and every agent on that network checking daily would make it routine. Following a redirect needs no API and has no allowance. A repository with no releases redirects nowhere, leaving a last segment of `latest`, which is not a tag and is read as "no release".

**Versions are compared numerically.** The display form is `x.yy` and the minor part is a number: as strings `0.9` sorts *after* `0.10`, so a machine on 0.9 would consider itself ahead of every release for the next ninety. Anything that will not parse is never offered — treating "could not read it" as "probably newer" would have the agent install whatever a mangled tag pointed at. Strictly newer only, so anyone running a build from source ahead of the tag is not quietly moved backwards.

**The install sequence** is the one `scripts/install.ps1` performs, in the same order and for the same reasons, with one step added. Each refusal below is a case where doing nothing is the better answer, because unlike the installer this runs unattended, and a bad outcome is not "try again" but "the tray icon is gone and the phone cannot see this machine":

| Step | Refusal |
| --- | --- |
| Fetch `SHA256SUMS.txt` — a few hundred bytes, before the 30 MB, so an unverifiable release costs nothing on a tethered connection | No entry for this asset, or a first field that is not 64 hex characters. A download URL GitHub cannot resolve is answered with an HTML page and a **200**, which lands on disk looking like a file; refusing anything that is not a hash is what stops that page being treated as an answer |
| Compare the installed file's hash to the published one | Equal → **nothing is written at all**. Windows judges an executable as it is written and its verdict is not stable between two writes of identical bytes (issue #108), so a pointless copy is a real risk of breaking a working install |
| Download the asset and hash it | Mismatch → nothing is installed |
| **Run the staged build with `--version`** | Will not start, exits non-zero, or reports a version that is not the tag → the old build stays. This is the step the installer does not have, and it is what makes an unattended update defensible: issues #92, #93 and #101 are all "the executable arrived and then would not start", which is precisely what a machine nobody is sitting at cannot recover from |
| Swap it in | Failure → the previous file goes straight back |

**Replacing a running image.** Windows refuses to delete or overwrite a running executable but will happily *rename* one, and a process keeps running from the file it started from whatever that file is now called. So the installed executable is renamed to `1remote.exe.old` and the new one copied into its place; the retired copy is deleted afterwards on a best-effort basis and swept up by a later update. When `.old` is itself still held — an agent that updated while sessions were open goes on running from it — a numbered name is used instead, so a second update succeeds rather than failing on the leavings of the first. This rename is also the whole of the rollback, which is why it is a rename and not a delete.

**The agent will not restart itself under work a restart would strand.** This is the rule the design turns on, and issue #174 narrowed what counts: an ACP turn always blocks, because there is no wrapper underneath it to reconnect. A terminal wrapper built before it could reconnect (`TerminalSession.SupportsReconnect` false) also blocks, exactly as every wrapper once did — its session keeps running at the desk but is never shareable again, and nothing tells the person holding the phone, if the agent restarted under it. A wrapper that can reconnect does not block at all: it rides out the restart on its own (§4.1), so counting it here would leave a machine that always has one terminal open never reaching an update it already downloaded. `Program.UpdateBlockerCount` is `chats.ActiveTurns` plus the terminal sessions that do not advertise reconnect support. So activity is read **after** the install rather than before — downloading and verifying takes long enough for somebody to have started work meanwhile — and when the blocker count is not zero the window says the update is installed and waiting. Terminal and ACP activity changes notify the updater; the final completion requests exactly one restart. A restart also happens only after the pipe server is disposed, because the replacement process would otherwise exit with "an agent is already running".

`1remote update` is the same sequence from a command line, for a machine with no interactive desktop. It never restarts anything.

**Everything the update path says goes to the file log** (`Update`, event 1500), not the console. The agent normally runs hidden from a scheduled task, where nothing is reading stderr — and "why has this machine never updated" is a question that can only be answered afterwards, from `%LOCALAPPDATA%\1RemoteCLI\logs`.

### 4.3 Headless VT emulator

This is the technical heart of the system, and it replaces the "5,000-line ring buffer" of the previous design.

**Why a line buffer is the wrong model.** Claude Code, Copilot CLI, and every full-screen TUI are cursor-addressed. They do not emit lines; they emit *redraws* — "move to row 4 column 12, erase to end of line, write these characters, restore cursor". Replaying the last N lines of such a stream to a freshly opened terminal produces visual garbage, because the replay is missing all the state the sequences depend on. Worse, a mid-stream resume can begin inside an escape sequence.

**The model.** The agent parses every byte of a session's output through a VT state machine and maintains a live screen model:

* A grid of cells (character + SGR attributes) sized to the current PTY dimensions.
* Cursor position, visibility, and saved cursor.
* Current SGR state, character sets, and relevant DEC private modes.
* Both the primary and alternate screen buffers, and which is active.
* Window title.

The parser is the canonical Paul Williams ANSI/DEC state machine (`GROUND`, `ESCAPE`, `CSI_ENTRY`, `CSI_PARAM`, `OSC_STRING`, and so on), which is byte-oriented and therefore immune to being handed a chunk that splits a UTF-8 character or an escape sequence. `VtNetCore` is a viable starting point; a purpose-built parser is acceptable and probably smaller, since only the subset of sequences that these CLIs actually emit needs to be handled.

**Snapshot by re-serialization.** On attach, the emulator does not send a cell grid in a bespoke JSON format. It **re-serializes its screen model back into VT escape sequences** — a byte stream that, when fed to a fresh terminal, reproduces the screen exactly:

```
reset → select screen buffer → clear → for each row: position, emit runs with SGR
      → restore cursor position, visibility, and title
```

The client therefore needs no snapshot decoder at all: a snapshot and a live delta are the same kind of thing, and both are simply written to `xterm.js`. This also gives a strong correctness property to test against (§8.1).

**No scrollback.** Only the visible screen is modelled, which keeps per-session memory in the low hundreds of kilobytes and keeps snapshots small enough to deliver over a cellular link in one round trip. Scrolling back on a phone was not a goal; the desk terminal still has full scrollback.

### 4.4 Output framing and flow control

A single `npm install` can emit megabytes per second. Forwarding that verbatim would saturate a cellular link, blow past SignalR's default 32 KB message limit, and make the UI unusable.

The agent applies four rules:

1. **Coalesce.** Output is accumulated and flushed on a fixed ~30 Hz tick (33 ms) rather than per read. This alone collapses most TUI redraw storms.
2. **Cap.** A frame is capped at 24 KB, comfortably under the 32 KB default limit.
3. **Split safely.** Frame boundaries are only taken when the VT parser is in the `GROUND` state, so a frame never ends mid-sequence.
4. **Re-snapshot instead of replay when behind.** Queue depth is per client, and the queues live in the hub rather than the agent — the hub is where the difference between one slow phone and a slow session is visible. When a client's queue passes **256 KB** or its oldest frame passes **2 seconds**, the hub **discards that client's backlog entirely** and asks the agent to send it a fresh snapshot.

Rule 4 is the payoff of the screen-state model. Because only the visible screen matters, throwing away a backlog is not lossy from the user's point of view — the snapshot already reflects everything the discarded bytes would have produced. A client on a bad link converges to the current screen instead of falling ever further behind.

Two details make rule 4 survive contact with a genuinely bad link:

- **Every client has its own queue and its own sending task.** SignalR processes one invocation at a time per connection, so if the hub awaited the fan-out inside the agent's call, one phone whose transport buffer was full would stop output for *every session on that machine*. Publishing is therefore non-blocking, and a stalled client can only ever stall itself.
- **Forced repaints are throttled to one per client every 2 seconds.** A link too slow to carry the output is also too slow to carry a screen, so without a floor the hub would answer each overflow with a snapshot, overflow on the snapshot, and spend the whole link on repaints that never arrive. With the floor, a client that cannot keep up settles into a slow cadence of complete screens — the most useful thing a bad link can deliver.

Because several clients may watch one session, frames that answer a *particular* client — an attach snapshot or a resume replay — carry a target and are delivered only to it. The others never missed those bytes; applying them again would write them onto the screen a second time.

A targeted frame does not consume a sequence number. It carries the number of the state it depicts, so two consecutive frames may share a number, and a repaint sent to one client never appears as a hole to the others. Clients therefore treat sequence numbers as non-decreasing, and report missed output only on a genuine skip.

A snapshot obeys the cap too. A densely coloured full screen re-serializes to well over 24 KB, so it is cut at the same `GROUND` boundaries and sent as several frames. Only the first carries `Kind = Snapshot` — that is what tells the client to clear what it holds — and the rest are ordinary deltas painted on top. All of them are emitted inside the session's exclusive region, so live output cannot interleave between two frames of the same snapshot.

Frames are carried as binary via the MessagePack hub protocol, avoiding the ~33 % overhead of base64 in JSON.

### 4.5 Resume after a brief disconnection

Each session's outbound frames carry a monotonically increasing `seq`. The agent retains a small tail buffer (the last 256 KB, or fewer frames if larger). On reattach the client sends its last received `seq`:

* If the requested `seq` is still in the tail buffer, the agent sends the missing frames, unchanged and with their original numbers — a fast path for a two-second signal drop. Renumbering them would present the client with a gap it would report as lost output.
* Otherwise the agent sends a fresh snapshot with a new `seq` baseline.

Both outcomes are correct; the tail buffer is purely a latency optimization.

Two rules keep the fast path honest:

* **Numbering happens before sending, not after.** Output produced while the hub is unreachable still consumes a sequence number and still enters the tail. Otherwise a client resuming across a hub outage would receive an unbroken run of sequence numbers with the outage's output missing from it — a screen that is wrong while claiming to be continuous, which is worse than any repaint. A long outage simply evicts its way out of the tail and the reattach is answered with a snapshot.
* **A reshape disqualifies replay.** If the attaching client's geometry differs from the session's, the missed frames were produced for a screen of another shape and replaying them would place wrapped lines where they used to belong. That failure looks plausible rather than obviously broken, so the agent repaints instead.

### 4.5.1 Structured ACP chat sessions

The tray agent can expose recent conversations from one selected Agent Client Protocol provider alongside wrapped terminals. GitHub Copilot is the default and is launched as `copilot --acp --stdio`. Setting `ONEREMOTE_ACP_PROVIDER=claude` selects the official `claude-agent-acp` adapter instead. Both speak ACP v1 as JSON-RPC 2.0 over newline-delimited JSON on stdio, support `session/list`, `session/load`, `session/prompt`, `session/update`, and standard `session/request_permission`, and use the same persisted conversation store as their desktop experience.

That shared store supports **sequential resume, not live cross-process synchronization**. An already-open Copilot Desktop session belongs to its own private `copilot --server --stdio` child, while 1RemoteCLI owns a separate public ACP process. GitHub exposes no supported API for injecting updates into the desktop process or refreshing its live view. 1RemoteCLI therefore never writes Copilot's private SQLite schema and never attaches to private desktop IPC.

Discovery is intentionally bounded to the 20 most recently updated conversations from the last 14 days. ACP lists resumable history rather than a trustworthy "currently open" bit; exposing the whole store would turn the machine list into an archive browser. The provider process is local, inherits the interactive user's credentials, and is restarted if it exits or a refresh fails. Consecutive failures back off from 5 seconds to a 5-minute ceiling; only the first failure, changed failures, and power-of-two summaries are logged, followed by one recovery line after a successful refresh. The retries continue indefinitely, so installing the configured CLI later restores discovery without restarting the agent. `ONEREMOTE_ACP=0` disables discovery, and `ONEREMOTE_ACP_EXECUTABLE` overrides the selected executable for non-standard installs.

An ACP session is a `SessionInfo` with `kind = AgentChat`. Attaching calls `session/load`, reconstructs a typed transcript from `session/update` notifications, and sends a targeted snapshot only to the attaching phone. Later updates are replacement deltas keyed by stable event id:

- consecutive `user_message_chunk` and `agent_message_chunk` frames become user and agent message events;
- `tool_call` and `tool_call_update` replace one tool event as its status changes;
- `session/request_permission` becomes a permission card containing exactly the options the provider advertised;
- supported `elicitation/create` form requests become question cards with single-select menus, and the confirmed answer is returned on the provider's original reverse-RPC connection. Unsupported form shapes receive an explicit invalid-params response so the provider can fall back instead of leaving an unusable pending card.

The ACP client advertises form elicitation support during initialization. The PWA keeps tool activity separate from conversation text and offers compact, summary, and full detail levels: compact retains only active tools, summary keeps one-line activity cards, and full includes tool output. Long paths and unbroken tool text wrap within the session viewport rather than widening the page.

**Plans are atomic, turn-scoped task trees.** Every native ACP `plan` update replaces the current user turn's plan in place; starting a later user turn creates a new plan event, so completed and failed history remains in transcript order rather than being overwritten by unrelated work. Protocol version 9 appends a stable `taskId`, optional `parentTaskId`, and bounded `depth` to each plan entry, plus the owning `planTurnId` and monotonic `planRevision` to the event. The daemon preserves provider-supplied enrichment when present, derives deterministic task identity across ordinary flat ACP snapshots, resolves depth-only trees, and normalizes completed, in-progress, failed, and pending states. A version 8 peer stops at the original three plan-entry fields; a new PWA receiving that older shape derives stable fallback ids and renders a flat list.

The phone presents each plan as a persistent, collapsible tree with connector lines, aggregate completion progress, distinct accessible state icons, and per-branch disclosure controls. An in-progress task remains visually prominent. Replacement snapshots retain branch disclosure state because rows are keyed by stable task id, while transcript snapshots and reconnects carry every turn-scoped plan event just like any other transcript item. Hierarchy is enhancement data rather than a requirement: ordinary ACP agents continue to produce a useful flat task list.

**Prompt capabilities are negotiated, never assumed.** `initialize` returns `agentCapabilities.promptCapabilities`; the ACP client reads `image` and `embeddedContext` as strict booleans — absent, non-boolean, or misspelled is `false` — and carries them on every chat `SessionInfo` as an appended `chatCapabilities` field. A peer that predates protocol version 6 omits the field entirely, which decodes as "no attachment support" rather than "unknown", so an older PWA offers nothing rather than offering a picker whose photo would be refused after it had been taken. Capabilities are re-negotiated whenever the ACP process restarts: the provider applies the new answer to every discovered session and publishes `SessionUpdated` for each one whose answer moved, and applies "none" when the process is lost, so a composer cannot keep an Attach button that nothing can honour.

**Chat attachments are prompt content, not files.** A browser-selected photo or document reaches the machine over its own bounded, chunked, acknowledged transport (§5.3) and is staged under `%TEMP%\1RemoteCLI\chat-attachments\<sessionId>\<attachmentId>`, owned by exactly one session and one client connection. Disk rather than memory, because four files arriving slowly from a phone would otherwise sit in the agent's heap for as long as the phone takes to give up; and the path is never named to the phone, because the whole difference from a terminal upload is that nothing here becomes a path the user pastes.

`session/prompt` is built as an ordered array: the trimmed text block first when there is text, then the attachments in the order the user selected them. An attachment whose bytes carry a PNG, JPEG, WebP, or GIF signature becomes an ACP `image` block typed from that signature — the browser's declared type is only the operating system's guess about an extension, so the bytes win — and requires the advertised `image` capability. Everything else requires `embeddedContext` and becomes an embedded `resource` under a synthetic `attachment://1remotecli/<attachmentId>/<name>` URI: valid strict UTF-8 travels as `text`, anything else as Base64 `blob`. The URI is deliberately not a `file:` path, because the file is in a browser on a phone and inventing a machine path for it would be a lie the agent could act on. A file that claims to be an image and carries no image signature is refused rather than downgraded to a resource. An attachment-only prompt is allowed; a prompt with neither text nor attachments is not. Text-only prompts continue to travel as `SendChatMessage`, unchanged, so an agent that predates attachments keeps working.

The transcript echo carries a metadata-only summary — filename, media type, size — and never a byte of the file: the transcript is broadcast to every attached device, replayed on every snapshot, and quoted in logs the moment something fails.

**Limits are shared and enforced three times.** `ChatAttachmentLimits` fixes 5 MB per attachment, 10 MB aggregate per prompt, at most 4 attachments, 64 KB chunks, and the existing 20,000-character text cap; the PWA mirrors them. They are far below the 25 MB terminal ceiling on purpose: a chat attachment is Base64 (four thirds larger) inside one JSON-RPC line and then part of a context window, which is smaller and more expensive than disk. The browser checks so a phone can refuse a 12 MB file before spending a minute uploading it; the hub checks so an oversized request never reaches an agent; the agent checks the bytes themselves, because a declared size and a declared type are only ever claims.

The phone can send a prompt only after attaching. The hub resolves that attachment inside the authenticated user's partition and never accepts a user identity as a parameter, preserving the same routing invariant as terminal input. For an ACP chat, attachment is also an ownership handshake: `SessionInfo.chatState` remains `Available` until `session/load` succeeds, then becomes `Ready`. A provider rejection that says another process owns the session becomes `Busy`; any other load failure becomes `Unavailable`. The PWA disables its composer unless the state is `Ready`, explains that Copilot Desktop does not update live, and offers a retry after the user closes the other view. An agent predating protocol version 8 omits the field and decodes as `Unknown`, which also blocks prompts rather than guessing that concurrent writes are safe.

Permission ownership has one unavoidable boundary. A reverse RPC request belongs to the stdio connection on which the provider issued it. 1RemoteCLI can display, push-notify, and answer permissions raised by turns started through its own ACP process. It cannot observe or answer a transient permission already waiting on the desktop app's private connection. The persisted transcript can still be loaded, and the conversation can be continued after that desktop-owned turn completes; the UI and documentation must not claim that an already-open desktop approval can migrate between connections.

ACP turns count as active work for update safety. An agent restart or self-update is deferred until no prompt is in flight, so installing a release cannot terminate a provider halfway through a tool call or leave a permission request with no process to answer.

### 4.6 Reconnection

Connections are expected to fail. A phone walks into a lift, a laptop suspends, the hub restarts on deploy. None of these are errors worth reporting to anyone; all of them are handled by retrying.

**Both ends retry forever.** Delay starts at zero, then 1 second doubling to a 30-second cap, with up to 1 second of jitter **added** to each delay so that a hub coming back does not receive every client at the same instant. The jitter is additive rather than multiplicative so it can only ever lengthen a wait, never shorten one below the intended floor.

There is no attempt limit. A retry policy that gives up is a policy that requires a human to notice and act — which is precisely the situation the product exists to avoid, since the human is not at the desk.

**The agent re-registers on every reconnect.** The hub's routing registry is in memory, so a restarted hub has never heard of anybody. On reconnect the agent republishes its machine and every live session. Without this, a routine deployment would silently strand every machine: the sessions keep running perfectly at the desk, and only the phone can tell that anything is wrong — the hardest possible shape of failure to diagnose.

**The client retries its first connection too.** The SignalR client's automatic reconnect covers a connection that was established and then dropped; it does not cover a `start()` that never succeeded. The PWA therefore wraps the initial connect in the same policy, so opening the app while the hub is down waits and recovers rather than failing to a dead screen.

**The UI must not lie about it.** A phone whose socket is down shows *Reconnecting*, not a green dot over a screen that quietly stopped updating. States that are already final are left alone: a session that ended has not become uncertain because the network did. And a session whose exit the client actually witnessed is remembered as ended, so that reattaching after a reconnect reports the exit code it saw rather than the weaker "this session is gone" that the hub would otherwise answer with.

### 4.7 Relay hub

ASP.NET Core 8 with self-hosted SignalR on a **single instance** of App Service or Container Apps. Azure SignalR Service was rejected because it bills per message and a live terminal is extremely chatty; a single self-hosted instance comfortably serves the target scale.

**Single instance is a load-bearing assumption.** The routing registry is in memory, so scaling out to two instances would break routing — an agent connected to instance A is invisible to a phone connected to instance B. The deployment is therefore pinned to one instance, and §9 records what changes if that ever needs to lift.

**Deployment target.** All Azure resources — the hub, its App Service or Container Apps environment, and the Entra app registration of §3.1 — live in a single Azure subscription owned by the project owner. No secrets, subscription identifiers, tenant identifiers, or owner account names are stored in the repository; the concrete target is recorded in an untracked `azure-target.local.md`, runtime configuration comes from App Service settings or Key Vault, and local development uses .NET user-secrets.

**Registry.**

```
UserKey ─┬─ Machines: machineId → { connectionId, displayName, os, lastSeen,
         │                          notificationLevel }
         │                          └─ Sessions: sessionId → { program, args, cwd,
         │                                                     cols, rows, startedAt }
         └─ Clients:  connectionId → { attachedTo: (machineId, sessionId)? }
```

Everything is reconstructed by agents and clients reconnecting after a restart. No database (§9 notes the one real cost of this: push subscriptions).

**Routing.** Every hub method resolves `UserKey` from the connection's principal, looks up the target inside that partition only, and rejects with an `Error` message if the target is absent. Since a machine that does not belong to the caller is not in the caller's partition, a spoofed `machineId` finds nothing.

**Liveness.** SignalR keep-alive at 15 seconds, client timeout at 30 seconds. A dropped agent connection marks the machine offline and notifies that user's attached clients; its sessions are removed, since sessions cannot outlive their wrapper. Both numbers live in one place and are applied to the hub and to the end-to-end harness alike, so the hub a test exercises behaves like the hub that is deployed on exactly the axis those numbers govern.

### 4.8 Mobile PWA

React + Vite + Tailwind, `@xterm/xterm` with `@xterm/addon-fit` and `@xterm/addon-web-links`, and a service worker for installability and Web Push.

**Screens.** A machine list (online/offline, session counts); a session list per machine (program, CLI type, session kind, working directory, uptime, "waiting for input" badge); the terminal view; and a structured chat view with user and agent messages, tool status, permission choices, and a message composer.

**Chat Plan view.** An ACP chat offers Compact, Summary, Full, and Plan views. Compact through Full remain transcript detail levels, including any native ACP plan events. Plan is a separate read-only view of the task state Copilot Desktop persists at `%USERPROFILE%\.copilot\session-state\<sessionId>\session.db`: the agent opens only a GUID-named session directory, uses a read-only SQLite connection, validates the `todos` schema, and reads `todo_deps` when compatible. It advertises a non-empty task snapshot on `SessionInfo`; an older agent, missing database, incompatible schema, or empty task table leaves the field null and the PWA disables Plan. Completed, active, pending, blocked, and failed tasks have distinct status marks, while dependency edges impose a stable prerequisite-first ordering without turning the phone view into a graph.

The local task snapshot is independent of ACP's native `plan` transcript updates: Copilot Desktop currently updates its task database through tool calls without emitting those updates. The agent refreshes the snapshot during session discovery and attach, and after live tool-call updates, then uses the existing `SessionUpdated` fan-out so an open phone view follows status changes. Per-session reads are serialized and each snapshot is read under one SQLite transaction so `todos` and `todo_deps` cannot represent different database revisions.

**Knowing what a session is running.** Each session carries a `cliType`, worked out by the agent from the command line it was asked to wrap — `claude`, `pwsh`, `gh copilot` — and shipped as a field on `SessionInfo`. It exists so the phone can offer the right buttons: a terminal on a phone is mostly a screen you cannot type into comfortably, and the difference between a usable session and a frustrating one is whether `/compact` and `Shift+Tab` are one tap away rather than a dozen against autocorrect.

Detection is a pure function of the command line, not of the output stream. Sniffing would be more accurate for the awkward cases and is the wrong trade twice: it puts a parser on the hottest path in the product, and it makes the type arrive some seconds *after* the session, so the buttons appear late on the one screen where the user is already waiting. Desktop-shortcut detection is always shown for confirmation before creation; its confirmed answer is persisted into the generated wrapper and wins over later automatic detection.

The command line it reads is the whole one, not just the program. Desktop shortcuts for these tools are written as `cmd /k "copilot --allow-all-tools"`, because a shortcut that starts a `.cmd` shim directly closes its window the moment the tool exits and the shell is there to hold it open. So a shell is treated as a doorway: `cmd`'s `/c` and `/k`, and PowerShell's `-Command` and `-File`, are read for the program they were told to run, up to two shells deep, and the shell is only the answer when there is nothing recognisable behind it. Without that, every session started the way #66 exists to support — from a wrapped shortcut — would be labelled a shell, which is true about what was launched and useless about what is running.

It is a hint and never a control — nothing about how a session is relayed, framed or interrupted changes with the type — so guessing is acceptable here in a way it would not be anywhere else. The worst a wrong answer does is offer a menu of commands the program does not have. The guess is shown twice: as a badge on the session row, and in the terminal view's header beside the machine and geometry, because the header is where you are standing when you notice it is wrong. Tapping it there opens the quick-action sheet with the picker already showing — one picker, in the place where the buttons it decides are. The correction travels to the *agent*, which owns session state, and comes back as `SessionUpdated` to every device. Applying it locally would be one line of code and would leave the phone in the pocket, the tablet on the desk, and the settings window disagreeing about the same session.

**Naming and pinning a session.** Three sessions all called `pwsh`, on two machines, is the state the list is actually in after an hour's work, and the agent cannot fix it: it knows what was launched, not what it is for. So a session can be renamed from the list — a `⋯` button on the row opens a small panel with a text field and a pin toggle, rather than crowding two controls onto a row that is a single tap target on purpose.

The name is stored at the hub (§5.3) rather than in the browser, which is what lets it reach a **push notification**: the reason to call something "the deploy" is that the phone says the deploy is waiting. Clearing it reveals the agent's own name again. Wherever a session is named — the row, the terminal header, the "opening…" line, the notification title — the same order is used: the user's name, then the agent's, then the program.

Pinned sessions are lifted into a single **Pinned** group above every machine card, not merely sorted to the top of their own. On the screen this app is for the fold arrives after about four rows, and a session pinned on the third machine down would otherwise still be below it. The pinned row shows its machine's name where it would otherwise show the working directory, since it is now being read away from the card that said which machine it was on; the machine's own session count still includes it, because that count answers "what is running there", which pinning does not change.

**Terminal view.** The `xterm.js` viewport, a status bar, and a fixed accessory bar above the on-screen keyboard, addressing the fact that mobile keyboards have no modifier or arrow keys:

```
┌──────────────────────────────────────────────────────────┐
│  DESKTOP-MAIN › claude          ● live          [detach] │
├──────────────────────────────────────────────────────────┤
│                                                          │
│   ✔ Updated src/app.ts                                   │
│   ? Allow file edit to src/api.ts? (y/n) ▊               │
│                                                          │
├──────────────────────────────────────────────────────────┤
│  Ctrl  Alt  Esc  Tab   ↑  ↓  ←  →   ⏎        [ Ctrl+C ]  │
└──────────────────────────────────────────────────────────┘
```

`Ctrl` and `Alt` are sticky modifiers: tapping arms them for the next keypress, tapping again disarms. Sticky rather than held, because a thumb cannot hold one button while pressing another on a screen this size — and because it is the affordance iOS and Android already use for their own Shift key, so it is not a new idea to anybody holding the device. An armed modifier is consumed by exactly one keypress, from the accessory bar or the software keyboard alike; leaving one latched would turn the next letter into a control code nobody asked for, and an unintended control code is not a typo you can see and correct.

The encodings are the ones a real terminal puts on the wire, since the whole design rests on the PTY being unable to tell the phone apart from the keyboard on the desk. `Ctrl`+letter clears bit 6, which is ASCII's own construction (`Ctrl+C` → `0x03`); the digit row carries the DEC convention that makes `Ctrl+3` an Esc and `Ctrl+8` a Delete; `Alt` prefixes an escape, which is what readline, bash and zsh are written against. A character `Ctrl` does not modify is sent unchanged rather than swallowed, so arming `Ctrl` and typing an accented letter produces the letter. Cursor keys carry their modifiers inside the sequence — `Ctrl+Left` is `CSI 1;5D`, which readline reads as "back one word" — while unmodified cursor keys keep the short `CSI D` form, because programs that match on exact bytes, as agent prompts do, recognise only that one.

`Ctrl+C` is a distinct, red, always-visible control because interrupting a runaway agent is the single most time-critical action in the product. It is deliberately not reachable only through the sticky-modifier path, it never sits behind the "more" disclosure, and it discards whatever was armed rather than combining with it — it is the one key that must do the same thing every time it is pressed. It also does not travel as a byte: it uses the dedicated interrupt method, which signals the process, because a session wedged badly enough to have stopped reading its input is exactly the session being interrupted.

**Per-CLI quick actions.** Behind a disclosure on the same bar sits a short, per-`cliType` list: the shortcuts that program is actually driven by, and its most-reached-for commands. Claude Code and Copilot CLI get `Shift+Tab` (`CSI Z`), which is how you change what an agent is allowed to do without editing a config file, and Claude Code additionally gets a double Escape as a single button — it distinguishes one Esc from two by timing, and a relayed double tap cannot be relied on to land inside that window. Shells get PSReadLine's line editing instead, which is the part that hurts most without a physical keyboard.

The lists are deliberately short. A list of everything is a reference manual, and a reference manual on a phone screen is slower than typing. Commands are *inserted, not submitted*: half take an argument, and the ones that do not include `/clear`, which discards work that cannot be recovered by apologising to the model afterwards. A session whose type is unknown is offered no commands at all rather than a guess, because a button whose effect nobody can predict is worse than no button.

**Clipboard and attachments.** Paste is an explicit action because mobile browsers do not expose the clipboard to an arbitrary terminal key event. It reads text only from a user gesture and hands it unchanged to `xterm.js`'s paste path. The headless terminal tracks DECSET 2004 and includes it in a snapshot, so xterm adds bracketed-paste delimiters exactly when the remote program enabled them; newlines and escape-looking text remain paste content rather than browser-generated terminal control.

Attach opens the platform file/photo picker and streams one selected file to the attached machine. Files are capped at 25 MB and split into ordered 64 KB chunks. The browser waits for an agent result after every chunk, so progress means the machine accepted those bytes rather than merely that the phone handed them to SignalR. Every operation resolves through the caller's current terminal attachment; the hub supplies the connection id, and the agent binds the upload to that session and client.

The agent writes to `%TEMP%\1RemoteCLI\terminal-uploads\<sessionId>\<uploadId>` under a sanitized leaf name. Each upload id gets a new directory, the in-progress file is named `.partial`, and completion flushes and atomically renames it without overwrite. The returned path is shell-quoted and pasted at the cursor but never submitted. Cancellation, detach, and relay failure remove partial files; closing the terminal removes its completed attachments; startup and an hourly sweep prune session directories older than 24 hours so a crash cannot retain them indefinitely once the agent is running again.

**Chat attachments are a separate feature that happens to share a transport.** An `AgentChat` composer offers Attach — a document picker where the agent advertised `embeddedContext`, an image picker where it advertised only `image` — plus a separate camera action, which exists because `capture` is what makes a phone open the camera rather than the photo library and putting it on the shared input would take the library away from everyone. Nothing is offered when the session's negotiated capabilities are absent or empty (§4.5.1). Selection starts the upload immediately, so the waiting happens while the user is still typing; each item shows its name, type, size, per-item progress, and any failure against the file it belongs to, and can be removed before sending. Removal, closing the chat, and a successful send all delete the staged bytes on the machine, and object URLs are revoked with them. Send stays disabled while an upload is in flight; on the agent's acknowledgement the draft and the selection are cleared, and on a refusal both are kept, because a prompt the machine refused consumed nothing and is worth correcting rather than retyping. The two features stay apart on purpose: a terminal attachment becomes a file whose path is pasted into a PTY, and a chat attachment becomes typed prompt content whose machine path is never disclosed.

**Sizing.** The phone is authoritative while attached: on attach, and on orientation change, the client computes columns and rows from the viewport and sends `ResizeTerminal`, which reshapes the real PTY. The desk terminal window does not resize in response, so a program will render to the phone's narrower width in a wider desktop window until the phone detaches. This is the accepted trade: a correctly reflowed phone view is worth a temporarily odd-looking desk window, and TUIs handle `SIGWINCH`-equivalent resizes natively. On detach the client hands the session back the shape it had at the desk, so walking away does not leave a 45-column program stranded inside a wide window until somebody drags the corner. Resizes are debounced, so the keyboard animating open produces one message rather than thirty.

**The visible area is not the layout viewport.** On iOS, opening the software keyboard does not shrink the layout viewport at all — the page is scrolled up inside a smaller *visual* viewport. A full-height fixed element therefore keeps its full height and hides its own bottom behind the keyboard, and the bottom is the accessory bar and the last lines of output: the two things somebody opened the app to reach. The terminal view consequently measures `visualViewport` and pins itself to an explicit pixel height, translated down by the scroll, rather than using the `inset-0` that would be the obvious thing and is wrong on exactly the device this product is for. Sub-pixel drift is rounded away, because iOS reports fractional heights that wobble through the whole keyboard animation.

**Connection handling.** SignalR automatic reconnect with backoff; on reconnect, reattach with the last `seq`. The status bar distinguishes *live*, *reconnecting*, and *session ended*.

**Installation is a feature, not packaging.** The PWA ships a web app manifest, generated PNG icons, and a service worker — not for offline use, which a live terminal client cannot have, but because on iOS an app that is not installed to the Home Screen can never receive a push notification, whatever permission it holds. Everything in §6.2 is downstream of the user performing *Share → Add to Home Screen* by hand, a step nothing in the browser can prompt for. The app therefore asks: it detects whether it is running standalone and, if not, shows the three-step instruction; only once installed does it offer a permission button, and only from a tap. Prompting inside a Safari tab is worse than not offering at all, because the permission is granted, nothing is ever delivered, and the user believes it is set up.

The same detection distinguishes the cases that look alike and are not: an iPhone below iOS 16.4 is told its OS is too old rather than being sent to install an app that still could not notify; an iPad, which since iPadOS 13 claims in its user agent to be a Macintosh and is caught by its touch-point count, gets iPhone instructions rather than desktop ones; an unreadable user agent is assumed capable, because refusing on an unfamiliar string would break the app on every iOS released after the code was written. After granting, the user can send themselves a local test notification — the one end-to-end check they can perform themselves, and the half of the pipeline that is hard to get right.

**Caching.** Navigations are network-first, which is the opposite of the usual PWA default and correct here: a cached shell is never more useful than a fresh one, and serving last week's client to a phone with a working connection risks talking a superseded protocol to a hub that has moved on. The cache exists so that opening the app in a lift shows the app reporting no connection rather than the browser's error page. Hashed assets are served cache-first, because a hit on a content-hashed URL is always correct. The cache is named after a fingerprint of the build's own asset list, so it rolls exactly when the contents do, with no version constant for anybody to forget. A new worker takes over immediately rather than waiting for every tab to close: there is no unsaved work on the phone to lose — the session lives on the machine — and a stale build is the failure that matters.

Only same-origin `GET`s are intercepted. The hub is a WebSocket to another origin and the identity provider is a redirect to a third; a service worker that intercepts an auth redirect is a very confusing thing to debug.

### 4.9 Projects (issue #110)

Sessions accumulate with nothing above them: ten machines, thirty sessions, one flat list. Projects are the grouping layer, and the design keeps to the same shape as everything above it — per-user, hub-owned, and no new concept for the agent to learn.

**The agent stays unaware of projects entirely.** A project is something a user does to their own view of their own sessions; it is not a property of the program that is running. Teaching the wrapper or the pipe protocol about it would mean every one of §4.1–§4.7's invariants (identity from the connection, not the caller; the agent as the sole writer of session state) needs a second answer for "and what about projects", for a feature that never needs the desk side to know it exists.

**Data model.** A project is `{ projectId, name, description?, siteUrl?, repoUrl?, isGeneral, iconVersion, createdAt }` (`ProjectInfo`, `src/Protocol/Models.cs`). `SessionInfo` carries `projectId` (`string?`) — `null` means General, exactly as `customName == null` means "use the agent's own name" (§5.3) — plus the appended `suggestedProjectId` and `suggestedProjectMoves` fields used by learned routing. Every user gets exactly one project with `isGeneral == true`, the reserved id `"general"` and the fixed name `"General"`, seeded the first time `ListProjects` is asked for that user and never deletable or renamable. Its optional metadata and icon remain editable. Uniqueness is per-user and case-insensitive, checked against every other project and the reserved General name.

**Where the assignment lives versus where the definition lives — two different stores, on purpose:**

- **A session's project assignment is a label, exactly like `customName`/`pinned` (§5.3's "Session names and pins")** — held in the registry's per-machine map, not on the persisted project or the session record. It survives an agent reconnect the same way a rename does: the agent re-announces every open session by the same `sessionId`, and the label is re-applied. It does **not** survive the session itself ending, which matches "sessions die with the wrapper" (§9) and needs no durable store of its own.
- **Project *definitions*** — name, description, URLs, icon — are the hub's first real persistent store, `ProjectStore` (`src/Hub/Projects/ProjectStore.cs`), following `OperatorStateStore`'s already-proven shape (§4.6, `docs/operator-channel.md`): one JSON file under the App Service data volume, read at startup, tolerant of a corrupt file (start that user's projects from empty rather than refuse to boot), written whole-file and atomically (temp file, then rename, never a partial write on disk). Unlike the operator channel's 30-second flush timer, a project mutation flushes to disk immediately — creates, renames and deletes are rare and user-initiated, and silently losing one on a crash a few seconds later would be a bad experience for a feature this small. `docs/deployment.md` describes the file and its rollback behaviour.

**Icons are files, not wire payloads.** A project icon is stored on disk beside the JSON state (`Hub/Projects/ProjectStore.cs`'s icon root, one subdirectory per user) and served through authenticated `POST/GET/DELETE /projects/{projectId}/icon` endpoints. Ownership is enforced from the caller's `UserKey`, never a request parameter. Uploads are capped at 512 KB and restricted to PNG, JPEG and WebP by both declared content type and file signature. The PWA downscales to a square before uploading. It fetches an icon with an Authorization header and gives `<img>` a local blob URL, so bearer tokens never enter image URLs or logs. `iconVersion` cache-busts requests; zero selects the app icon. Upload and clear broadcast `ProjectUpdatedNotification` to every client.

**Stats are computed client-side, not on the hub.** The PWA already holds the full machine/session list; the project tiles group that list by `projectId ?? "general"` to answer "how many sessions, across how many machines" (`relay/projects.ts`'s `projectStats`). There is no hub-computed counter and no new fan-out message for it — it is always exactly as fresh as the session list the app already maintains, and a hub that had to keep per-project counts in sync with every session open/close/move would be a second source of truth for numbers the client can derive for free.

**Moving a session** is a hub-answered client method, `SetSessionProject { machineId, sessionId, projectId, kind }` (`RelayHub.SetSessionProject`), resolved from the caller's own partition the same way `SetSessionName`/`SetSessionPinned` are — not through the session's attachment, because moving is done from the list, where nothing need be attached. It validates the target project exists (skipped when `projectId` is `null`, i.e. "move to General"), edits the label through the same `TryEditLabel` machinery as a rename, and fans out the ordinary `SessionUpdated` to every device. `kind` distinguishes a manual selection, an accepted suggestion, and an explicit "Always move" choice; its default value is manual so older clients retain their historical behavior.

**Learned routing is per-user and hub-owned.** When a user moves a General session, `ProjectStore` hashes its stable launch pattern — machine, effective name, executable, working directory, arguments, session kind, and CLI type — and increments that pattern's destination count without persisting those values in plaintext. A manual move is enough to suggest the destination for the next matching General session. Equal top evidence for two destinations suppresses the guess. Accepted suggestion counts are tracked separately; after four accepted suggestions the PWA offers an adjacent "Always move" action. Choosing it stores one automatic rule for that pattern, and future matching sessions are assigned before their open notification is fanned out. A durable assignment for the same live session id takes priority, and deleting a project also deletes every learned record that points to it.

**Deleting a project** reassigns its live sessions back to General synchronously — `RelayRegistry.ClearProjectAssignments(userKey, projectId)` sweeps every machine the user owns, online or not, clearing any label pointing at the deleted project — and the hub fans out one `SessionUpdated` per affected live session plus a single `ProjectDeletedNotification`. A machine that was offline at delete time still has its on-disk-nowhere, in-memory label cleared the moment it reconnects and re-announces, by the same path a normal `SessionOpened` always went through — but as a defence-in-depth backstop for any path that might otherwise miss it, `RelayHub` also self-corrects: whenever a session is announced or updated whose `projectId` no longer resolves to a real project, the hub clears it back to General there and then (`CorrectStaleProjectIfNeeded`, called from both `SessionOpened` and `SessionUpdated`). Between the sweep and the backstop, a session can never be left pointing at a project that no longer exists.

**Create/update return the record directly** — `ProjectResult { Project?, Error? }` — *and* fan out `ProjectCreatedNotification`/`ProjectUpdatedNotification` to every client of that user. The direct return exists because the caller needs the generated id immediately, most importantly to follow up with an icon upload in the same flow; the fan-out is what keeps every other open device in sync, exactly like the rest of the hub.

**PWA screens and navigation.** The home screen is project tiles (`ui/ProjectTiles.tsx`) rather than the machine list directly: large tiles carrying the icon (versioned URL or the default asset), the name, and the live stats described above, plus an "add project" tile that opens `ui/ProjectEditor.tsx` — one form, reused for both create and update, with name/description/site URL/GitHub URL fields, an icon file picker with client-side canvas downscale, and a delete action disabled for General. Selecting a tile drills into the existing session list (`ui/MachineList.tsx`), now filtered to sessions whose `projectId` (or its absence) matches the selection, with a back affordance in the header — additive to the existing component, not a rewrite, and not a router dependency: `App.tsx` holds a small `view: 'projects' | 'sessions'` plus `selectedProjectId` state. The session row/editor gains a "move to project" control next to rename and pin, backed by the same project list the tiles already loaded.

---

## 5. Protocol specification

Three protocols: the local pipe (wrapper ↔ agent), and the hub protocol in its agent and client halves. All hub traffic is MessagePack over WebSockets, secured by TLS.

### 5.1 Local pipe protocol (wrapper ↔ agent)

Length-prefixed MessagePack frames over the named pipe.

| Direction | Message | Payload |
| :--- | :--- | :--- |
| W → A | `SessionOpened` | `program`, `args[]`, `cwd`, `cols`, `rows`, `displayName?`, `priorSessionId?`, `supportsReconnect` |
| A → W | `SessionAccepted` | `sessionId` |
| W → A | `Output` | `bytes` — raw PTY output |
| W → A | `SessionClosed` | `exitCode` |
| A → W | `Input` | `bytes` — to write to the PTY |
| A → W | `Resize` | `cols`, `rows` |
| A → W | `Interrupt` | — sends `0x03` |

`priorSessionId` and `supportsReconnect` are additive (issue #174): a fresh session omits `priorSessionId`, and a reconnect after a lost pipe (§4.1) sends the id it held before so the registry can hand it straight back. `supportsReconnect` defaults to `false` so an older wrapper is never mistaken for one the update restart blocker can safely ignore (§4.2).

### 5.2 Agent ↔ hub

**Agent → hub**

```json
{ "target": "RegisterMachine",
  "arguments": [{
    "machineId": "6f9a1c22-4d18-4b0e-9d3a-2a7e5b0c81f4",
    "displayName": "Primary Dev Workstation",
    "os": "Microsoft Windows 11 Pro 10.0.26100",
    "agentVersion": "1.0.0",
    "protocolVersion": 5,
    "notificationLevel": "AllAttentionEvents"
  }]
}
```

```json
{ "target": "SessionOpened",
  "arguments": [{
    "sessionId": "s-a94f29901",
    "program": "claude",
    "args": ["--resume"],
    "cwd": "C:\\Projects\\1RemoteCLI",
    "cols": 120, "rows": 30,
    "startedAt": "2026-08-13T15:22:04Z",
    "cliType": "ClaudeCode"
  }]
}
```

```json
{ "target": "TerminalOutput",
  "arguments": [{
    "sessionId": "s-a94f29901",
    "seq": 4821,
    "kind": "delta",
    "data": "<binary VT bytes>"
  }]
}
```

`kind` is `"delta"` or `"snapshot"`; a snapshot resets the client's terminal before it is applied.

Also: `SetMachineNotificationLevel { notificationLevel }`, `SessionClosed { sessionId, exitCode }`, `SessionUpdated { session }`, `SessionAwaitingInput { sessionId, hint }`, `SessionAttention { sessionId, awaitingInput, hint }`, `ChatTranscript { sessionId, seq, kind, events[], targetConnectionId? }`, `RefreshToken { token }`.

`SessionUpdated` is deliberately not a second `SessionOpened`, even though the registry's add is an upsert and would store the right thing. An open is counted in the usage figures, and being told twice what a session is should not look like having started it twice.

**Hub → agent**: `AttachRequested { sessionId, clientConnectionId, cols, rows, lastSeq? }`, `DetachRequested { sessionId, clientConnectionId }`, `SendInput { sessionId, data }`, `BeginTerminalUpload { sessionId, clientConnectionId, uploadId, fileName, totalBytes }`, `UploadTerminalChunk { sessionId, clientConnectionId, uploadId, offset, data }`, `CancelTerminalUpload { sessionId, clientConnectionId, uploadId }`, `ResizeTerminal { sessionId, cols, rows }`, `InterruptSession { sessionId }`, `SendChatMessage { sessionId, text }`, `BeginChatAttachment { sessionId, clientConnectionId, attachmentId, fileName, mimeType, totalBytes }`, `UploadChatAttachmentChunk { sessionId, clientConnectionId, attachmentId, offset, data }`, `CancelChatAttachment { sessionId, clientConnectionId, attachmentId }`, `SendChatPrompt { sessionId, clientConnectionId, text, attachmentIds[] }`, `RespondChatPermission { sessionId, requestId, optionId }`, `SetSessionTypeRequested { sessionId, cliType }`, `TokenExpiring { expiresAt }`.

### 5.3 Client ↔ hub

**Client → hub**: `ListMachines {}`, `AttachSession { machineId, sessionId, cols, rows, lastSeq? }`, `DetachSession { sessionId }`, `SendInput { sessionId, data }`, `BeginTerminalUpload { sessionId, uploadId, fileName, totalBytes }`, `UploadTerminalChunk { sessionId, uploadId, offset, data }`, `CancelTerminalUpload { sessionId, uploadId }`, `ResizeTerminal { sessionId, cols, rows }`, `InterruptSession { sessionId }`, `SendChatMessage { sessionId, text }`, `BeginChatAttachment { sessionId, attachmentId, fileName, mimeType, totalBytes }`, `UploadChatAttachmentChunk { sessionId, attachmentId, offset, data }`, `CancelChatAttachment { sessionId, attachmentId }`, `SendChatPrompt { sessionId, text, attachmentIds[] }`, `RespondChatPermission { sessionId, requestId, optionId }`, `SetSessionType { sessionId, cliType }`, `SetSessionName { machineId, sessionId, name? }`, `SetSessionPinned { machineId, sessionId, pinned }`, `RegisterPush { endpoint, keys }`, `RefreshToken { token }`.

**Hub → client**: `MachineList { machines[] }`, `MachineOnline / MachineOffline { machineId }`, `SessionOpened / SessionUpdated / SessionClosed { machineId, session }`, `TerminalOutput { sessionId, seq, kind, data }`, `ChatTranscript { sessionId, seq, kind, events[] }`, `SessionAwaitingInput { machineId, sessionId }`, `SessionAttention { machineId, sessionId, awaitingInput, hint }`, `TokenExpiring { expiresAt }`, `Error { code, message, sessionId? }`.

`SetSessionType` is resolved through the caller's own attachment, like every other client → agent message, so the type can only be corrected from the session you are watching. Resolving it by ownership instead would be more convenient and would add a second way for a client message to reach a machine, which is the invariant that keeps "how could this possibly reach the wrong machine" a one-place question.

`SetSessionName` and `SetSessionPinned` are the two exceptions, and they are exceptions precisely because they are *not* client → agent messages. They are answered inside the hub and never cross to a machine, so the invariant above does not apply to them; what does apply is that they carry `machineId` explicitly and resolve it inside the caller's own partition, because renaming is done from the list, where nothing is attached. A forged machine id therefore finds nothing rather than finding someone else's session.

Both are answered with the ordinary `SessionUpdated` fan-out to every one of that user's clients rather than a notification of their own. The label is applied to the `SessionInfo` the hub materialises, so the message that already exists carries it, and the phone that is looking at the list — attached to nothing — is told as well as the laptop that is watching the terminal.

`SendInput` is a pure passthrough: `data` is the exact byte sequence the terminal should receive — `"y\r"`, `"\u001b[A"` for cursor-up, `"\u0003"` for `Ctrl+C`. The hub never interprets it, which keeps the phone's input indistinguishable from the keyboard's.

The three terminal-upload methods are request/result calls rather than fire-and-forget notifications. Their `TerminalUploadReply { uploadId, confirmedBytes, totalBytes, remotePath?, errorCode?, errorMessage? }` crosses agent → hub → browser unchanged, so a browser never reports progress or inserts a path before the owning agent has accepted the corresponding operation.

The four chat-attachment methods are request/result calls for the same reason, and are a separate family rather than the terminal ones with a different session kind. They only ever resolve to an attached `AgentChat` session; a terminal target is refused, as is a chat target on the terminal family. `ChatAttachmentReply { attachmentId, confirmedBytes, totalBytes, completed, errorCode?, errorMessage? }` deliberately carries no path: a browser-selected chat attachment becomes prompt content, so there is nothing on the machine the phone should be told about. `ChatPromptReply { accepted, errorCode?, errorMessage? }` acknowledges that the agent validated ownership, capabilities and metadata and successfully read the staged bytes — not that the ACP turn finished; the turn continues to arrive as ordinary streamed transcript events, exactly as it does for a text-only message. An agent too old to have the methods answers `attachment_unavailable`, which is a distinct code from the terminal family's `upload_unavailable` so the phone can say which feature needs the update.

Staged bytes are owned by one session and one client connection, and are deleted by cancellation, by a successful prompt consuming them, by the owning client detaching or disconnecting, by the chat disappearing, and by losing the relay connection. Nothing is deleted when a prompt is *refused*, because nothing was consumed and the user's selection is worth correcting rather than re-choosing.

#### Session names and pins

A session carries two labels the user owns rather than the agent: `customName` and `pinned`. They live at the hub, for the lifetime of the session, and are deliberately **not** stored on the agent or in browser storage:

- The hub is the only place where "for as long as the session runs" enforces itself, because the registry entry disappears when the session does.
- It is the only place that can put the user's name in a **push notification**, which is most of the value — the point of naming a session "the deploy" is that the lock screen says the deploy is waiting, not that `pwsh` is.
- It syncs to every device the user has open, for free, through a message that already exists.

The label is held *beside* the session record, in a per-machine map, rather than on the record itself. An agent that drops off the network has its session records cleared and gets them back by announcing the same sessions again; a name stored on the record would be lost to any wifi blip, which for a feature that promises to last as long as the session would be a lie. The map survives the clear and is re-applied whenever a session is added or updated — which also makes the hub the sole writer of those two fields, so an agent cannot introduce a name it was never given.

Labels are dropped when the session closes. The one path that leaks is a session that ends while its machine is offline, where no close ever arrives, so each machine keeps at most 64 labels and evicts orphans — labels with no live session — before anything else.

`name` is `null` to clear it, which is distinct from an empty string: clearing reveals the agent's own `displayName` again, so the two must stay distinguishable all the way down the wire. Names are sanitised once on the way in (`SessionName.Sanitize`, in the shared protocol assembly): control and format characters are dropped, whitespace runs are folded to a single space, and the result is truncated to 60 text elements. This is the only field in the product whose contents are chosen by one person and rendered somewhere that person is not standing — a lock screen, a terminal header, another device's list — so a bidi override or a control character is cleaned at the single point of entry rather than at each of the places it leaves. A name that sanitises to nothing is treated as no name at all.

Like every other display name, a custom name is never logged, at any level, and never reaches the operator channel or the usage counters.

### 5.4 Projects (issue #110)

**Client → hub**: `ListProjects {}`, `CreateProject { name, description?, siteUrl?, repoUrl? }`, `UpdateProject { projectId, name, description?, siteUrl?, repoUrl? }`, `DeleteProject { projectId }`, `SetSessionProject { machineId, sessionId, projectId?, kind }`.

**Hub → client**: `ProjectListNotification { projects[] }` (answers `ListProjects`, General always first), `ProjectResult { project?, error? }` (answers `CreateProject`/`UpdateProject` directly, so the caller has the generated id without waiting for the fan-out), `ProjectCreatedNotification { project }`, `ProjectUpdatedNotification { project }`, `ProjectDeletedNotification { projectId }` — the latter three reach every client of that user, not just the one that asked, the same way `SessionUpdated` does.

`ListProjects`/`CreateProject`/`UpdateProject`/`DeleteProject` are answered entirely inside the hub, like `SetSessionName`/`SetSessionPinned` — there is no agent-facing message for any of them, and §4.9 explains why the agent has no need to know. `SetSessionProject` is likewise resolved from the caller's own partition rather than through an attachment, exactly like the two existing session-label methods, and its effect surfaces the same way theirs does: an ordinary `SessionUpdated` carrying the session's new `projectId`.

New error codes: `ProjectNotFound` (an unknown or another user's project id), `DuplicateProjectName` (case-insensitive collision, including against "General"), `InvalidProjectSiteUrl` and `InvalidProjectRepoUrl` (the corresponding optional URL is not an absolute HTTP(S) URL), `CannotDeleteGeneralProject`.

### 5.5 Versioning

`RegisterMachine` and the client handshake both carry `protocolVersion`. The hub rejects an unsupported version with a clear `Error` telling the user to update, rather than failing in an obscure way at the first incompatible message.

Version 2 added `cliType` and its two messages. The minimum supported version stayed at 1, because both changes are additive: `[Key(n)]` serialises as a positional array, so a version 1 agent simply sends a shorter session and a version 1 client reads a longer one and ignores the tail. A field inserted anywhere but the end would shift every later field with no error anywhere — the machine list would quietly start showing the agent version in the OS column — which is why appending is the only permitted way to evolve one of these messages, and why `wire.fixture.json` pins the layout against bytes the C# serializer actually produced.

`customName` and `pinned` were appended to `SessionInfo` on the same terms, along with `SetSessionName` and `SetSessionPinned`, and the version stayed at 2 for the same reason: a client that has never heard of either reads a longer session and ignores the tail, and one that has reads a shorter one from an older hub and lands on "nobody renamed it". Neither is a new capability the other end has to have — the two methods are answered entirely inside the hub — so there is nothing an older peer could fail to honour.

Version 3 adds `SessionInfo.kind = Terminal | AgentChat`, typed transcript and explicit attention notifications, the chat message and permission-response methods, and projects. `kind` is appended at key 12 and `projectId` at key 13. An older client therefore treats every session as a terminal in General, while a newer client defaults a missing or unknown kind to `Terminal` and a missing project to General. Version 3 peers are required for chat and project methods themselves, but the minimum supported version remains 1 because the wire changes are additive. The wire fixture contains all transcript event shapes, project messages and client requests so the browser's hand-written positional decoder stays pinned to the C# serializer.

Version 4 adds bounded, chunked terminal file uploads. Version 5 appends `notificationLevel` to `RegisterMachine` and adds `SetMachineNotificationLevel`, so a connected agent can apply a Settings change without reconnecting. `AllAttentionEvents` is numeric value zero: a version 4 registration that ends before the appended field therefore retains the historical behavior. Version 6 adds agent-chat attachments — `BeginChatAttachment`, `UploadChatAttachmentChunk`, `CancelChatAttachment`, `SendChatPrompt` — and appends `SessionInfo.chatCapabilities` at key 14. `SendChatMessage` is untouched and remains the path for text-only prompts, so an older agent keeps serving chat exactly as before, and a session record that ends before key 14 reads as advertising no attachment support at all rather than as unknown. Version 7 appends explicit terminal continuity state, learned project suggestion fields, and the project move kind. All have safe defaults when absent, so the hub continues accepting protocol versions 1 through 7.

---

## 6. Notifications

The highest-value feature after attach itself: knowing that a CLI is waiting for you, without keeping the PWA open.

### 6.1 Detecting "waiting for input"

Windows offers no signal for "this process is blocked on a console read", so detection is heuristic. The agent raises `SessionAwaitingInput` when **all** of the following hold:

1. No output for `QuietPeriod` seconds (default 8, configurable).
2. The child process is still running.
3. The cursor is visible.
4. The screen looks like it is waiting: the cursor sits just past text on its own line with nothing after it — the shape of a prompt awaiting a response.

Condition 4 is the discriminator, and it is the reason the agent keeps a screen model rather than a ring of bytes. A build that is merely slow is exactly as quiet as a prompt; what separates them is that the build ended its last line with a newline, which leaves the cursor at column zero of a blank row, while `? Allow this edit? (y/n) ` leaves it against the question. A byte stream cannot answer where the cursor ended up, whether the program hid it, or whether the last thing drawn was a question or the tail of a progress bar. The three facts are read as one record under the screen's lock, because a cursor position sampled a moment after the visibility flag could describe a screen that never existed.

The second condition is structural rather than a check: a session exists in the registry only while its wrapper is connected, and the wrapper owns the child, so a sweep only ever sees live sessions.

Detection polls rather than reacting to output, because the event being detected is the *absence* of output and nothing arrives to announce it. The sweep is a few field reads per session on a one-second tick.

To keep it from being noisy: at most one notification per quiet episode, re-armed only when new output arrives; a session is never flagged within `MinimumUptime` of starting (default 5 s), since programs are quiet while they start; and `SessionClosed` fires its own separate "finished" notification carrying the exit code. The arming rule is load-bearing — without it, a prompt left unanswered overnight would notify once a second until morning, and a user who turns notifications off does not turn them back on.

Where sensitivity and silence conflict, the heuristic stays silent. A missed prompt costs the user a few minutes; a false one costs the feature, because a notification that fires when nothing is waiting teaches its recipient to ignore every notification after it. One accepted piece of noise: an idle full-screen editor on the alternate buffer has prompt-shaped posture and will be announced once. That is tolerable, since the hub only pushes when no client is attached.

Optional user-configured regexes (`(y/n)`, `Continue?`, `Press any key`) can force a match before the quiet period elapses. They are matched against the last non-blank line rather than the cursor's line, because prompts like `Press any key` are often followed by a newline. A match bypasses both the quiet period and the shape test, but still requires a visible cursor and the minimum uptime. Patterns are compiled with a 50 ms match timeout — they come from a file the user edits, and one catastrophically backtracking pattern must not stall the sweep for every session on the machine. A pattern that will not compile is logged and dropped; it costs the user that pattern, not the feature.

The heuristic is intentionally the primary mechanism, since prompt wording varies per tool and per version and a shipped pattern list would rot silently. Every number is a guess about somebody else's tools, so none of it is compiled in: `%LOCALAPPDATA%\1RemoteCLI\settings.json` supplies `quietPeriodSeconds`, `minimumUptimeSeconds`, `pollIntervalSeconds`, and `promptPatterns`, and `ONEREMOTE_QUIET_PERIOD_SECONDS` / `ONEREMOTE_MINIMUM_UPTIME_SECONDS` override the file. A malformed settings file is logged and ignored rather than fatal — losing every session on the machine over a stray comma costs far more than the setting being edited.

The notification carries a hint: the last non-blank line, trimmed and truncated. For a prompt that is the question itself, which is more use on a lock screen than the program's name — the user knows what they started, not what it decided to ask.

### 6.2 Delivery

Web Push with VAPID, via `Lib.Net.Http.WebPush`. Hand-rolling RFC 8291's ECDH and HKDF was rejected: getting the encryption subtly wrong yields a payload the browser silently discards, and there is no way to observe that from the server.

**Who gets woken.** The PWA subscribes through the service worker and offers the subscription to the hub with `RegisterPush`, which stores it against `UserKey` — never against the connection, since a phone gets a new connection every time it wakes. Subscriptions are keyed by endpoint within the user, because the endpoint is the browser's own identity for the subscription and re-registering must replace rather than accumulate; keyed any other way, an overnight phone would end up buzzing once per reconnect.

**When.** On terminal `SessionAwaitingInput`, structured-chat `SessionAttention(awaitingInput = true)`, and `SessionClosed`, and in every case only when that user has **no client attached to that session**. A chat permission uses the provider's explicit title rather than the terminal prompt heuristic. Buzzing about the session already open in the user's hand is how a person learns to ignore notifications, which costs the ones that matter. The attached-client count is read inside the same lock as the routing decision — a second query could see a different answer if somebody attached in the gap — and so `SessionAddress` carries it out of the registry alongside the machine and session names.

**Per-machine level.** The agent Settings tab offers three radio choices. *All attention events* is the default and preserves the behavior above. *Action required* sends waiting-for-input and structured permission pushes but suppresses completion/failure pushes. *Off* suppresses every Web Push originating from that machine. This preference filters only queued Web Push: live `SessionAwaitingInput`/`SessionAttention` fan-out and the session's stored attention state are unchanged, so an open PWA remains accurate at every level. The choice is stored in the agent's local preferences, included in every registration, updated live through the authenticated agent connection, and held separately on each machine record; changing one computer cannot silence another.

**Naming.** A session is named by its own display name if the agent gave it one, otherwise by its program. Never by its session id: "claude is waiting" is the whole message, where the id would mean nothing to somebody reading a lock screen.

**Perishability.** "Waiting for input" is sent with `Urgency: high` and a **10-minute TTL**; a question that has since been answered is a lie on a lock screen, and the push service should drop it rather than deliver it on wake. "Finished" gets an hour, since it stays true however late it arrives. Both carry a `Topic` — the first 22 characters of the base64url SHA-256 of the tag — so the push service itself collapses supersedable notifications for one session while the phone is offline, and a `tag` so the browser collapses them after delivery.

**Never inline.** SignalR processes one invocation at a time per connection, so a hub method that awaited a push service would let a slow third party stall every session on the reporting agent. `IPushNotifier.Enqueue` is deliberately `void`, backed by a bounded channel (512, `DropOldest`) drained by a background service. An unbounded queue would turn a push outage into hub memory growth; dropping the newest would discard the notification the user most needs.

**Expiry.** A 404 or 410 from the push service means the app was uninstalled or the subscription rotated, and the subscription is forgotten. Anything else is logged and swallowed — a notification is never worth failing a session over. A user whose last subscription goes is removed entirely, so the hub does not accumulate an entry per account that ever registered.

**Payload trust.** The payload is authenticated by nothing the browser checks: anyone holding the endpoint and keys can send one. The service worker therefore keeps only `pathname + search` from whatever URL arrives, and rejects anything that does not resolve to a leading `/` — `new URL` accepts `javascript:` and `data:` against any base and hands back an opaque path that would otherwise pass as relative. A notification tap can never leave this origin under this app's name and icon. For the same reason the service worker **always shows something**, even for a malformed payload: iOS revokes push permission from an app that receives a push and displays nothing, so a dropped notification costs every future one too.

**Tapping.** The deep link is `/?machine=…&session=…` — a query rather than a path, because the app is a single static page and a path would need a rewrite rule or a router. Tapped while the app is already running, the service worker posts `OPEN_SESSION` to the existing client instead of navigating; navigating would tear down the live socket and make the user wait through a reconnect to answer a question already on screen. The link is consumed on read and stripped from the address bar, so a later reload returns to the machine list rather than reopening a session the user deliberately left.

**Configuration.** `GET /push/vapid` returns the public key, unauthenticated — it is public by definition. It returns 404 when no keypair is configured, and the PWA reads that as "push is off" rather than as an error, so a hub running without keys degrades to a working app with no notifications instead of a broken one.

**iOS constraints**, since iPhone is the primary target: Web Push requires **iOS 16.4 or later** *and* the PWA to be installed to the home screen — Safari tabs cannot receive push. Permission must be requested from a user gesture, and any `await` before the request loses it. First-run onboarding therefore walks through *Share → Add to Home Screen*, then requests permission on an explicit tap and subscribes immediately, rather than waiting for the next reconnect.

Subscriptions live in the hub's memory, so a hub restart drops them until each PWA reconnects and re-registers — which it does on every connection, precisely so that recovers by itself. §9 records this.

---

## 7. Non-functional requirements

Every figure below is measured, not asserted. `NonFunctionalTests` drives the whole stack — a SignalR client, the real hub, the real agent, a real named pipe, a real pseudoconsole and a real program inside it — and prints an actual number for each row. The **Measured** columns are the output of that suite on 2026-08-14 (AMD EPYC 7763, Windows 11 26200, Release build). Rerunning it reproduces them.

Two things make the measurement honest rather than decorative:

- **The two latency legs are separated.** A round trip timed from outside is input latency plus output latency plus the program's own think time, so a regression in either leg can hide inside the total. The scripted CLI therefore stamps the instant a keystroke reached it (`E2E-TS <ticks>`); send-to-stamp is the input leg and stamp-to-frame is the output leg. Both processes share one machine clock, so the subtraction means something.
- **The assertions are looser than the targets, deliberately.** The targets describe a phone talking to a deployed hub; the suite runs on whatever machine checked the code out, including a shared two-core CI runner compiling something else at the time. Asserting the target exactly would make it the flakiest file in the repository and it would be deleted within a month. Each assertion allows three times the target — enough to survive a noisy runner, tight enough that an order-of-magnitude regression still fails. The printed figures, not the assertions, are the validation record.

### 7.1 Performance

| Metric | Target | Measured |
| :--- | :--- | :--- |
| Keystroke on phone → byte written to PTY | ≤ 60 ms p50, ≤ 150 ms p95, excluding network RTT | **0.8 ms p50, 1.0 ms p95** |
| PTY output → pixel on phone | ≤ 200 ms p50, ≤ 400 ms p95 on a good 4G link | **15.6 ms p50, 30.7 ms p95** (excluding network) |
| Snapshot delivered after attach | ≤ 500 ms p95 | **1.0 ms p95** |
| Output frame rate | ~30 Hz, adaptive down under backlog | **35.7 Hz** under a 2 000-line flood |
| Reconnect after network restore | ≤ 5 s | **0.61 s** after a full hub restart |

The output leg is dominated by the coalescing interval and nothing else — 15.6 ms is roughly half a frame period, which is what a uniformly-arriving byte costs when frames go out at 30 Hz. That is the floor for this design, and the remaining budget is all network. The flood measurement is the one that matters for the phone's radio: 2 000 lines of output arrived as 24 frames averaging 2 375 bytes, not as thousands of messages.

Recovery is measured to a stricter bar than "reconnected": the clock stops when a keystroke typed on a phone has reached the program, because an agent that is back on the hub but cannot yet carry input has not recovered.

### 7.2 Capacity

| Metric | Target | Measured |
| :--- | :--- | :--- |
| Users | 5 | — |
| Machines | 10 | — |
| Concurrent sessions | 20 | **20 real pseudoconsoles, all listed and responsive** |
| Concurrent clients per session | 2 | **2, both receiving the same output** |
| Agent memory | ≤ 40 MB base, ≤ 2 MB per session | **0.20 MB per session** |
| Hub memory | ≤ 250 MB | — |

Per-session agent memory is small precisely because only the visible screen is retained. The measured figure is the managed heap this test process grows by, which *over-counts*: it includes the hub, the wrappers and the test's own bookkeeping, none of which a shipped agent carries. An over-count that comes in at a tenth of budget is still a pass.

The two-clients row is asserted on both clients rather than one, because a second attach that stole the stream from the first would satisfy a test that only checked the newcomer.

The dashed rows are not measurable from a test process and are checked at release instead. Base memory for the agent and the hub is a working-set reading taken from the shipped binaries once they are running (§10, packaging); users and machines are counts of registrations, which cost a dictionary entry each and are bounded by the allowlist rather than by anything the code does.

### 7.3 Availability and data handling

The hub is a single instance with no redundancy; a restart drops all connections and clients reconnect automatically within seconds. Agents retry indefinitely with backoff.

Terminal content is **relayed but never persisted** — the hub holds bytes only for the instant it takes to forward them, and writes no terminal content to logs at any level. Screen state exists only in agent memory and dies with the session.

**How that guarantee is enforced.** "Never log terminal content" is not a rule anyone can keep by being careful; a single `_log.LogDebug($"got {text}")` added at 2 a.m. while chasing a framing bug undoes it, and nothing fails. So the logging vocabulary is closed rather than free-form. `Protocol/Diagnostics/LogEvents.cs` is the only vocabulary the product logs through: a set of `[LoggerMessage]` source-generated events with fixed compile-time templates and fixed parameter lists. There is no `Log(string message)` member, so there is no free-form string to interpolate a payload into, and no member accepts `byte[]`, a span, a screen, or a line of text. What gets logged about traffic is **sizes and sequence numbers** — which is what you actually need to debug framing and flow control anyway:

```
Relayed 512 bytes as seq 1841 for session s-7f2a.
Delivered 3 bytes of input to session s-7f2a.
```

Two deliberate exclusions. A session's **display name is not logged**, because it is a string the user typed and can hold anything; the *program* name is, because we chose that metadata and it is how you tell two sessions apart. And the general failure event takes an `Exception` rather than a message, so the call site has nothing to format.

Three tests hold the line, because there are three ways to break it:

| Test | Catches |
| --- | --- |
| `LogEventsTests` (reflection over the vocabulary) | Somebody *adding* an event that could carry a payload |
| `LogRedactionTests` (end-to-end canary) | Any component logging a payload through some other logger |
| `FileLoggerTests` | A formatter reintroducing content while rendering "for readability" |

The canary drives the real product — real pipe, real pseudoconsole, real hub — with a secret-shaped string flowing both directions through a session, captures every record from every sink at `Trace`, and asserts the string appears in none of them. It has been verified to fail when a payload log is deliberately added.

**Log files.** `%LOCALAPPDATA%\1RemoteCLI\logs\agent-YYYY-MM-DD.log`, one per day, fourteen days kept. The file is opened and closed per write rather than held, so it can be read, copied or deleted while the agent is running — a log you must stop the misbehaving process to collect is a log nobody ever sends you. `ONEREMOTE_LOG_LEVEL` turns it up (`trace`, `debug`/`verbose`, `info`, `warn`, `error`, `off`); an unrecognised value gives `Information` rather than refusing to start. The default is `Information`, not `Debug`, because `Debug` logs a line per relayed frame and would bury the one line that matters.

### 7.4 Security posture

Threats addressed: cross-user access (structural partitioning by `UserKey`), unauthorized sign-in (account allowlist), stale credentials (mid-connection token refresh), local privilege boundaries (pipe ACL restricted to the user's SID), remote code execution (attach-only — the capability does not exist), and machine spoofing (agent-generated GUID, partition-scoped lookup).

**Accepted risk — the relay is trusted.** Terminal output frequently contains secrets: API keys echoed into a shell, `.env` contents, private source. TLS protects it in transit, but the hub process sees plaintext. For a self-hosted hub run by the same small group that uses it, this is an acceptable trade against the complexity of end-to-end encryption. The protocol keeps `data` as an opaque byte payload that the hub never inspects, so an E2EE mode could be added later by encrypting that field between agent and client without changing message shapes.

**Accepted risk — full control equals full control.** An attached phone can type anything the keyboard could. Compromise of the Microsoft account compromises every live session on every paired machine. This is inherent to the product, mitigated by requiring MFA on the account and by the fact that sessions only exist while the user has one running at their desk.

---

## 8. Testing strategy

### 8.1 VT emulator — the critical component

Everything the user sees depends on the emulator being correct, so it carries the heaviest testing.

**Round-trip property test — the most important test in the system.** For an arbitrary VT byte stream:

```
stream ──► emulator A ──► screen state A
                             │ re-serialize
                             ▼
                       VT byte stream ──► fresh emulator B ──► screen state B

assert: A ≡ B   (cells, attributes, cursor, active buffer, title)
```

Run as a property test over generated streams and over recorded real traces from `claude`, `copilot`, `pwsh`, `vim`, `htop`, and a progress-bar-heavy `npm install`. Because snapshotting *is* re-serialization, this single property directly validates what the user sees on attach.

**Conformance and robustness.** A corpus of `vttest`/`esctest`-derived cases for cursor movement, erase operations, scroll regions, SGR (including 256-colour and true-colour), alternate screen switching, and resize. Fuzzing with random bytes, truncated sequences, and adversarially split chunks, asserting the parser never throws, never allocates unboundedly, and always returns to `GROUND`.

**Chunk-splitting invariance.** Feeding the same stream split at every possible boundary must produce identical screen state — this is what guarantees network chunking cannot corrupt rendering.

### 8.2 Other layers

**Agent and wrapper.** Integration tests over a real named pipe with a scripted child process: session open/close, exit-code propagation, input from both sources interleaving safely, resize propagation to `ResizePseudoConsole`, and cleanup of PTY handles on normal exit, crash, and kill. Flow control is tested with a deliberately slow consumer, asserting that a flooding session converges to a snapshot rather than growing an unbounded queue.

**Hub.** Authorization is the priority, because this product grants full control of a developer's machine and the hub is the only thing between an attacker and that. Token validation covers expired, wrong-audience, wrong-issuer (including a token from a *different* tenant that is otherwise perfectly valid, which a static issuer validator gets wrong), missing-scope, no-scope-at-all, unsigned, wrong-key, `alg: none`, missing `tid`, and missing `oid`. Isolation covers a client from user A addressing user B's machine **while holding valid ids for it** — one test per hub method, so a regression names the method that broke — an agent registering into another partition, the same machine id in two partitions, a refresh that changes `UserKey` aborting the connection, and a refresh with an unacceptable token being *refused but not fatal*. Plus registry lifecycle tests for agent disconnect, reconnect, and duplicate registration.

Projects (issue #110) get the same treatment rather than a smaller one, since a project is reachable through the identical partitioned methods everything else is: `ProjectStoreTests` covers the store in isolation — General auto-seed and its permanent first position and name, name/description/URL validation, case-insensitive uniqueness, ownership isolation between two users, restart round-trip persistence, corrupt-file backup, serialized atomic writes, and icon signature/set/read/clear/version-bump/cross-user-denial. `RelayRegistryTests` covers the session-label half — moving a session, moving back to General, a project assignment surviving an agent reconnect, refusing a move into another user's partition, the delete-time sweep reassigning affected sessions while leaving others alone (including sessions on offline machines), and the stale-project self-correction backstop. `RelayHubTests` covers the end-to-end shape — CRUD fan-out to every device, duplicate-name rejection, Bob unable to see/edit/delete Alice's projects, a new session defaulting to General, move validation and fan-out, and a project delete reassigning live sessions and announcing both the session update and the deletion. `AuthorizationTests`'s two structural tests (below) require no changes to cover the five new methods, since they inspect method bodies rather than enumerate a hand-kept list; only the cross-user theory needed one new case added by hand, for `SetSessionProject`.

Two of these are structural rather than behavioural, and they are the ones that will still be working in a year — a test that Bob cannot attach to Alice's session proves today's methods are safe and says nothing about the method somebody adds next month:

| Test | Enforces |
| --- | --- |
| `NoRequestTheHubAcceptsCanCarryAnIdentity` | No request type has a `UserKey`, `TenantId`, `ObjectId`, `Upn` or similar field, so no hub method — present or future — *can* read the caller's identity from the caller |
| `EveryHubMethodResolvesTheIdentityFromTheConnection` | Every method either calls `RequireUserKey()` or hands `Context.ConnectionId` to the registry. A method that routed by a machine id alone fails the build |

Both have been verified to fail when the mistake is deliberately introduced. The whole suite runs in CI on every push and pull request to `main`.

**PWA.** Unit tests for the accessory bar's key encoding (sticky modifiers, arrows, `Ctrl+C` → `0x03`), for viewport-to-columns/rows arithmetic, and for the relay client's connection lifecycle. `relay/projects.ts`'s reducers and `projectStats` grouping are covered the same way `relay/machines.ts` already was; `protocol/wire.contract.test.ts` decodes every new project message and the appended `SessionInfo.projectId` from the regenerated fixture, so nothing about the wire shape is asserted by hand.

### 8.3 End to end, from a browser

The layers above are each tested against a substitute for their neighbours. The end-to-end suite is the only place where nothing is substituted except the identity provider: one process holds a real hub, a real agent, real pseudoconsoles and the built PWA served from the same origin, and Playwright drives a phone-sized Chromium against it.

| Piece | Where | What it is |
| :--- | :--- | :--- |
| `tests/E2E.Host` | `1remote-e2e-host.exe` | Hub, agent, wrapper sessions and the built app on one port, plus a small control API (`/e2e/ready`, `/e2e/sessions`, `/e2e/sessions/{id}/size`) for the things a phone cannot do |
| `tests/E2E.Script` | `1remote-e2e-script.exe` | A deterministic CLI at the far end: colour and emphasis, a prompt, and single-key commands that report what the *program* sees — including the width the operating system is telling it about |
| `src/PWA/e2e` | Playwright specs | Seventeen scenarios: finding and attaching, snapshot restore, colour and emphasis, cross-user isolation, keystrokes and interrupts, resizing and rotation, losing the connection, and a session that ends |

Two things are worth stating plainly, because both are places where a test suite can quietly stop meaning anything.

**Sign-in is the one substitution.** `AuthAdapter` (`src/PWA/src/auth/adapter.ts`) has two implementations, and Vite swaps them by module alias when `VITE_E2E=1`. The stand-in reads a user name from the URL; the hub's `NameTokenHandler` turns that name into claims and then runs the *real* allowlist and the *real* `UserKey` derivation, so isolation is genuinely enforced rather than assumed. Signature validation is covered properly in `Hub.Tests`, at the level where it belongs. Automating a real Entra sign-in would need a service-account credential in CI and would depend on a flow Microsoft can change without notice. `src/PWA/tests/authBundle.test.ts` runs a real production build and fails if the stand-in appears in the output, so the claim that it cannot ship is checked rather than trusted.

**Assertions are made against the program, not the browser.** A resize that reflowed the browser's copy of the screen without reaching the pseudoconsole would look identical from the outside, so the resize tests ask the script how wide it believes it is. A snapshot of plain text would pass with every attribute dropped, so the styling test reads xterm's rendered classes, and was verified by removing bold from the re-serializer and watching it fail.

The suite runs in CI on `windows-latest` — the host targets `net8.0-windows` and opens ConPTY — with `retries: 0`, because a retry converts a flake into a pass and a flake is a defect.

### 8.4 Measuring the non-functional requirements

`NonFunctionalTests` is the only file in the repository whose job is to produce numbers rather than verdicts. It runs the same in-process stack as §8.2's end-to-end tests, alone in its own xunit collection — timing a keystroke while another test is starting a pseudoconsole on the next core measures the other test — and prints a figure for each row of §7. See §7 for the results and for why its assertions deliberately allow three times the target.

The one thing it needed that nothing else in the suite provides is a program that will say *when* it was typed at. The scripted CLI from §8.3 grew a `t` key for it, which prints `E2E-TS <ticks>` and nothing else. Without a stamp taken inside the receiving program, only the round trip is observable, and a round trip cannot tell you which half got slower.

### 8.5 Manual device matrix

iPhone Safari (installed to home screen, since push depends on it) as the primary target, then Android Chrome, covering orientation change, keyboard show/hide, backgrounding and resuming, and Wi-Fi ⇄ cellular handover.

---

## 9. Known limitations and deferred decisions

| Limitation | Consequence | Path forward |
| :--- | :--- | :--- |
| Agent runs as the interactive user | A machine is unreachable when nobody is logged on, or after a reboot until logon | Accepted. A service-plus-`CreateProcessAsUser` design could lift it, at the cost of the complexity §2.1 avoids |
| Sessions die with the wrapper | Closing the desk terminal ends the session | Deliberate for v1. A daemon-owned, tmux-style detachable session is the natural Phase 5 |
| No remote spawn | Cannot start work from the couch | Phase 5, behind a per-machine profile allowlist rather than free-form execution |
| Hub state is in memory, single instance | A restart drops connections; cannot scale out | Accepted at this scale. Scaling out requires a Redis backplane and a shared registry |
| Push subscriptions are not persisted | After a hub restart, notifications stop until each PWA is opened once | Small persistent store (a file or Azure Table) for subscriptions only |
| No scrollback on mobile | Cannot scroll back past the visible screen on the phone | Accepted; the desk terminal retains full scrollback |
| Relay sees plaintext | Hub operator can see terminal content | E2EE of the `data` field, additively |
| Existing terminals cannot be adopted | Must remember to type `1remote` | A shell function aliasing common tools would make it transparent |
| No audit log | No record of what was typed remotely | Optional per-machine local audit log |

---

## 10. Implementation roadmap

### Phase 1 — Vertical slice
End-to-end proof over the thinnest possible path: ConPTY wrapper with local tee and named-pipe transport; agent with session registry and hub connection; hub with full token validation, `UserKey` partitioning, and the account allowlist; PWA with sign-in, machine list, session list, and an `xterm.js` view. Raw byte passthrough, no emulator yet — enough to type on a phone into a real session.

### Phase 2 — The emulator
Headless VT parser and screen model; snapshot by re-serialization; snapshot on attach; the round-trip property test and the recorded-trace corpus. This is where the product becomes genuinely usable with TUIs.

### Phase 3 — Resilience and flow control
Frame coalescing and size caps; safe frame boundaries; the tail buffer and `seq`-based resume; re-snapshot-when-behind; automatic reconnect on both agent and client; mid-connection token refresh on both ends.

### Phase 4 — Mobile experience and notifications
Accessory key bar with sticky modifiers and the dedicated `Ctrl+C`; viewport-driven resize; PWA installability and the iOS add-to-home-screen onboarding; idle/prompt detection; Web Push with VAPID and deep links; the tray UI and the Scheduled Task installer.

### Phase 5 — Beyond v1
Guarded remote spawn from per-machine profiles; detachable sessions that survive the desk terminal; machine sharing between accounts; audit logging; optional end-to-end encryption.

---

## 11. Repository layout

```
/specs                    Design specifications (this document)
/docs                     Setup guides, Azure app registration walkthrough
/src
  /Daemon                 C# — produces 1remote.exe (agent, wrapper, login)
    /ConPty               P/Invoke wrapper for the pseudoconsole API
    /Terminal             Headless VT parser, screen model, re-serializer
    /Ipc                  Named pipe server and client
    /Hub                  SignalR client, framing, flow control
    /Tray                 Tray icon and settings window
    /Shell                Shortcut read/write, wrapping rules, file picker
    /Install              Autostart registration, Start menu, PATH
  /Hub                    C# — ASP.NET Core 8 + SignalR relay
    /Projects             Project store, persistence, icon files (issue #110)
    /Relay                Registry, hub methods, session labels
  /PWA                    React + Vite + Tailwind + xterm.js
/tests
  /Terminal.Tests         Emulator conformance, property, and fuzz tests
  /Daemon.Tests           Wrapper and agent integration tests
  /Hub.Tests              Authorization and registry tests
  /PWA.Tests              Unit and Playwright end-to-end tests
```

---

*End of specification.*
