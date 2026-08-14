# Manual device matrix

Automated tests do not cover software keyboards, backgrounding, or network handover — the three things most likely to be wrong. This is the walkthrough that does, and it has to be done on real hardware.

**Primary: iPhone, Safari, installed to the home screen.** Push depends on the home-screen install, so a tab is not a valid run.
**Secondary: Android, Chrome.** Gaps here are recorded as known issues rather than blocking.

Record results at the bottom of this file. A run is only meaningful against a stated build.

## Before you start

On the PC:

```powershell
1remote status                 # signed in
1remote agent                  # running, tray icon present
1remote --name "matrix" pwsh   # a session to attach to
```

Have `docs/troubleshooting.md` open. If a step fails, work out whether it is the device or the setup *before* recording a failure.

## The walkthrough

### Setup

- [ ] Sign in with the account on the allowlist.
- [ ] Install to the home screen (Share → Add to Home Screen on iOS; menu → Install app on Android).
- [ ] Open it from the home screen. The notifications card offers to turn them on.
- [ ] Grant notification permission. Card reads **Notifications are on**.

### Attach

- [ ] The machine appears, with `matrix` under it.
- [ ] Tap it. The screen appears immediately and **matches what is on the PC**, including colour and emphasis — not a blank terminal waiting for the next byte.
- [ ] Run something colourful at the desk and confirm it renders correctly on the phone.

### Typing

- [ ] Tap the screen; the keyboard opens; typed characters reach the program.
- [ ] Every accessory bar control: `Esc`, `Tab`, `⏎`, all four arrows, `^C`, `^D`, `^Z`.
- [ ] **More keys** opens the rest, and they work.
- [ ] Sticky modifiers: tap `Ctrl`, then `c` — the program is interrupted. The modifier applies to exactly one key and then clears.
- [ ] Start a long-running command at the desk and interrupt it from the phone.
- [ ] The program keeps running after the interrupt rather than the session dying.

### Layout

- [ ] Rotate to landscape. Content reflows; nothing is clipped or hidden behind a notch.
- [ ] Rotate back.
- [ ] Show and hide the keyboard in both orientations. The screen is never left behind the keyboard.
- [ ] Detach and confirm the terminal at the desk returns to its previous shape.

### Backgrounding — the one that matters

iOS suspends the connection. The failure to hunt is **a stale screen that still looks live**, because the user will type into it.

- [ ] Background the app for 10 minutes with the session active.
- [ ] While it is backgrounded, produce output at the desk.
- [ ] Return to the app. It must either show *Reconnecting* or show the **current** screen. It must never show the old screen as though it were live.
- [ ] Type immediately on return. Either the keystroke lands, or it is refused visibly. It must not be silently swallowed.
- [ ] Repeat with 30 minutes.

### Notifications

- [ ] Lock the phone.
- [ ] At the desk, trigger a prompt — start something that asks a question and leave it waiting.
- [ ] The notification arrives. Note how long it took.
- [ ] Tapping it opens the app **on that session**, attached, showing the question.
- [ ] Answer it from the lock-screen-launched app and confirm the program proceeds.

### Network

- [ ] Mid-session, turn Wi-Fi off to force a handover to cellular. It recovers on its own.
- [ ] Turn Wi-Fi back on. It recovers again.
- [ ] Ride a lift, or use Airplane mode for 30 seconds. *Reconnecting* appears and then clears.
- [ ] On a deliberately poor connection, flood the session (`dir /s`, or a noisy build). **Lag must converge** — the screen catches up to current. An ever-growing delay is a flow-control bug and is the failure to look for here.

## Results

Copy this block per run.

```
Build:      <1remote --version> / hub <version from /health>
Date:
Device:     e.g. iPhone 15 Pro, iOS 18.5, Safari, home screen
Result:     pass / pass with notes / fail
Notes:
```

### Runs

_None recorded yet. This needs real hardware — see [#38](https://github.com/eranyariv/1RemoteCLI/issues/38)._

## Known device quirks

_Add anything found above that is a property of the device rather than a bug to fix._
