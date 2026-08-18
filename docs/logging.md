# Logs

The agent runs unattended under a scheduled task with no console. When your phone stops seeing a machine, the log file is the only account of what happened.

## Where

```
%LOCALAPPDATA%\1RemoteCLI\logs\agent-YYYY-MM-DD.log
```

The settings window's **Open logs** goes straight there. One file per day, fourteen days kept, oldest pruned on startup and at each date rollover.

You can read, copy or delete the file **while the agent is running** — it is opened and closed per write rather than held open. Attach the last day or two to a bug report.

## Turning it up

```powershell
setx ONEREMOTE_LOG_LEVEL debug
```

Then restart the agent (tray → *Quit*, then run it again, or `schtasks /Run /TN "1RemoteCLI Agent"`). Accepted: `trace`, `debug` (or `verbose`), `info`, `warn`, `error`, `off`. Anything unrecognised falls back to `info` rather than refusing to start.

`Debug` adds a line per relayed frame — useful for framing and flow-control bugs, far too noisy to leave on. Turn it back down with `setx ONEREMOTE_LOG_LEVEL info`.

`ONEREMOTE_LOG_DIR` moves the folder. Mostly for tests.

## What a line looks like

```
2026-08-14 02:40:19.067 +03:00  info  Agent[1400]  Listening on pipe 1remotecli-agent-S-1-12-1-3479607529.
2026-08-14 02:40:19.340 +03:00  WARN  Hub[1101]   Not signed in, so this machine is not reachable. Run '1remote login'.
2026-08-14 02:40:19.342 +03:00  info  Hub[1002]   Reconnecting to the hub in 30s.
```

Local time with offset, fixed-width level, category, and an **event id in brackets**. The id is stable and greppable — `[1101]` is always "not signed in", regardless of how the wording changes.

| Range | About |
| --- | --- |
| 1000–1099 | Hub connection: connected, disconnected, reconnecting, refused |
| 1100–1199 | Sign-in and tokens |
| 1200–1299 | Machine and session registration |
| 1300–1399 | Attach, detach, relay, input |
| 1400–1499 | The local named pipe and its wrappers |
| 1900+ | Failures and refusals |

## What is never in there

**No terminal content, at any level, ever.** Not output, not input, not the screen, not the session's display name. This is enforced by the closed logging vocabulary in `src/Protocol/Diagnostics/LogEvents.cs` and by three tests, not by discipline — see §7.3 of the design spec.

Traffic is logged as sizes and sequence numbers:

```
Relayed 512 bytes as seq 1841 for session s-7f2a.
Delivered 3 bytes of input to session s-7f2a.
```

Which means a log is safe to paste into an issue, and also that a log will never tell you *what* was on screen — only how much of it moved and when. That is the intended trade.

## Common lines and what to do

| Line | What it means |
| --- | --- |
| `[1101] Not signed in` | Run `1remote login`. Expected at boot before anyone signs in. |
| `[1003] The hub refused this machine` | The account is not on the hub's allowlist, or the token lacks the scope. |
| `[1002] Reconnecting to the hub in 30s` | Normal while offline. Backs off; retries indefinitely. |
| `[1102] Could not renew the access token` | The cached credential expired and silent refresh failed. Run `1remote login`. |
| `[1303] Dropped N bytes` | A client fell too far behind and will be resynchronised. Harmless unless constant. |
