# toboo Harness bridge 1.0.2

This independent Cordis plugin runs inside the user's existing DeepSeek Harness. The interactive UI is **toboo's existing Avalonia desktop application**. Harness supplies model adapters/authentication in the background; Trilium is not in this application's request path.

It uses only `ctx.llm.listProviders`, `listModels`, optional `resolveModelInfo`, and `stream`. It never creates a general-purpose Agent, changes the global default model, reads Harness/Trilium conversation histories, or exposes filesystem, terminal, browser, attachment, servo or tool execution operations. The model request declares `tools: []`; an unexpected tool-call output stops the turn.

Install from the repository:

```powershell
./scripts/install-toboo-harness.ps1
# Fully quit Harness from its system tray, then reopen it.
./scripts/test-toboo-harness.ps1   # Read-only status/model check, no prompt.
./scripts/start-toboo.ps1
```

The installer backs up the desktop profile overlay under ignored `artifacts/harness-install/`, appends only `toboo-companion-bridge`, leaves existing entries in place, and restricts the independent `.toboo-harness` directory to the current Windows user. It does not copy any model API key. This source installation requires the repository path to remain available. The same bridge and installer are included in the portable release.

Private runtime files are outside the repository, under `%USERPROFILE%/.toboo-harness/`: `connection.json`, `bridge.lock`, `client.json`, `client.json.lock`, and `sessions/<uuid>.json`. The connection credential rotates on startup. Only `127.0.0.1:37848/rpc` is served; Trilium's separate `37846` bridge is unchanged. Native requests require the independent credential, exact loopback Host, JSON content, and no Origin header. C# ignores environment HTTP proxies, refuses redirects, caps response size, and validates the service/version contract. Do not publish these private files.

Protocol 1 actions: `status`, `models`, `list`, `new`, `view`, `send`, `stop`. Requests use a fixed schema. Session and request IDs are version-4 UUIDs. `new` is idempotent; `send` durably records a UUID plus payload fingerprint before calling the model. A repeated UUID with identical content returns the prior admission; changed content is rejected. A cancellation can record a tombstone before admission, preventing a delayed send from starting later. Views serialize behind admission writes; a terminal result becomes visible after its final disk commit. Shutdown drains pending admissions without starting new calls. An exclusive process lock is acquired before recovering owned records; the desktop's pending-state file also has an exclusive writer lock.

The UI retains an uncertain payload and UUID on disk, keeps the draft, and offers explicit reconciliation/retry/stop; it never automatically resends a model prompt. Each turn has a 90-second deadline and a 20-second client lease. Stop targets the exact request ID and sends AbortSignal to the adapter. Already-consumed model usage remains governed by Harness/the provider. Provider-level network retries are separate from bridge admission deduplication; this is not an exactly-once billing guarantee.

Limits: 4,000 input characters, 8,000 visible reply characters, 1,024 output tokens requested, two simultaneous generation jobs, 100 turns per conversation and 100 owned conversations. The UI pages 20 turns at a time. Model context retains complete successful pairs only, up to 12 pairs and 16,000 characters. Older history remains locally stored but is not unlimited model memory. Off reasoning is selected only when the exact model advertises an `off` effort; no global model setting is written.

Optional expression context defaults off. The desktop only snapshots a continuously confirmed live-camera expression within its 500ms monotonic freshness budget. The bridge drops an old cue at admission/retry; no image bytes are accepted. Transcript persistence and privacy behavior are explained in toboo's settings. Messages display as selectable plain text; remote HTML, images and links are not automatically executed or fetched.

Verification:

```powershell
node --test integrations/toboo-harness/tests/engine.test.mjs
./scripts/test-toboo-harness.ps1 -Live # Explicitly sends short generated test prompts in an owned test session.
```

The live script tests real replies, conversation recall, exact-ID duplicate admission and cancellation. It never uses a camera, actuator, Trilium note or existing personal conversation. Outputs are ignored under `artifacts/harness-live/`. `TOBOO_HARNESS_TEST_SESSION` optionally enables C# read-only loading/rendering tests of that generated session. Without it those two tests explicitly skip; ordinary offline tests remain active.

Tested against installed Harness `0.2.1-rc.5`. API design was checked against upstream commit `fcc0ad462172522aaf5c93a7b2e01f6b9f0b0159`, particularly `packages/llm/llm`. References: [official LLM service](https://github.com/deepseek-ai/deepseek-harness/tree/fcc0ad462172522aaf5c93a7b2e01f6b9f0b0159/packages/llm/llm). This is an independent implementation using the host's public model service, not a bundled Harness runtime or copied Trilium UI. No additional npm package dependency is installed.
