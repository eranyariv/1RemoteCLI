# 1RemoteCLI PWA

The phone client. It signs you in with your Microsoft account, connects to the relay
hub, and shows the machines you own and the terminal sessions running on them.

This is a browser app, not a store app. There is nothing to install and nothing to
approve: you open a URL, sign in with the same account the agent on your desktop
signed in with, and you see your sessions. That property is the whole point — the
thing you reach for when you are away from the desk cannot be something that needs a
review queue to ship.

## Running it

```sh
npm install
npm run dev       # dev server on http://localhost:5173
npm run build     # type-check + production bundle into dist/
npm run preview   # serve the production bundle on http://localhost:4173
npm test          # vitest
npm run lint      # oxlint
```

`npm run build` runs `tsc -b` first, so a type error fails the build rather than
shipping a broken bundle.

### Reaching the dev server from a phone

Vite binds to localhost by default, which your phone cannot reach. Bind to the LAN
instead:

```sh
npm run dev -- --host
```

Vite prints a `Network:` URL; open that on the phone. Two caveats:

- The machine's firewall must allow inbound connections on the port. Windows prompts
  the first time.
- Sign-in only works if the origin you browse to is a registered redirect URI on the
  Entra app registration. `http://localhost:5173/` and `http://localhost:4173/` are
  registered (see `docs/azure-setup.md`); a LAN address like
  `http://192.168.1.20:5173/` is not, so you would need to add it. For a quick look at
  the layout on a real phone, that is worth doing. For anything involving real
  sessions, use the deployed build instead.

## Configuration

Everything is read from Vite environment variables at build time. Create
`.env.local` (git-ignored) to override:

| Variable | Default | Meaning |
| --- | --- | --- |
| `VITE_HUB_URL` | `https://1remotecli-hub.azurewebsites.net` in production, `http://localhost:5080` in dev | Relay hub base URL. The client appends `/hub/relay`. |
| `VITE_ENTRA_CLIENT_ID` | the registered SPA client id | Entra application (client) id. |
| `VITE_ENTRA_TENANT` | `common` | Authority tenant segment. |
| `VITE_API_SCOPE` | `api://.../Session.Access` | The scope requested for the hub token. |

The defaults are the real ones, so a plain `npm run dev` against a locally running hub
works with no configuration at all.

## How it is put together

```
src/
  protocol/   the wire format shared with the C# hub
  auth/       MSAL configuration and token acquisition
  relay/      the SignalR connection and the machine-list state it produces
  ui/         presentational components
  App.tsx     the shell: sign-in gate, connection status, machine list
```

### `protocol/` — why this exists as its own layer

The hub speaks MessagePack, and the C# message types are annotated with
`[MessagePackObject]` and `[Key(n)]`. That combination serialises each message as a
**positional array**, not a map. Nothing in the bytes on the wire names a field; the
browser reads column 3 and has to already know that column 3 is the session id.

The consequence is that inserting a property into the middle of a C# record silently
shifts every later field for this client. There is no exception, no missing key, no
type error — just the wrong value in the wrong place, discovered later as a bug that
looks nothing like a serialisation problem.

So the contract is tested rather than assumed. `tests/Protocol.Tests/WireContractTests.cs`
serialises a canonical set of messages using SignalR's *actual* serializer options and
writes `src/protocol/wire.fixture.json` — the raw bytes plus the values C# believes it
encoded. `wire.contract.test.ts` decodes those bytes with this client's decoders and
asserts it recovers exactly those values. Change a `[Key]` on either side and one of
the two suites fails immediately, in the language that made the change.

Do not hand-edit `wire.fixture.json`. Regenerate it by running the C# test with
`UPDATE_WIRE_FIXTURE=1` set, and commit the result.

Two details in that format are worth knowing before touching `wire.ts`:

- **Enums travel as their names**, not their numbers — `"Snapshot"`, not `1`.
- **`DateTimeOffset` is a two-element array**: a wall-clock time written as though it
  were UTC, and an offset in minutes. Reading only the first element produces code
  that is correct on a UTC machine and hours wrong everywhere else. The fixture
  deliberately uses `+03:00` so that mistake fails a test.

### `relay/` — connection and state

`RelayClient` is deliberately not a React thing. A hub connection outlives renders,
and wiring one into component lifecycle reliably produces two connections and a leak.
The class owns the connection; `useRelay` is a thin hook over it, and `start()`
returns any in-flight attempt so React StrictMode's double-effect is harmless.

`machines.ts` is a pure reducer, which is why the lifecycle rules are testable without
a server. Two of those rules are decisions rather than mechanics:

- **An offline machine keeps its row but loses its sessions.** Keeping the row is how
  you tell "my laptop is asleep" apart from "I have no machines" — the second would be
  alarming and wrong. Dropping the sessions avoids offering a tap that is guaranteed
  to fail.
- **A notification about a machine we have never heard of is dropped**, not used to
  invent a row. A row assembled from a partial notification would be missing most of
  what the UI shows; the next `ListMachines` fills the gap properly.

### `auth/` — sign-in

Redirect flow, not popup: iOS Safari blocks popups that are not the direct result of a
tap, and the popup path fails in exactly the situation this app exists for.

Tokens live in `sessionStorage`. `localStorage` would survive closing the tab, which
means a token sitting on a phone that may be handed to someone else. Pure in-memory
would be stricter still, but it forces a full interactive sign-in on every reload and
every time iOS reclaims a backgrounded tab — which is most of the time on a phone.

When signed out, the token factory returns an empty string rather than throwing. The
hub then rejects the connection cleanly and the UI can say so, which is far better
than a transport-level failure that surfaces as an unexplained disconnect.

## What it does not do yet

Tapping a session shows a placeholder. The terminal view — xterm.js, attach/detach,
input, resize, Ctrl+C — is the next task.
