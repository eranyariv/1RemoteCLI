# 1RemoteCLI documentation

Attach your phone to a terminal session that is already running on your Windows machine — read the output, answer the prompt, send Ctrl+C — and then walk away again.

The problem it solves: you start `claude` on a long refactor, leave your desk, and twenty minutes later it is sitting on *"Do you want me to apply these changes? (y/n)"* and has been for nineteen of them.

## Read these in order

| | |
| --- | --- |
| **[Getting started](getting-started.md)** | Install, sign in, share your first session. Start here. |
| **[Using it from your phone](phone-setup.md)** | Add the app to your home screen, turn on notifications, drive a session. |
| **[Troubleshooting](troubleshooting.md)** | When the agent will not connect, a machine will not appear, or notifications never arrive. |
| **[Security and what you are agreeing to](security.md)** | Read this before you point it at a machine that matters. It is short and it is honest. |

## If you are running the service, not just using it

| | |
| --- | --- |
| **[Deployment](deployment.md)** | Deploy the hub, configure it, add someone to the allowlist, ship a new `1remote.exe`. |
| **[Azure setup](azure-setup.md)** | The one-time app registration and resource provisioning, from zero. |
| **[Logging](logging.md)** | Where the logs are and how to turn the volume up. |
| **[Implementation plan](implementation-plan.md)** | The stages and tasks v1 was built from. |

The full design is in [`specs/1RemoteCLI-design-spec.md`](../specs/1RemoteCLI-design-spec.md).

## The shape of it in one picture

```mermaid
flowchart LR
    subgraph pc["Your Windows PC"]
        w["1remote claude<br/>(wrapper + ConPTY)"] <-->|named pipe| a["1remote agent<br/>(tray, one per machine)"]
    end
    a <-->|"WebSocket, TLS"| h["Hub<br/>(Azure App Service)"]
    h <-->|"WebSocket, TLS"| p["Phone<br/>(PWA on your home screen)"]
    h -.->|"Web Push"| p
```

Three pieces, one binary. `1remote.exe` is the wrapper *and* the agent; which one you get depends on the command. The hub is a relay in Azure that both sides dial out to, so nothing has to be reachable from the internet and no ports are opened on your PC.
