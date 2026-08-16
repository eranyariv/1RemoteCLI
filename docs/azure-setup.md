# Azure setup

Everything below lives in the Azure subscription owned by `owner@example.com`. Before running any of it, scope the CLI to this project — the machine-wide `az` profile is signed in to a different account:

```powershell
. .\scripts\az-env.ps1
az account show --query "user.name" -o tsv   # must print owner@example.com
```

| | |
| --- | --- |
| Tenant | `Default Directory` (`aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee`) |
| Subscription | `Visual Studio Enterprise Subscription` (`bbbbbbbb-cccc-dddd-eeee-ffffffffffff`) |
| Region | `israelcentral` |

Azure requires MFA for resource management. If a command fails with `RequestDisallowedByAzure` and mentions MFA, re-authenticate with the challenge `az` prints:

```powershell
az login --tenant "<tenant>" --scope "https://management.core.windows.net//.default" --claims-challenge "<claims>"
```

## Entra app registration

One registration serves both the agent and the PWA. That is deliberate: because both sides present a token from the same application, "the phone and the machine are the same identity" is something the hub can actually verify rather than infer.

| | |
| --- | --- |
| Display name | `1RemoteCLI` |
| Application (client) ID | `3db435ae-5e69-483c-a044-d6e8b6262fc6` |
| Object ID | `cf5ff7b5-f852-421f-8564-edd4734f4388` |
| Supported account types | Any Entra tenant + personal Microsoft accounts |
| Application ID URI | `api://3db435ae-5e69-483c-a044-d6e8b6262fc6` |
| Exposed scope | `Session.Access` (`90af9976-aefb-4d54-b293-bfc8c0cbe3a2`) |
| Access token version | 2 |
| Public client (agent) redirect | `http://127.0.0.1` |
| SPA (PWA) redirect | `https://1remotecli-hub.azurewebsites.net/`, `http://localhost:5173/`, `http://localhost:4173/` |

The agent's redirect uses **`127.0.0.1`, not `localhost`**, and that is load-bearing. See [One registration, two platforms](#one-registration-two-platforms) below before changing either row.

The client ID, tenant, and scope name are **configuration, not secrets** — they ship in the PWA bundle and in agent config. There is no client secret at all: the agent is a public client using the loopback redirect with PKCE, and the PWA is an SPA using auth code + PKCE. Nothing in this project should ever need a credential that must be kept out of git.

### Recreating it from scratch

```powershell
. .\scripts\az-env.ps1

az ad app create `
  --display-name "1RemoteCLI" `
  --sign-in-audience AzureADandPersonalMicrosoftAccount `
  --public-client-redirect-uris "http://127.0.0.1"
```

Take the `appId` and `id` from the output, then patch the rest through Graph (the CLI has no first-class flags for exposed scopes):

```powershell
$appObjId = "<object id>"
$appId    = "<application id>"
$scopeId  = [guid]::NewGuid().ToString()

$body = @{
  identifierUris = @("api://$appId")
  isFallbackPublicClient = $true
  api = @{
    requestedAccessTokenVersion = 2
    oauth2PermissionScopes = @(@{
      id = $scopeId
      value = "Session.Access"
      type = "User"
      isEnabled = $true
      adminConsentDisplayName = "Access terminal sessions"
      adminConsentDescription = "Allows the app to attach to the signed-in user's terminal sessions through the 1RemoteCLI relay hub."
      userConsentDisplayName = "Access your terminal sessions"
      userConsentDescription = "Allows 1RemoteCLI to attach to your terminal sessions and send input on your behalf."
    })
  }
} | ConvertTo-Json -Depth 10 -Compress

Set-Content "$env:TEMP\appreg.json" $body -Encoding utf8
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$appObjId" `
  --headers "Content-Type=application/json" --body "@$env:TEMP\appreg.json"
