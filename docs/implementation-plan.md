# 1RemoteCLI — v1 Implementation Plan

Companion to [`specs/1RemoteCLI-design-spec.md`](../specs/1RemoteCLI-design-spec.md). The spec says *what* v1 is; this plan says *how we get there and in what order*.

**Definition of done for v1:** from an iPhone, on cellular, sign in with a Microsoft account, see a Windows machine and the `claude` session running on it, read the current screen, answer a `(y/n)` prompt, send `Ctrl+C`, survive a Wi-Fi-to-5G handover, and get a push notification when a session goes quiet at a prompt — with the desk terminal behaving exactly as it did before.

---

## Sequencing philosophy

Two rules shape the ordering.

**Prove the riskiest path first.** Stage 1 is a deliberately thin but *complete* vertical slice — every component, no polish, raw byte passthrough with no terminal emulator. If ConPTY, the pipe, the hub, auth and `xterm.js` cannot be made to talk to each other, we want to know in week one, not after building a VT emulator.

**Then build the thing that carries the product.** Stage 2 is the headless VT emulator. It is the single most technically demanding and most load-bearing component: it is what turns "raw bytes on a screen" into something that survives an attach mid-session. It gets its own stage and the heaviest test investment.

Stages 3–5 are then largely additive and can absorb schedule pressure without invalidating anything before them.

```
Stage 0  Foundations
   │
Stage 1  Vertical slice ────────────► first demo: type from a phone
   │
Stage 2  VT emulator ───────────────► first genuinely usable build
   │
Stage 3  Resilience & flow control ─► survives real networks
   │
Stage 4  Mobile UX & notifications ─► feature complete
   │
Stage 5  Hardening & release ───────► v1
```

---

## Stage 0 — Foundations

**Goal:** every component has a home, a build, and a deployable skeleton. No behaviour yet.

| # | Task | Notes |
| :-- | :--- | :--- |
| 0.1 | Scaffold the monorepo | .NET 8 solution, `src/Daemon`, `src/Hub`, `src/PWA`, `tests/*`, per spec §11 |
| 0.2 | Entra app registration | `common` endpoint, `Session.Access` scope, SPA + loopback redirect URIs; documented in `docs/` so it is reproducible |
| 0.3 | Shared protocol contracts | Message DTOs and `protocolVersion` in one library referenced by agent and hub; PWA types generated or mirrored |
| 0.4 | Hub skeleton deployed | Health endpoint live in the target Azure subscription, proving the deploy path early |

**Exit criteria:** `dotnet build` and `npm run build` both succeed; the hub's health endpoint answers over HTTPS in Azure.

---

## Stage 1 — Vertical slice

**Goal:** type a character on a phone and watch it appear in a real PowerShell session on a desktop. No emulator, no snapshot, no reconnect — raw bytes only.

| # | Task | Depends on |
| :-- | :--- | :--- |
| 1.1 | ConPTY P/Invoke wrapper — create, resize, close, handle lifetime | 0.1 |
| 1.2 | Wrapper CLI — spawn child under the PTY, tee to local console, raw-mode save/restore, exit-code propagation | 1.1 |
| 1.3 | Named-pipe IPC — framed MessagePack, ACL scoped to the user SID | 0.1 |
| 1.4 | Agent skeleton — machine identity GUID, session registry, pipe server | 1.3 |
| 1.5 | `1remote login` — MSAL loopback flow, DPAPI-encrypted token cache | 0.2 |
| 1.6 | Hub token validation — dynamic issuer, `aud`, `scp`, `tid`+`oid` → `UserKey`, account allowlist | 0.2, 0.4 |
| 1.7 | Hub relay — SignalR hub, in-memory registry, partition-scoped routing | 1.6 |
| 1.8 | Agent hub client — register machine, session lifecycle, raw output forwarding | 1.4, 1.5, 1.7 |
| 1.9 | PWA sign-in and lists — MSAL, machine list, session list | 0.2, 1.7 |
| 1.10 | PWA terminal view — `xterm.js`, attach, render raw stream, send input | 1.9 |

**Exit criteria:** `1remote pwsh` at the desk, attach from a phone browser, type `dir`, see output on both the phone and the desk terminal. A second Microsoft account signed into the same hub sees nothing.

**Known-ugly at this stage, by design:** attaching mid-session shows a blank screen until new output arrives; a network blip loses the session view; a flood of output is forwarded verbatim.

---

## Stage 2 — Headless VT emulator

**Goal:** attach to a session that has been running for an hour and immediately see exactly what is on the desk screen.

| # | Task | Notes |
| :-- | :--- | :--- |
| 2.1 | VT parser state machine | Williams DFA — byte-oriented, immune to chunk splits |
| 2.2 | Screen model | Cell grid with SGR, cursor + saved cursor, DEC modes, alternate buffer, title |
| 2.3 | Snapshot re-serializer | Screen state → VT byte stream, so the client needs no snapshot decoder |
| 2.4 | Emulator test suite | Round-trip property test, conformance corpus, fuzz, chunk-split invariance, recorded real traces |
| 2.5 | Integrate into the agent | Snapshot on attach, `kind: snapshot \| delta`, plus resize plumbing end to end |

**Exit criteria:** attaching mid-`vim`, mid-`htop`, and mid-`claude` renders the correct screen within 500 ms; the round-trip property test passes over the full recorded-trace corpus; resizing on the phone reflows the running program.

