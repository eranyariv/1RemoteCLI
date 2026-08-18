# The operator channel

The hub reports to one Telegram chat — the operator's own — and takes a few administrative commands back. It is how you find out that the allowlist is empty, that push has started failing, or that the Entra client secret expires in a fortnight, without opening a portal.

It is **off by default**, and off is a supported state. Nobody needs a bot to work on the relay.

## What it will never tell you

**Counts and statistics only.** No terminal output, no machine names, no session names, no session ids, no commands anyone typed. A message can say *"3 sessions, 4.2 MB, 1h 12m"*; it cannot say *which* session, on which machine, or what was on it.

That is the same trade [the logs](logging.md) make, for the same reason — except the stakes are higher here, because a log stays on the machine that wrote it while this leaves the building and lands on a phone.

So it is not a rule anyone is asked to remember. It is enforced three ways:

1. **A closed vocabulary.** `OperatorMessage` has a private base constructor, so every message the hub can send is one of the shapes nested inside it. You cannot send a string; there is no API that takes one. Adding a new thing to say means adding a record, in that file, next to the ones that show what is acceptable.
2. **A reflection guard.** `OperatorVocabularyTests` walks every member of every type in `OneRemoteCli.Hub.Ops` and fails if any signature mentions the `OneRemoteCli.Protocol` assembly, `PushPayload`, `SessionAddress` or `RegisteredMachine` — including nested inside arrays, generics and by-ref parameters. Banning the whole protocol assembly rather than a list of names means the *next* message contract someone adds is banned by default.
3. **A canary.** A test plants a recognisable session id through the counters, drains them, builds a digest and flushes the state file, then asserts the string appears in none of it.

`SessionAddress` is the trap worth knowing about. It carries `MachineName` and `SessionName`, which is correct for push — that goes to the session owner's own phone — and a disclosure here, because this goes to the operator, who is someone else. Nothing at the call site looks wrong. The guard is what notices.

**The one identifier that does travel** is the account: a username, and for `AccountRefused` also the `{tid}:{oid}` user key. That is deliberate. "Someone was refused" is not actionable, and `/allow` needs an exact, non-reassignable string to act on. The issue's ban list covers machine and session identifiers; account identity is the subject of the message, not a leak in it.

## Setting it up

