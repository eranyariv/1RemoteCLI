# Voice mode deployment specification

## Scope

Voice mode uses Azure AI Speech for one utterance at a time:

1. The signed-in PWA requests `POST /api/voice/token` from the hub.
2. The hub validates the existing `Session.Access` bearer token and applies a per-user rate limit.
3. The hub exchanges its server-held Speech resource key for a short-lived Azure token.
4. The browser uses that token with the Azure Speech JavaScript SDK for streaming microphone recognition and speech synthesis.
5. Recognized text follows the existing authorized SignalR paths for project/session navigation, ACP prompts, terminal input, and ACP permissions.

No audio passes through the relay, and raw audio is not stored. The PWA never receives the Speech resource key. Microsoft documents Speech STS tokens as valid for 10 minutes; the hub advertises a 9-minute expiry and the PWA refreshes with one minute remaining.

The first implementation uses deterministic intent routing and local bounded output summaries. **No Azure OpenAI resource or model is required.** Add an LLM only if measured utterances cannot be routed or summarized deterministically; doing so requires a separate design and cost/privacy review.

## Required Azure resources

| Resource | Initial choice | Notes |
| --- | --- | --- |
| Existing hub | App Service in `israelcentral` | No topology change; voice HTTP endpoints are same-origin. |
| Azure AI Speech | One Speech resource in `uaenorth`, F0 for evaluation or S0 for production | `israelcentral` is not in the published Speech region matrix. `uaenorth` is the initial nearby supported region; verify STT, the selected neural voice, subscription policy, and data residency before provisioning. `westeurope` is the fallback. |
| Speech recognition locale | `en-US` | App setting; change without rebuilding. |
| Speech synthesis voice | `en-US-AvaMultilingualNeural` | App setting; verify availability in the selected region before deployment. |
| Key Vault | Existing or one Standard vault | Recommended for the Speech resource key. The App Service setting can be a Key Vault reference. |
| Azure OpenAI | None | Not used by this implementation. |

Provision every resource in the project owner's Azure subscription. Before any Azure command, follow `AGENTS.md`: dot-source `scripts\az-env.ps1`, print the project-scoped account, and compare account, tenant, and subscription with the untracked `azure-target.local.md`.

## Hub configuration

App Service maps double underscores to the `AzureSpeech` configuration section:

| App Service setting | Required | Secret | Default |
| --- | --- | --- | --- |
| `AzureSpeech__Region` | Yes | No | none |
| `AzureSpeech__SubscriptionKey` | Yes | **Yes** | none |
| `AzureSpeech__RecognitionLanguage` | No | No | `en-US` |
| `AzureSpeech__VoiceName` | No | No | `en-US-AvaMultilingualNeural` |

Do not put the subscription key in source control, the PWA build, shell history, or logs. Prefer:

```text
AzureSpeech__SubscriptionKey=@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<name>/)
```

Enable the App Service system-assigned identity and grant it only `Key Vault Secrets User` on that vault/secret. If Key Vault is not used, set the value directly in App Service configuration through a secure operator workflow.

The hub deliberately removes default `HttpClient` logging from the Speech token client. Provider response bodies and tokens are never logged.

## API and authorization contract

| Endpoint | Authorization | Response |
| --- | --- | --- |
| `GET /api/voice/health` | Existing `Session.Access` bearer token | Provider name, configured status, region, locale, voice, and public limits; never a key or token. |
| `POST /api/voice/token` | Existing `Session.Access` bearer token | Short-lived token, region, locale, voice, and expiry; `Cache-Control: no-store`. |

The token endpoint derives the user from validated `tid` + `oid`; it accepts no user, project, machine, or session identifier. Session ownership remains enforced by the existing relay registry when recognized text is sent.

## Limits and cost controls

