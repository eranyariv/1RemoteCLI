# AGENTS.md

## Work tracking

All work in this repo is tracked with **GitHub issues** — features, work items, bugs, chores, everything.

Before starting any new work or bug fix:

1. **Search first.** Check whether an issue already covers it:
   - `gh issue list --repo eranyariv/1RemoteCLI --state all --limit 100`
   - `gh issue list --repo eranyariv/1RemoteCLI --state all --search "<keywords>"`
2. **If an issue exists**, work against it and reference it in commits and PRs (`Fixes #<n>`).
3. **If no issue exists**, create one before making changes, then proceed against it.

This applies both to work the user requests and to problems discovered while working.

## Branching and committing

**All work goes directly on `main`.** Do not create feature branches, and do not open pull requests, unless explicitly asked to.

Commit and push without waiting to be asked. When a unit of work is complete, commit it with a clear message referencing the relevant issue (`Closes #<n>`) and push to `origin main` in the same turn.

## Specs

Functional and technical specifications live in `specs/`.

## Deployment

All Azure resources for this project deploy to the **Azure subscription owned by `owner@example.com`** (Azure Enterprise account). This is the target for the relay hub and any supporting resources — do not provision into any other subscription or tenant.

The same account is also the identity used for the Entra app registration that the agent and PWA authenticate against.

Never commit subscription IDs, tenant IDs, client secrets, connection strings, or VAPID private keys to the repo. Use App Service / Container Apps configuration or Key Vault, and keep local values in untracked user-secrets.
