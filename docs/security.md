# Security: what you are agreeing to

Read this before you point 1RemoteCLI at a machine that matters. It is short, and it is deliberately blunt about the parts that are not solved.

## What it actually gives you

An attached phone can type anything into that terminal session that a keyboard at your desk could. That is the product. It is not a read-only viewer with a reply box.

## What is protected

| Threat | How |
| --- | --- |
| Another user reaching your machines | Sessions are partitioned by account key structurally, not filtered. There is no code path that looks up another account's machine. |
| Someone unknown signing in | An explicit account allowlist. An empty allowlist admits nobody. |
| Stale credentials | Tokens are validated on connect and re-validated on refresh mid-connection. A refresh that changes the account aborts the connection. |
| Another user on your PC | The named pipe between wrapper and agent is ACL'd to your SID. |
| Remote code execution | The phone can attach to sessions. It cannot start one. The capability does not exist in the protocol. |
| A machine pretending to be yours | Machine ids are agent-generated GUIDs, and lookups are scoped to your partition. |
| Interception in transit | TLS on both hops. The agent dials out; nothing listens on your PC and no port is opened. |

Tokens on the PC are encrypted with DPAPI scoped to your Windows user: another account on the same machine cannot read them, and the file cannot be copied to another machine and reused.

## Accepted risk 1 — the relay sees your terminal in plaintext

Terminal output contains secrets. API keys echoed into a shell, the contents of a `.env`, private source code, connection strings in a stack trace. TLS protects all of that in transit, but **the hub process itself sees it unencrypted.**

For a self-hosted hub, run by the same small group that uses it, that is a reasonable trade against the complexity of end-to-end encryption. It stops being reasonable the moment the hub is run by someone you would not hand those secrets to directly. The test is simple: *would you paste this terminal's contents into a message to whoever operates the hub?* If not, do not attach that session.

The protocol keeps the terminal payload as an opaque byte field the hub never inspects, so end-to-end encryption could be added later by encrypting that field between agent and phone without changing any message shapes. It is not implemented today.

## Accepted risk 2 — full control is full control

**Whoever holds the Microsoft account holds every live session on every paired machine.** Not read access: the ability to type. `rm -rf`, `git push --force`, exfiltrating anything the machine can reach.

This is inherent — a tool for answering prompts from your phone is a tool for typing on your machine from your phone. What limits the blast radius:

- **Require MFA on the account.** This is the single control that matters. Everything else assumes the account is not compromised.
- **Sessions only exist while you have one running.** There is no persistent back door: nothing is attachable unless you started it with `1remote` and it is still alive.
- **Keep the allowlist to people you would give your unlocked laptop to**, because functionally that is what you are doing.
- **Treat the phone as a credential.** Screen lock, biometrics, and remote wipe available.

## Practical guidance

**Do** use it for coding agents, builds, test runs, deployments you are supervising — the long-running things that stop and ask.

**Think twice** about sessions holding production credentials, or on machines with access to customer data. Not because it is broken, but because the two accepted risks above are real and you would be accepting them on someone else's behalf.

**Do not** run a hub for people outside your trust boundary. This design assumes the operator and the users are the same small group.

## If the account is compromised

1. Revoke sessions on the Microsoft account and reset the password.
2. Remove the account from the hub allowlist — `Entra__Allowlist__*`. This ends access; it is checked on connect and on every token refresh, so existing connections do not survive it indefinitely.
3. On each paired machine: `1remote logout`, and kill any wrapper processes to end live sessions. The running agent notices within a second and drops its hub connection, which takes the machine off every phone; it does not need restarting.
4. Assume anything visible in an attached session was read, and rotate accordingly.

## Reporting a vulnerability

Open a private security advisory on https://github.com/eranyariv/1RemoteCLI rather than a public issue.

The full threat model is in [`specs/1RemoteCLI-design-spec.md`](../specs/1RemoteCLI-design-spec.md) §7.4.