| Control | Value |
| --- | --- |
| Simultaneous microphone/speaker operation per PWA | 1 |
| Maximum utterance | 30 seconds |
| Initial silence timeout | 10 seconds |
| Recognized text | 4,000 characters |
| One synthesized speech chunk | 2,000 characters |
| Terminal detail retained in memory | Latest 8,000 cleaned characters |
| Token cache | 9 minutes, refresh with 1 minute remaining |
| Token grants | 12 per signed-in user per minute, no queue |
| Automatic provider retries | None; errors are surfaced and the user explicitly retries |
| Raw audio retention | None |

For planning, Azure's published F0 allowance is 5 real-time STT audio hours and 500,000 neural TTS characters per month, with one concurrent request. S0 is pay-as-you-go; the public price page has historically listed roughly USD 1 per real-time STT audio hour and USD 15 per million neural TTS characters, but region/currency prices and quotas change. Confirm the selected region in the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) immediately before provisioning and configure Azure Cost Management budget alerts on the resource group.

F0 is appropriate for a single evaluator but its concurrency of one means listening and speaking requests from two phones will throttle. Use S0 for the expected five-user deployment and keep the service quota at its default unless telemetry proves otherwise.

## Privacy and safety

- The browser shows listening, thinking, speaking, muted, disconnected, and provider-error states.
- `stop voice mode`, `cancel`, `repeat`, `back to sessions`, and `back to projects` are intercepted before session input.
- Recognition is single-utterance and final results are deduplicated briefly to prevent retry/barge-in double sends.
- ACP prompts and permissions use the existing typed relay methods.
- Terminal commands are deliberate text plus Return. Destructive, elevated, multiline, compound, redirected, or unusually long commands require a spoken yes/no confirmation.
- Terminal control sequences are removed before speech. Long output is summarized locally and retained only in bounded memory for `more detail`.
- Tokens, keys, raw audio, and full sensitive transcripts must not be added to application logs or analytics.

## Browser and mobile constraints

- A user tap is required to start voice mode so browsers can grant microphone access and unlock audio playback.
- `getUserMedia` requires HTTPS (localhost is the development exception).
- iOS uses WebKit for all installed PWAs. Locking the phone, backgrounding the app, an audio route change, or an incoming call can stop microphone capture and suspend the SignalR connection. The app reports disconnection and resumes after the relay reconnects; it cannot keep an open microphone while suspended.
- Bluetooth devices can add recognition and playback latency. The UI remains authoritative when audible state feedback is delayed.
- Browser/OS microphone permission denial is not recoverable in code; the user must re-enable it in site or system settings.
- Voice mode is not a substitute for the visual terminal when output is highly structured, interactive full-screen, or exceeds useful speech detail.

## Deployment and diagnostics

1. Provision the Speech resource in the selected supported region and choose F0 or S0.
2. Put its key in Key Vault (recommended) or protected App Service configuration.
3. Set the four `AzureSpeech__*` settings and restart the hub.
4. Sign in to the PWA and call `GET /api/voice/health`; expect `status: "ready"`.
5. Start voice mode from the project list and verify project selection, session selection, three ACP or terminal turns, a risky terminal confirmation, navigation commands, an ACP permission, and reconnect behavior.
6. Inspect Azure metrics for STT/TTS requests, throttles, latency, and spend. Do not enable request-body or audio logging.

`not_configured` health means a setting is missing or the region string is unsafe. HTTP 502 from the token endpoint means Azure rejected or could not complete the key exchange. HTTP 429 means the per-user token-grant limit was reached. None of these responses contains a secret.

## References

- [Speech authentication and 10-minute STS tokens](https://learn.microsoft.com/azure/ai-services/speech-service/rest-text-to-speech)
- [Supported Speech regions](https://learn.microsoft.com/azure/ai-services/speech-service/regions)
- [Speech quotas and limits](https://learn.microsoft.com/azure/ai-services/speech-service/speech-services-quotas-and-limits)
- [Azure AI Speech pricing](https://azure.microsoft.com/pricing/details/cognitive-services/speech-services/)
- [Secure-context microphone requirements](https://developer.mozilla.org/docs/Web/API/MediaDevices/getUserMedia)
