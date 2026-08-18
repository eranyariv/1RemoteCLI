# 1RemoteCLI

**Answer your terminal from your phone.**

You start `claude` on a long refactor, leave your desk, and twenty minutes later it is sitting on *"Do you want me to apply these changes? (y/n)"* — and has been for nineteen of them.

1RemoteCLI attaches your phone to a terminal session already running on your Windows machine. Read the output, answer the prompt, send Ctrl+C, walk away again. Your phone buzzes when a session needs you.

```powershell
1remote claude          # exactly like running claude, but now visible from your phone
```

## Try it

Run this **from inside Copilot CLI or Claude Code**, as a shell command — in Copilot CLI that means prefixing it with `!`:

```powershell
irm https://raw.githubusercontent.com/eranyariv/1RemoteCLI/main/scripts/install.ps1 | iex
```

Not from a plain PowerShell window. Windows refuses to run executables it has no reputation for, and these builds are unsigned, so a plain install is likely to fail with `Access is denied` — on a work machine, permanently. A file written by your coding CLI inherits its trust and runs immediately. You already have one of those CLIs installed; putting their sessions on your phone is what this is for.

No account and no token. The script picks the build for your architecture and checks it against the SHA-256 published with the release.

Then, in a new terminal:

```powershell
1remote login           # sign in
1remote claude          # share a session
```

Then open the app on your phone and tap the session. Full walkthrough: **[docs/getting-started.md](docs/getting-started.md)**.

## Documentation

| | |
| --- | --- |
| [Getting started](docs/getting-started.md) | Install, sign in, first session |
| [Using it from your phone](docs/phone-setup.md) | Home screen, notifications, driving a session |
| [Troubleshooting](docs/troubleshooting.md) | When it does not work |
| [Security](docs/security.md) | **Read before pointing it at a machine that matters** |
| [Deployment](docs/deployment.md) · [Azure setup](docs/azure-setup.md) | Running the service |
| [Design spec](specs/1RemoteCLI-design-spec.md) | How and why it works the way it does |

## How it works

```mermaid
flowchart LR
    subgraph pc["Your Windows PC"]
        w["1remote claude<br/>wrapper + ConPTY"] <-->|named pipe| a["1remote agent<br/>one per machine"]
    end
    a <-->|"WebSocket / TLS"| h["Hub<br/>Azure App Service"]
    h <-->|"WebSocket / TLS"| p["Phone<br/>PWA"]
    h -.->|"Web Push"| p
```

The wrapper runs your program under a real ConPTY, so it behaves exactly as it would unwrapped — same colours, same keys, same exit code. The agent keeps a headless VT emulator per session, which is what lets a phone attach mid-flight and immediately see the current screen instead of a blank terminal.

Both sides dial out to the hub, so nothing on your PC has to be reachable from the internet and no ports are opened. The hub is a stateless relay; the phone app is served from the same origin.

## Building it

```powershell
dotnet build 1RemoteCLI.slnx        # wrapper, agent, hub
dotnet test 1RemoteCLI.slnx
cd src\PWA; npm ci; npm test        # phone app
```

Packaging and deployment:

```powershell
.\scripts\publish-agent.ps1         # -> artifacts\win-x64\1remote.exe
.\scripts\publish-hub.ps1           # build the app into the hub and deploy it
```

Releasing the agent is a version bump and a tag — `.\scripts\bump-version.ps1` then `git push origin v0.02` builds both architectures and publishes them with their hashes. One `x.yy` number covers the agent, the hub and the app; see [Deployment](docs/deployment.md#versioning).

## Status

v1. Windows 10 2004+ on the desk, iOS 16.4+ or Android in your pocket. macOS support and richer agent-chat integrations are tracked in [issues](https://github.com/eranyariv/1RemoteCLI/issues).
