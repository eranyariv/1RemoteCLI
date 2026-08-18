# Security: what you are agreeing to

**This document is published, and the published copy is the canonical one:**

> **https://1remotecli.yariv.org/security.html**

It is deliberately not kept here in full. The readme people are pointed at tells them to read the security document *before* deciding to install, and while this repository is private, a link into it 404s for exactly that audience. A safety document only existing collaborators can open is not a safety document. Keeping a second copy here would also mean two versions of a security document drifting apart, which is the worst thing this particular file could do.

Edit `src/PWA/public/security.html`. It ships with the hub, so it goes live with the next `scripts/publish-hub.ps1`.

## What is in it

- What attaching actually grants: the ability to type into that session, not a read-only view with a reply box.
- What is protected, and by what mechanism: structural partitioning by account key, the allowlist, token re-validation on refresh, the pipe ACL scoped to your SID, the absence of any "start a session" capability in the protocol, TLS on both hops, DPAPI at rest.
- **Accepted risk 1**: the hub sees terminal output in plaintext. The payload is an opaque byte field the hub never inspects, so end-to-end encryption could be added later without changing message shapes, but it is not implemented today.
- **Accepted risk 2**: whoever holds the account can type on every paired machine. MFA is the control that matters.
- What to do if the account is compromised.

## Reporting a vulnerability

Mail eran@yariv.org, or open a private security advisory on this repository. Not a public issue.

The full threat model is in [`specs/1RemoteCLI-design-spec.md`](../specs/1RemoteCLI-design-spec.md) 7.4.