**Highest-risk task:** 2.3. Re-serialization must reproduce *semantics*, not bytes. Task 2.4's round-trip property is what makes this tractable — it turns a vague "does it look right?" into a mechanical assertion, so 2.4 is written alongside 2.3, not after it.

---

## Stage 3 — Resilience and flow control

**Goal:** the product stops falling over on a real mobile network.

| # | Task | Notes |
| :-- | :--- | :--- |
| 3.1 | Framing | 30 Hz coalescing, 24 KB cap, boundaries only in parser `GROUND` state, MessagePack binary |
| 3.2 | Sequencing and resume | Per-session `seq`, 256 KB tail buffer, delta-or-snapshot on reattach |
| 3.3 | Backpressure | Discard the queue and re-snapshot when a client falls behind — the payoff of the screen-state model |
| 3.4 | Auto-reconnect | Backoff on both agent and PWA, reattach with last `seq`, honest connection status in the UI |
| 3.5 | Token refresh and liveness | `TokenExpiring`/`RefreshToken`, `UserKey` immutability across refresh, abort on expiry; machine-offline cleanup |

**Exit criteria:** `npm install` in an attached session keeps the phone responsive and never queues unboundedly; toggling airplane mode for 30 seconds recovers to a correct screen automatically; a connection whose token expires without refresh is dropped.

---

## Stage 4 — Mobile experience and notifications

**Goal:** feature complete — the thing you would actually reach for.

| # | Task | Notes |
| :-- | :--- | :--- |
| 4.1 | Accessory key bar | Sticky `Ctrl`/`Alt`, arrows, `Tab`, `Esc`, `Enter`, dedicated red `Ctrl+C` → `0x03` |
| 4.2 | Viewport-driven resize | Cols/rows from viewport, orientation change, phone-wins policy |
| 4.3 | Installability | Manifest, service worker, iOS add-to-home-screen onboarding (a hard prerequisite for push) |
| 4.4 | Idle/prompt detection | Quiet-period heuristic plus cursor/screen shape; optional user regexes; debounced |
| 4.5 | Web Push | VAPID, `RegisterPush`, hub push on `SessionAwaitingInput`, deep link into the session; session-ended notification |
| 4.6 | Tray UI and autostart | Tray icon and menu; Scheduled Task at logon with `Run` key fallback; `install`/`uninstall` |

**Exit criteria:** with the PWA closed on a locked iPhone, a `claude` prompt produces a notification that deep-links into the session, where it can be answered in two taps. A reboot plus logon brings the machine back online with no manual step.

---

## Stage 5 — Hardening and release

**Goal:** ship it.

| # | Task | Notes |
| :-- | :--- | :--- |
| 5.1 | Logging and redaction | Structured logs; assert no terminal content is ever logged at any level |
| 5.2 | Hub authorization test suite | Expired, wrong `aud`/`iss`, missing `scp`, non-allowlisted, cross-user addressing, `UserKey` change on refresh |
| 5.3 | Playwright end-to-end | Attach, snapshot, answer prompt, resize, interrupt, simulated disconnect |
| 5.4 | NFR validation | Latency, memory, and flood-convergence targets from spec §7 measured, not assumed |
| 5.5 | Device matrix | iPhone Safari installed to home screen (primary), then Android Chrome |
| 5.6 | Release | Production hub deploy, allowlist configuration, packaging, setup documentation |

**Exit criteria:** spec §7 targets met and recorded; the security tests in 5.2 all pass; a colleague can go from zero to an attached session using only `docs/`.

---

## Critical path and parallelism

The long pole runs **0.1 → 1.1 → 1.2 → 1.4 → 1.8 → 2.5 → 3.1 → 3.3**: the terminal data path. Everything else can be worked around it.

Genuinely parallel tracks:

- **Auth track** (0.2, 1.5, 1.6, 3.5, 5.2) touches almost none of the data path.
- **PWA track** (1.9, 1.10, 4.1, 4.2, 4.3) can develop against a stub hub that replays a recorded trace, well before the agent is finished.
- **Emulator track** (2.1–2.4) is pure computation over byte streams with no dependency on ConPTY, the pipe, or the network. It can start the moment there are recorded traces to test against — so **capture trace fixtures during Stage 1** to unblock it.

## Principal risks

| Risk | Mitigation |
| :--- | :--- |
| VT re-serialization is subtly wrong, so attach shows a plausible-but-incorrect screen | The round-trip property test (2.4) written alongside the serializer, plus real recorded traces rather than synthetic ones |
| iOS Web Push constraints (16.4+, must be installed to home screen) sink the notification feature | Validate push on a real iPhone during 4.3, before building 4.4 and 4.5 on top of it |
| Mobile latency makes interaction feel bad despite meeting throughput targets | Measure keystroke-to-echo early, in Stage 1, on a real cellular link — not at the end |
| Sticky modifiers and the software keyboard fight each other on iOS | Prototype 4.1 on a real device early; it is the part most likely to need redesign after contact |
| Attach-only proves too restrictive in daily use | Deliberate; revisit for Phase 5 remote spawn only with real usage evidence |

## Explicitly not in this plan

macOS agent support and desktop coding-agent chat sessions are tracked separately and are **not** v1 scope. Both are architecturally significant enough that the abstractions they need are noted in the spec, but neither should be allowed to expand v1.