```

Then pre-authorize the app for its own scope, so signing in does not prompt for consent on a permission the user is implicitly granting anyway, and create the service principal that lets the tenant issue tokens for it:

```powershell
$body = @{ api = @{ preAuthorizedApplications = @(@{ appId = $appId; delegatedPermissionIds = @($scopeId) }) } } |
  ConvertTo-Json -Depth 10 -Compress
Set-Content "$env:TEMP\appreg-pre.json" $body -Encoding utf8
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$appObjId" `
  --headers "Content-Type=application/json" --body "@$env:TEMP\appreg-pre.json"

az ad sp create --id $appId
```

`requestedAccessTokenVersion = 2` is load-bearing. A v1 token carries a different issuer and claim shape, and the hub validates the issuer dynamically against the token's own `tid`.

Add the SPA redirects once the hub's hosts are final. **Every origin the app is served from needs its own entry**, including a custom domain in front of the App Service:

```powershell
$body = @{ spa = @{ redirectUris = @(
  "https://1remotecli.yariv.org/",              # custom domain, the front door
  "https://1remotecli-hub.azurewebsites.net/",  # the App Service's own host
  "http://localhost:5173/",                     # dev server
  "http://localhost:4173/"                      # preview server
) } } | ConvertTo-Json -Depth 10 -Compress

Set-Content "$env:TEMP\appreg-spa.json" $body -Encoding ascii
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$appObjId" `
  --headers "Content-Type=application/json" --body "@$env:TEMP\appreg-spa.json"
```

Through a file rather than an inline `--body`: PowerShell mangles the quoting of a JSON literal on the command line, and Graph rejects it with `Unable to read JSON request payload` — which reads like a Graph problem and is not one.

The trailing slash matters. Entra matches SPA redirects exactly, and the PWA asks for `${window.location.origin}/`.

Registering an origin is not optional and is easy to forget, because **nothing fails until somebody tries to sign in**: `/health`, the manifest, the service worker and every asset are served happily from an origin nobody can log in to, so a new domain looks completely working right up to the moment it is used for the one thing it exists for. Adding a domain and registering it are two actions in two different systems with no link between them — [#52](https://github.com/eranyariv/1RemoteCLI/issues/52) and [#62](https://github.com/eranyariv/1RemoteCLI/issues/62) are the same bug, twice.

### Adding a custom domain later

Binding a domain to the App Service is only half of it. In full:

1. CNAME the domain at the App Service host (Cloudflare or otherwise) and bind it: `az webapp config hostname add`.
2. **Add `https://<domain>/` to `spa.redirectUris` above.** This is the step that gets missed.
3. Tell anyone who installed the PWA from the old origin to reinstall from the new one. Web push subscriptions are scoped to the origin by the browser, so they do not carry over — the app appears installed and simply never notifies.

Confirm both halves:

```powershell
(Invoke-WebRequest 'https://<domain>/health').Content
(az ad app list --display-name "1RemoteCLI" -o json | ConvertFrom-Json).spa.redirectUris
```

### One registration, two platforms

The agent and the PWA share this registration, which means one app carries both a **public client** redirect and **SPA** redirects. Entra matches loopback redirect URIs *without regard to port*, and where a request could match either platform, SPA classification wins.

So a desktop redirect of `http://localhost:{ephemeral}` also matches an SPA entry like `http://localhost:5173/`. The authorization code comes back marked single-page, redeemable only with an `Origin` header that a desktop client never sends, and sign-in dies at redemption:

```
AADSTS90023: Tokens issued for the 'Single-Page Application' client-type should only
be redeemed via cross-origin requests.
```

The two host spellings are compared as strings, so keeping the agent on `127.0.0.1` and the PWA's dev servers on `localhost` removes the ambiguity and lets both live on one registration. The rules that follow from that:

- **Never add a `localhost` URI to the public client platform**, and never add a `127.0.0.1` URI to the SPA platform. Either one re-creates the collision.
- If you ever need a second loopback host for the SPA, split the agent into its own native registration instead.

`AuthConfig.RedirectUri` in the agent must match the public client entry exactly.

## Hub