Talk to [@BotFather](https://t.me/BotFather), `/newbot`, and keep the token it gives you. Then message your new bot once — anything, `/start` will do. **A bot cannot open a conversation with you**, so until you speak first it has nowhere to send anything.

Your chat id is then in:

```
https://api.telegram.org/bot<token>/getUpdates
```

as `result[].message.chat.id`. For a private chat it is your own Telegram user id and never changes.

> The token is in the **URL path**, so anything that logs a request URI logs the credential. Do not paste that URL into a chat or a ticket, and do not let a shell echo it — read the token from a file inside the command and print only status codes. The hub has the same problem and solves it in `Program.cs` with `.RemoveAllLoggers()` on the `TelegramBotApi` client; see below.

Then set it on the app:

```powershell
az webapp config appsettings set -g 1remotecli-rg -n 1remotecli-hub `
  --settings Telegram__BotToken=<token> Telegram__ChatId=<chat-id> `
  --only-show-errors -o none
```

## Configuration

| Setting | Purpose |
| --- | --- |
| `Telegram__BotToken` | The bot's API token from BotFather. **Secret.** Absent disables the channel. |
| `Telegram__ChatId` | The one chat that receives reports and the only one whose commands are obeyed. |
| `Telegram__Commands` | `true` to also poll for and act on inbound commands. Default `false`. |
| `Telegram__DigestDay` | Day of the weekly digest. Default `Monday`. |
| `Telegram__DigestHourUtc` | Hour of that day, UTC. Default `8`. |
| `Telegram__MonthlyCost` | What this deployment costs a month, for the cost-per-user line. Default `0`, which omits it. |
| `Telegram__Currency` | Symbol printed in front of it. Default `$`. |
| `Telegram__ClientSecretExpiresOn` | When the Entra client secret expires, e.g. `2027-03-14`. Absent disables the warning, and it is absent by default because this deployment has no client secret — see [azure-setup.md](azure-setup.md). |
| `Telegram__StatePath` | Where the durable counters live. Defaults to `$HOME/data/1RemoteCLI/operator-state.json`. |

Token and chat id are checked together: with either missing the channel is inert and the hub logs one line at startup saying so. Nothing else changes.

**Commands are a separate switch from the token on purpose.** Reporting is write-only — the worst a stolen reporting token does is talk. Commands can change who is allowed to sign in. A hub that only reports is a much smaller thing to lose.

`MonthlyCost` and `ClientSecretExpiresOn` are configured rather than discovered. The hub could read Azure spend from the Cost Management API, but that needs an ARM credential it should not be given: a bot token that can also read a subscription's billing is a far larger loss. The plan is a fixed monthly charge anyway, so a typed-in figure is exact. Likewise the hub validates tokens against public signing keys and never holds the client secret, so it cannot discover the expiry — but "everything works until one morning nobody can sign in" is precisely the failure this channel exists to prevent, and a date entered once at renewal is enough.

## What it sends

Unprompted:

| Message | When |
| --- | --- |
| `HubStarted` | Every start. The version now running, the version that was running before, how many accounts are allowlisted, and how many starts there have been. |
| `AllowlistEmpty` | At startup when nobody is allowlisted, i.e. the hub is unusable by anyone. |
| `AccountFirstSeen` | An account connects that has never connected before. |
| `AccountRefused` | A sign-in was refused: not allowlisted, missing scope, or no user key. Deduplicated per identity, so a retrying phone cannot spam you. |
| `PushFailuresSpiked` | 10 push failures inside a 15-minute window. Once per window. |
| `TokenFailuresSpiked` | 10 token rejections inside a 15-minute window. Once per window. |
| `AgentVersionSkew` | An agent connects on a version older than the hub's. |
| `ClientSecretExpiring` | The configured expiry is near. |
| `WeeklyDigest` | The scheduled digest: accounts, sessions, bytes, connected time, new accounts, failures, cost per active account. |

On request, in reply to a command: `StatusReport`, `HealthReport`, `VersionReport`, `DigestRequested`, `AllowlistChanged`, `AccountKicked`, `BroadcastSent`, `CommandRejected`, `Help`.

That list is exhaustive. If it is not in `OperatorMessage`, the hub cannot say it.

## Commands

Only when `Telegram__Commands` is `true`, and **only from the configured chat** — the chat id is re-checked on every update rather than trusted, because an inbound channel is an administrative one.

| Command | Does |
| --- | --- |
| `/status` | Machines connected, live sessions, accounts online, connections, uptime, version. |
| `/health` | Version, uptime, whether push is configured, how many accounts are subscribed to push, how many are allowlisted. |
| `/version` | The running hub version. |
| `/digest` | Builds and sends the weekly digest now, and starts the week again from this moment. The scheduled slot is unaffected. |
| `/allow <account>` | Adds an account to the allowlist at runtime. |
| `/deny <account>` | Denies an account at runtime. Overrides configuration. |
| `/kick <account>` | Closes that account's live sessions. Does not change the allowlist. |
| `/broadcast <text>` | Pushes a notice to every subscribed phone. |
| `/help` | The command list. |

Anything else beginning with `/` gets a `CommandRejected`. Anything not beginning with `/` is ignored entirely — the channel is not a chatbot.

**Runtime allowlist changes live in the state file, not in app settings.** They survive restarts and are replayed at startup, but they are *amendments* to the configured list: `/allow` is for admitting someone now and putting them in `Entra__Allowlist__n` later, not instead of it. `/deny` wins over configuration, so it is also the fast way to shut someone out without an App Service restart.

## The state file

The one durable thing in an otherwise stateless hub:

```
$HOME/data/1RemoteCLI/operator-state.json
```

`$HOME` on App Service is the Azure Files share — it survives restarts and redeploys, which anything derived from the content root does not. Off App Service it falls back to `%LOCALAPPDATA%`.

It holds the week's counters, which accounts have ever been seen (so "new account" means new), the runtime allowlist amendments, the last digest time, and the Telegram update offset. Written atomically, temp-then-move, and flushed periodically rather than per event. A corrupt or unreadable file is logged and treated as empty rather than being fatal — losing a week of counters must never stop the relay.

It is a few kilobytes of JSON with no schema, no migration and no SDK, and you can read it in a text editor when something looks wrong. Which is the whole argument for it over a database.

**It assumes one instance**, like the rest of the hub — see *Why the plan stays at one instance* in [deployment.md](deployment.md). Two instances would interleave writes to one file and both poll `getUpdates`, which is a race for each update.

## When it goes quiet

The channel is best-effort by design. Sends go onto a bounded queue — 256 messages, oldest dropped when full — and a background service drains it one a second, because the Bot API answers 429 much above that. A Telegram outage, a revoked token or a network failure is logged and dropped, never retried into a backlog and never allowed to fault a request path. **A silent channel is not proof of a healthy hub** — if you have not heard the weekly digest, check the app logs before assuming there was nothing to say.

If you suspect the token has leaked, `/revoke` in BotFather and set the new one. The old token stops working immediately.
