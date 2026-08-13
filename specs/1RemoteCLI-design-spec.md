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

1. **Attach, don't spawn.** v1 can only attach to sessions you started yourself at the keyboard. Nothing can be launched from the phone. This eliminates remote code execution as an attack surface rather than trying to contain it.
2. **The screen is the state.** A terminal's meaningful state is its visible screen, not its output history. The agent runs a headless VT emulator and sends a *screen snapshot*, not a replay of bytes.
3. **Boring infrastructure.** One process per machine, one hub instance, no database, no message broker. The system should be comprehensible in an afternoon.
4. **The user is the security boundary.** One Microsoft identity owns machines, sessions, and clients. Cross-user access is structurally impossible, not merely checked.

### 1.2 Scope

**In scope for v1:** attaching to running sessions from a phone, full interactive control, reconnection across network changes, push notification when a session needs attention, multiple machines per user.

**Out of scope for v1:** launching processes remotely, file transfer, scrollback history on mobile, sharing a machine with another person, session persistence across a closed desk terminal, end-to-end encryption.

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
│   └────────────────────────────────────────────────┘                       │
└────────────────────────────────────────────────────────────────────────────┘
```

### 2.1 Why a tray EXE and not a Windows Service

A Windows Service runs in session 0 as `LocalSystem`. Child processes would therefore have no access to the user's `%USERPROFILE%`, git credentials, SSH keys, or the per-user logins that `claude.exe` and `copilot.exe` depend on — and a DPAPI blob written under `CurrentUser` scope during an interactive login could not be decrypted by `LocalSystem` at boot.

The agent is therefore an ordinary Win32 executable running as the interactive user, started at logon by a Scheduled Task, with no console window and a system tray icon. It inherits the user's full environment for free. The cost is that a machine is only reachable while that user is logged on; this is acceptable and is documented as a known limitation (§9).

### 2.2 Why attach-only

Remote spawn means arbitrary executables, arbitrary arguments, and an arbitrary working directory, chosen from a phone. Whoever phishes the Microsoft account owns every paired machine. Containing that requires an allowlist, per-machine consent, audit logging, and a second factor — significant machinery guarding a capability that the primary use case does not need. The motivating scenario is *"I started Claude Code and then walked away"*, which attach-only serves completely.

**Constraint:** Windows cannot retroactively attach a pseudoconsole to a process that is already running under a different console. A session must be *born* under the wrapper to be attachable; an existing Windows Terminal tab cannot be adopted.

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

The wrapper does **not** parse VT sequences, hold a screen model, or talk to the network. All remote-facing logic lives in the agent, in one place.

### 4.2 Tray agent (`1remote agent`)

One per machine. No console window; a tray icon shows connection state and offers *Sign in*, *Show sessions*, *Open logs*, and *Quit*.

**Machine identity.** On first run the agent generates a GUID and persists it to `%LOCALAPPDATA%\1RemoteCLI\machine.json` along with a friendly display name (defaulting to the computer name, user-editable). The GUID — not the computer name — is the `machineId`. Computer names are neither unique nor unforgeable.

**Named pipe server.** `\\.\pipe\1remotecli-agent-{user-sid}`, with a security descriptor granting access **only to the current user's SID**. Without this ACL any local process, including one running as a different user on a shared machine, could inject keystrokes into a live session. The SID is embedded in the pipe name so that two users logged on to the same machine each get their own agent and their own pipe.

**Autostart.** A Scheduled Task registered at install: trigger *At log on* for the current user, action `1remote.exe agent`, "Run only when user is logged on", hidden, no execution time limit, and *not* stopped on battery or idle. A `Run` registry key is the fallback if task registration fails.

**Responsibilities.** Session registry, one headless VT emulator per session, hub connection and authentication, output framing and flow control, idle/prompt detection, and routing input from the hub to the correct wrapper pipe.

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

**Deployment target.** All Azure resources — the hub, its App Service or Container Apps environment, and the Entra app registration of §3.1 — live in the **Azure subscription owned by `owner@example.com`** (Azure Enterprise account). No secrets, subscription identifiers, or tenant identifiers are stored in the repository; runtime configuration comes from App Service settings or Key Vault, and local development uses .NET user-secrets.

**Registry.**

```
UserKey ─┬─ Machines: machineId → { connectionId, displayName, os, lastSeen }
         │                          └─ Sessions: sessionId → { program, args, cwd,
         │                                                     cols, rows, startedAt }
         └─ Clients:  connectionId → { attachedTo: (machineId, sessionId)? }
