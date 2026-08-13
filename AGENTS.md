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

The concrete target, confirmed at sign-in:

| | |
| --- | --- |
| Account | `owner@example.com` |
| Tenant | `Default Directory` (`aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee`) |
| Subscription | `Visual Studio Enterprise Subscription` (`bbbbbbbb-cccc-dddd-eeee-ffffffffffff`) |

Before provisioning anything, assert the identity — the machine-wide profile is a different account and silently deploying there would be hard to unwind:

```powershell
az account show --query "user.name" -o tsv   # must print owner@example.com
```

The same account is also the identity used for the Entra app registration that the agent and PWA authenticate against.

### Azure CLI is project-scoped

The machine-wide `az` profile is signed in to a different (Microsoft corp) account, so this project uses an **isolated Azure CLI profile** via `AZURE_CONFIG_DIR`. It lives at `~/.azure-profiles/1RemoteCLI` — outside the repo, because it holds refresh tokens and because git worktrees would otherwise each need their own login.

Every new shell must opt in; the variable does not persist between processes:

```powershell
. .\scripts\az-env.ps1     # dot-source, do not run
az account show            # verify: user.name must be owner@example.com
```

One-time sign-in (interactive, needs a browser):

```powershell
. .\scripts\az-env.ps1
az config set core.enable_broker_on_windows=false   # already set in the profile; see note
az login --allow-no-subscriptions                   # add --use-device-code if the browser redirect fails
```

The Windows account broker (WAM) is **disabled in this profile**. With it enabled, `az login` pops the native Windows account dialog, which is bound to the machine's work account and silently dismisses itself when you pick "Use another account" — the personal Microsoft account can never be entered. Disabling the broker forces the loopback browser redirect, which works. The setting is written to `$AZURE_CONFIG_DIR/config`, so the machine-wide profile keeps its broker.

Never run `az login` without dot-sourcing the script first — that would overwrite the machine-wide profile.

Never commit subscription IDs, tenant IDs, client secrets, connection strings, or VAPID private keys to the repo. Use App Service / Container Apps configuration or Key Vault, and keep local values in untracked user-secrets.