| | |
| --- | --- |
| Resource group | `1remotecli-rg` |
| App Service plan | `1remotecli-plan` (Linux, B1, **1 worker**) |
| Web app | `1remotecli-hub` |
| Host | `https://1remotecli-hub.azurewebsites.net` |
| Health probe | `https://1remotecli-hub.azurewebsites.net/health` |

```powershell
. .\scripts\az-env.ps1

az group create -n 1remotecli-rg -l israelcentral
az appservice plan create -g 1remotecli-rg -n 1remotecli-plan --sku B1 --is-linux --number-of-workers 1
az webapp create -g 1remotecli-rg -p 1remotecli-plan -n 1remotecli-hub --runtime "DOTNETCORE:8.0"

az webapp config set -g 1remotecli-rg -n 1remotecli-hub `
  --web-sockets-enabled true --always-on true --http20-enabled true --min-tls-version 1.2
az webapp update -g 1remotecli-rg -n 1remotecli-hub --https-only true
```

Why each of those matters:

- **One worker, and never more.** The routing registry is in memory, so a second instance silently breaks routing: an agent connected to instance A is invisible to a phone connected to instance B, and the failure looks like "my machine isn't showing up" rather than an error. Do not enable autoscale on this plan.
- **WebSockets enabled.** SignalR falls back to long polling otherwise, which is miserable for a live terminal.
- **Always On.** Without it App Service unloads the app after ~20 minutes idle, which drops every connected agent.
- **B1, not F1.** The free tier has no Always On and a daily CPU quota that a long-lived connection will hit.

### Redeploying

```powershell
. .\scripts\az-env.ps1
.\scripts\publish-hub.ps1
```

**Use the script, not a bare `dotnet publish`.** The hub serves the phone app from its own `wwwroot`, so a publish that skips the app build deploys whatever bundle was last left there — on a developer machine, possibly a development one, and in CI, nothing at all. The script builds the app, stages it, publishes, deploys and then verifies. See [Deployment](deployment.md).

Verify by hand:

```powershell
Invoke-WebRequest https://1remotecli-hub.azurewebsites.net/health -UseBasicParsing | Select-Object -Expand Content
# {"status":"ok","version":"1.0.0.0","utcNow":"..."}
```

## Notifications (VAPID)

Web Push identifies the sender with a VAPID keypair — a P-256 key, base64url, with no padding. It is not an Azure resource and not an Entra credential; it is generated once and lives in app settings. Without it the hub starts, logs a warning, and serves 404 from `/push/vapid`, which the PWA reads as "notifications are off". Everything else keeps working.

Generate one:

```powershell
dotnet run --project src\Hub\1RemoteCLI.Hub.csproj -- --generate-vapid
```

Or, with Node available:

```powershell
npx --yes web-push generate-vapid-keys
```

The **subject** must be a `mailto:` or `https:` URL that identifies you. Push services use it to reach the sender when a subscription misbehaves, and some reject a request without it.

```powershell
. .\scripts\az-env.ps1

az webapp config appsettings set -g 1remotecli-rg -n 1remotecli-hub --settings `
  "Push__Vapid__Subject=mailto:owner@example.com" `
  "Push__Vapid__PublicKey=<public key>" `
  "Push__Vapid__PrivateKey=<private key>"
```

The double underscore is how App Service nests configuration; these map to the `Push:Vapid` section.

**The private key is a credential.** Anyone holding it can send a notification that arrives under this app's name and icon on every subscribed phone. Keep it out of the repo, out of `appsettings.json`, and out of shell history where you can. It is not needed locally unless you are testing notifications.

**Rotating the keypair invalidates every existing subscription.** Browsers tie a subscription to the public key it was created with, so after a rotation every phone must open the app again to re-subscribe — which it does automatically on connect, but only once someone opens it. Rotate only if the private key leaks.

Verify:

```powershell
Invoke-WebRequest https://1remotecli-hub.azurewebsites.net/push/vapid -UseBasicParsing | Select-Object -Expand Content
# {"key":"BM...."}
```