```

Everything is reconstructed by agents and clients reconnecting after a restart. No database (§9 notes the one real cost of this: push subscriptions).

**Routing.** Every hub method resolves `UserKey` from the connection's principal, looks up the target inside that partition only, and rejects with an `Error` message if the target is absent. Since a machine that does not belong to the caller is not in the caller's partition, a spoofed `machineId` finds nothing.

**Liveness.** SignalR keep-alive at 15 seconds, client timeout at 30 seconds. A dropped agent connection marks the machine offline and notifies that user's attached clients; its sessions are removed, since sessions cannot outlive their wrapper. Both numbers live in one place and are applied to the hub and to the end-to-end harness alike, so the hub a test exercises behaves like the hub that is deployed on exactly the axis those numbers govern.

### 4.8 Mobile PWA

React + Vite + Tailwind, `@xterm/xterm` with `@xterm/addon-fit` and `@xterm/addon-web-links`, and a service worker for installability and Web Push.

**Screens.** A machine list (online/offline, session counts); a session list per machine (program, working directory, uptime, "waiting for input" badge); and the terminal view.

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

**Sizing.** The phone is authoritative while attached: on attach, and on orientation change, the client computes columns and rows from the viewport and sends `ResizeTerminal`, which reshapes the real PTY. The desk terminal window does not resize in response, so a program will render to the phone's narrower width in a wider desktop window until the phone detaches. This is the accepted trade: a correctly reflowed phone view is worth a temporarily odd-looking desk window, and TUIs handle `SIGWINCH`-equivalent resizes natively. On detach the client hands the session back the shape it had at the desk, so walking away does not leave a 45-column program stranded inside a wide window until somebody drags the corner. Resizes are debounced, so the keyboard animating open produces one message rather than thirty.

**The visible area is not the layout viewport.** On iOS, opening the software keyboard does not shrink the layout viewport at all — the page is scrolled up inside a smaller *visual* viewport. A full-height fixed element therefore keeps its full height and hides its own bottom behind the keyboard, and the bottom is the accessory bar and the last lines of output: the two things somebody opened the app to reach. The terminal view consequently measures `visualViewport` and pins itself to an explicit pixel height, translated down by the scroll, rather than using the `inset-0` that would be the obvious thing and is wrong on exactly the device this product is for. Sub-pixel drift is rounded away, because iOS reports fractional heights that wobble through the whole keyboard animation.

**Connection handling.** SignalR automatic reconnect with backoff; on reconnect, reattach with the last `seq`. The status bar distinguishes *live*, *reconnecting*, and *session ended*.

---

## 5. Protocol specification

Three protocols: the local pipe (wrapper ↔ agent), and the hub protocol in its agent and client halves. All hub traffic is MessagePack over WebSockets, secured by TLS.

### 5.1 Local pipe protocol (wrapper ↔ agent)

Length-prefixed MessagePack frames over the named pipe.

| Direction | Message | Payload |
| :--- | :--- | :--- |
| W → A | `SessionOpened` | `program`, `args[]`, `cwd`, `cols`, `rows`, `displayName?` |
| A → W | `SessionAccepted` | `sessionId` |
| W → A | `Output` | `bytes` — raw PTY output |
| W → A | `SessionClosed` | `exitCode` |
| A → W | `Input` | `bytes` — to write to the PTY |
| A → W | `Resize` | `cols`, `rows` |
| A → W | `Interrupt` | — sends `0x03` |

### 5.2 Agent ↔ hub

**Agent → hub**

```json
{ "target": "RegisterMachine",
  "arguments": [{
    "machineId": "6f9a1c22-4d18-4b0e-9d3a-2a7e5b0c81f4",
    "displayName": "Primary Dev Workstation",
    "os": "Microsoft Windows 11 Pro 10.0.26100",
    "agentVersion": "1.0.0",
    "protocolVersion": 1
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
    "startedAt": "2026-08-13T15:22:04Z"
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

Also: `SessionClosed { sessionId, exitCode }`, `SessionAwaitingInput { sessionId, hint }`, `RefreshToken { token }`.

**Hub → agent**: `AttachRequested { sessionId, clientConnectionId, cols, rows, lastSeq? }`, `DetachRequested { sessionId, clientConnectionId }`, `SendInput { sessionId, data }`, `ResizeTerminal { sessionId, cols, rows }`, `InterruptSession { sessionId }`, `TokenExpiring { expiresAt }`.

### 5.3 Client ↔ hub

**Client → hub**: `ListMachines {}`, `AttachSession { machineId, sessionId, cols, rows, lastSeq? }`, `DetachSession { sessionId }`, `SendInput { sessionId, data }`, `ResizeTerminal { sessionId, cols, rows }`, `InterruptSession { sessionId }`, `RegisterPush { endpoint, keys }`, `RefreshToken { token }`.

**Hub → client**: `MachineList { machines[] }`, `MachineOnline / MachineOffline { machineId }`, `SessionOpened / SessionClosed { machineId, session }`, `TerminalOutput { sessionId, seq, kind, data }`, `SessionAwaitingInput { machineId, sessionId }`, `TokenExpiring { expiresAt }`, `Error { code, message, sessionId? }`.

`SendInput` is a pure passthrough: `data` is the exact byte sequence the terminal should receive — `"y\r"`, `"\u001b[A"` for cursor-up, `"\u0003"` for `Ctrl+C`. The hub never interprets it, which keeps the phone's input indistinguishable from the keyboard's.

### 5.4 Versioning

`RegisterMachine` and the client handshake both carry `protocolVersion`. The hub rejects an unsupported version with a clear `Error` telling the user to update, rather than failing in an obscure way at the first incompatible message.

---

## 6. Notifications

The highest-value feature after attach itself: knowing that a CLI is waiting for you, without keeping the PWA open.

### 6.1 Detecting "waiting for input"

Windows offers no signal for "this process is blocked on a console read", so detection is heuristic. The agent raises `SessionAwaitingInput` when **all** of the following hold:

1. No output for `QuietPeriod` seconds (default 8, configurable).
2. The child process is still running.
3. The cursor is visible.
4. The screen looks like it is waiting: the cursor sits at the end of a non-empty line, on a line that does not end in a newline — the shape of a prompt awaiting a response.

To keep it from being noisy: at most one notification per quiet episode, re-armed only when new output arrives; a session is never flagged within the first few seconds of starting; and `SessionClosed` fires its own separate "finished" notification carrying the exit code.

Optional user-configured regexes (`(y/n)`, `Continue?`, `Press any key`) can force a match before the quiet period elapses. The heuristic is intentionally the primary mechanism, since patterns vary per tool and per version.

### 6.2 Delivery

Web Push with VAPID. The PWA subscribes via the service worker and sends its subscription to the hub with `RegisterPush`; the hub pushes to every subscription belonging to that `UserKey` when a `SessionAwaitingInput` arrives and that user has no client currently attached to the session. The notification deep-links straight into the session view.

**iOS constraints**, since iPhone is the primary target: Web Push requires **iOS 16.4 or later** *and* the PWA to be installed to the home screen — Safari tabs cannot receive push. Permission must be requested from a user gesture. First-run onboarding therefore walks through *Share → Add to Home Screen*, then requests notification permission on an explicit tap.

Subscriptions live in the hub's memory, so a hub restart drops them until each PWA reconnects and re-registers. §9 records this.

---

## 7. Non-functional requirements

### 7.1 Performance

| Metric | Target |
| :--- | :--- |
| Keystroke on phone → byte written to PTY | ≤ 60 ms p50, ≤ 150 ms p95, excluding network RTT |
| PTY output → pixel on phone | ≤ 200 ms p50, ≤ 400 ms p95 on a good 4G link |
| Snapshot delivered after attach | ≤ 500 ms p95 |
| Output frame rate | ~30 Hz, adaptive down under backlog |
| Reconnect after network restore | ≤ 5 s |

### 7.2 Capacity

| Metric | Target |
| :--- | :--- |
| Users | 5 |
| Machines | 10 |
| Concurrent sessions | 20 |
| Concurrent clients per session | 2 |
| Agent memory | ≤ 40 MB base, ≤ 2 MB per session |
| Hub memory | ≤ 250 MB |

Per-session agent memory is small precisely because only the visible screen is retained.

### 7.3 Availability and data handling

The hub is a single instance with no redundancy; a restart drops all connections and clients reconnect automatically within seconds. Agents retry indefinitely with backoff.

Terminal content is **relayed but never persisted** — the hub holds bytes only for the instant it takes to forward them, and writes no terminal content to logs at any level. Screen state exists only in agent memory and dies with the session.

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

**Hub.** Authorization is the priority: expired, wrong-audience, wrong-issuer, missing-scope, and non-allowlisted tokens are all rejected; a client from user A cannot address user B's machine even when given a valid `machineId`; a token refresh that changes `UserKey` aborts the connection; a connection whose token expires without refresh is dropped. Plus registry lifecycle tests for agent disconnect, reconnect, and duplicate registration.

**PWA.** Unit tests for the accessory bar's key encoding (sticky modifiers, arrows, `Ctrl+C` → `0x03`) and for viewport-to-columns/rows arithmetic. Playwright end-to-end tests against a local hub and agent driving a scripted CLI: attach, see the snapshot, answer a prompt, resize, interrupt, and survive a simulated disconnect.

**Manual device matrix.** iPhone Safari (installed to home screen, since push depends on it) as the primary target, then Android Chrome, covering orientation change, keyboard show/hide, backgrounding and resuming, and Wi-Fi ⇄ cellular handover.

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
    /Tray                 Tray icon, autostart registration
  /Hub                    C# — ASP.NET Core 8 + SignalR relay
  /PWA                    React + Vite + Tailwind + xterm.js
/tests
  /Terminal.Tests         Emulator conformance, property, and fuzz tests
  /Daemon.Tests           Wrapper and agent integration tests
  /Hub.Tests              Authorization and registry tests
  /PWA.Tests              Unit and Playwright end-to-end tests
```

---

*End of specification.*
