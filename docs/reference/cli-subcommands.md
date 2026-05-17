# Reference: CLI subcommands

Per-command flag + argument + exit-code reference for the `vais` CLI. ~29 top-level commands plus three branches (`eval` × 5, `diagnose` × 3, `config` × 4) for a total of 41 commands. Run `vais --help` for the live, version-correct verb list.

Every HTTP-bound command supports:

- `--context <name>` — override the active context for this call.
- `--token <value>` — override the resolved bearer token (highest auth precedence).

Local-only commands (`version`, `init`, `plugin-init`, `plugin-build`, the `config` branch) ignore both.

## Root commands

### `vais version`

| Field | Value |
|---|---|
| Purpose | Print the CLI assembly version. |
| Arguments | — |
| Flags | — |
| Output | One line with the version string. |
| Exit codes | `0` |

### `vais init <name>`

Scaffold a starter agent manifest to stdout or a file. Local only; no HTTP. The scaffold format is YAML; the `-o/--output` flag takes a filesystem path, not a format selector.

| Field | Value |
|---|---|
| Arguments | `<name>` — agent id baked into the manifest's top-level `id` field. |
| `-o, --output <path>` | Write the YAML scaffold to this path. Default: stdout. |
| `--model <provider>` | LLM provider. Default: `openai`. |
| `--mode <mode>` | Execution mode. Default: `toolCalling`. Other: `sgr`. |
| Exit codes | `0`, `1` |

### `vais get [name]`

List agents or fetch a single manifest.

| Field | Value |
|---|---|
| Arguments | `[name]` — optional. Omit to list; provide to fetch one. |
| `--version <value>` | Target a specific version (single-item mode only). Default: latest. |
| `--label-prefix <prefix>` | Filter list results by label prefix (e.g. `env:staging`). |
| `--limit <n>` | Max entries in list mode. Default: unlimited. |
| `-o, --output <format>` | `table` / `yaml` / `json`. Default: `table` for list, `yaml` for single item. |
| Exit codes | `0`, `1`, `2`, `3`, `4`, `130` |

### `vais apply -f <file>`

Create or update agent or graph manifests. A single file may contain multiple documents separated by `---`; `kind: Agent` and `kind: AgentGraph` documents are both accepted and dispatched to the appropriate endpoint. Falls back from `POST` to `PUT` on `409 Conflict` per document.

| Field | Value |
|---|---|
| Arguments | — |
| `-f, --file <path>` | **Required.** Manifest path (YAML / JSON) or `-` for stdin. May be specified multiple times. |
| `--idempotency-key <value>` | Stamp each call with this key. Default: generated UUID. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

**Cross-runtime note (v0.20).** To deploy agents to a remote runtime (e.g. to satisfy a `ref.runtimeUrl` in a cross-runtime graph), use `--context` or `--server` to point `apply` at the target runtime. The manifest itself does not embed which runtime it should be registered on; that is determined by the `--server` flag at apply time. See [Compose a graph across runtimes](../guides/compose-a-graph-across-runtimes.md) for a worked example.

### `vais delete <id>`

Evict an agent. Prompts on TTY; pass `--force` in non-interactive contexts (CI).

| Field | Value |
|---|---|
| Arguments | `<id>` — agent id to delete. |
| `--version <value>` | Target a specific version. Default: all versions. |
| `--force` | Skip the interactive confirmation prompt. |
| `--idempotency-key <value>` | Stamp the call. Default: generated UUID. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

### `vais cancel <id>`

Cancel in-flight work on an agent without evicting it.

| Field | Value |
|---|---|
| Arguments | `<id>` — agent id to cancel. |
| `--version <value>` | Target a specific version. |
| `--idempotency-key <value>` | Stamp the call. Default: generated UUID. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

### `vais invoke <id> --text <payload>`

Send a user message to an agent. Unary by default; `--stream` switches to v0.12 SSE.

| Field | Value |
|---|---|
| Arguments | `<id>` — agent id to invoke. |
| `--text <payload>` | **Required.** User message. Supports `@file` prefix — read from disk. |
| `--session <id>` | Correlate with an existing session. Default: new session per call. |
| `--version <value>` | Target a specific version. |
| `--stream` | Use `POST /v1/agents/{id}/invoke/stream` (SSE). Default: unary `POST /invoke`. |
| `--idempotency-key <value>` | Stamp the call. Default: generated UUID. |
| `-o, --output <format>` | `text` / `json`. Default: `text`. Stream mode renders to terminal. |
| Exit codes | `0`, `1`, `2`, `3`, `4`, `130` (on Ctrl-C during `--stream`) |

### `vais logs <id>`

Attach to the live SSE stream for an agent. Renders every `AgentEvent` as it fires.

| Field | Value |
|---|---|
| Arguments | `<id>` — agent id to tail. |
| `--session <id>` | Tail a specific session. Default: latest. |
| `--version <value>` | Target a specific version. |
| `--only <kinds>` | Comma-separated event kinds (kebab-case wire names). Default: all. |
| `--since <iso8601>` | Drop events with `At` earlier than the cutoff. Client-side filter. |
| Exit codes | `0`, `1`, `2`, `3`, `4`, `130` (on Ctrl-C) |

Event kinds: `turn.started`, `turn.completed`, `turn.failed`, `tool.started`, `tool.completed`, `tool.replayed`, `guardrail.triggered`, `interrupt.raised`, `handoff.requested`, `delta`.

### `vais signal <id> --kind <kind> --payload <json>`

Send a signal to an in-flight run (HITL approval delivery, external triggers).

| Field | Value |
|---|---|
| Arguments | `<id>` — agent id to signal. |
| `--kind <kind>` | **Required.** Signal kind (`resume`, `approve`, `cancel-pending`, etc. — consumer-defined). |
| `--payload <json>` | **Required.** JSON payload. Supports `@file.json` prefix — read from disk. |
| `--version <value>` | Target a specific version. |
| `--idempotency-key <value>` | Stamp the call. Default: generated UUID. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

### `vais get-graphs [name]`

List graphs or fetch a single graph manifest (v0.19).

| Field | Value |
|---|---|
| Arguments | `[name]` — optional graph id. Omit to list; provide to fetch one. |
| `--version <value>` | Target a specific version (single-item mode only). |
| `--label-prefix <prefix>` | Filter list results by label prefix. |
| `--limit <n>` | Max entries in list mode. Default: unlimited. |
| `-o, --output <format>` | `table` / `yaml` / `json`. Default: `table` for list, `yaml` for single item. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

### `vais delete-graph <id>`

Evict a graph and cancel all in-flight runs (v0.19). Prompts on TTY; pass `--force` in non-interactive contexts.

| Field | Value |
|---|---|
| Arguments | `<id>` — graph id to delete. |
| `--version <value>` | Target a specific version. |
| `--force` | Skip the interactive confirmation prompt. |
| `--idempotency-key <value>` | Stamp the call. Default: generated UUID. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

### `vais invoke-graph <id>`

Start a graph run (v0.19). Unary by default; `--stream` switches to SSE. Pass `--resume-from <interrupt-id>` + `--run-id <run-id>` to resume an interrupted run.

| Field | Value |
|---|---|
| Arguments | `<id>` — graph id to invoke. |
| `--initial-state <json>` | JSON object merged into the graph's initial state bag. Supports `@file` prefix. |
| `--version <value>` | Target a specific version. |
| `--run-id <id>` | Explicit run id. Required when `--resume-from` is set. |
| `--resume-from <interrupt-id>` | Resume from this interrupt id rather than starting fresh. Requires `--run-id`. |
| `--resume-payload <json>` | JSON payload forwarded to the resume handler. |
| `--stream` | Use the SSE endpoint to stream graph events as they fire. |
| `--idempotency-key <value>` | Stamp the call. Default: generated UUID. |
| `-o, --output <format>` | `text` (plain `lastAssistantText`) / `json` (full `GraphInvocationResult`) / `state` (raw `FinalState` JSON). Default: `text`. Ignored in `--stream` mode. |
| Exit codes | `0`, `1`, `2`, `3`, `4`, `130` (on Ctrl-C during `--stream`) |

### `vais graph-validate -f <file>`

Validate a graph manifest against the runtime's loader + validator without registering it (v0.19). Surfaces every URN the apply path would surface, but no state is mutated.

| Field | Value |
|---|---|
| `-f, --file <path>` | **Required.** Manifest path or `-` for stdin. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais graph-logs <id>`

Stream graph run events via SSE (v0.19). Renders each event with ANSI colors. Pass `--interrupt-id` + `--run-id` to resume and observe an interrupted run. Pass `--from-run-id` to replay stored events for a historical run.

| Field | Value |
|---|---|
| Arguments | `<id>` — graph id to tail. |
| `--initial-state <json>` | JSON object for the run's initial state bag. |
| `--version <value>` | Target a specific version. |
| `--run-id <id>` | Explicit run id. Required when `--interrupt-id` is set. |
| `--interrupt-id <id>` | Resume and observe from this interrupt id. Requires `--run-id`. |
| `--from-run-id <id>` | Replay stored events for the given completed run from the run store instead of streaming live. |
| `--node <id>` | Filter rendered events to one node id (works with both live and replay modes). |
| `--only <kinds>` | Comma-separated graph event kind filter (case-insensitive, kebab-case wire names). Default: all. |
| Exit codes | `0`, `1`, `2`, `3`, `4`, `130` (on Ctrl-C) |

Graph event kinds: `graph.started`, `node.started`, `node.agent_invoked`, `node.completed`, `edge.traversed`, `state.updated`, `graph.interrupted`, `graph.resumed`, `graph.completed`, `graph.failed`.

### `vais get-runs`

Inspect historical graph runs from the run store.

| Field | Value |
|---|---|
| `--graph <id>` | List recent runs for one graph id. Omit to list across all graphs. |
| `--run <id>` | Inspect one run's node executions (mutually exclusive with `--graph`). |
| `--limit <n>` | Max entries. Default: implementation-defined. |
| `-o, --output <format>` | `table` / `yaml` / `json`. Default: `table`. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais get-remote-runtimes`

List the remote runtimes configured on the target runtime host (v0.34). Queries `GET /v1/runtimes`; credentials are intentionally excluded from all responses.

| Field | Value |
|---|---|
| Arguments | — |
| `-o, --output <format>` | `table` / `json`. Default: `table`. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

Table columns: `NAME`, `URL`, `IDENTITY-MODE`.

## Gateways + MCP servers

### `vais get-llm-gateways [id]` / `vais get-mcp-gateways [id]` / `vais get-mcp-servers [id]` / `vais get-eval-suites [id]`

Each follows the same shape as `vais get` — `[id]` argument is optional (omit for list, provide for single fetch); supports `--label-prefix`, `--limit`, and `-o table|yaml|json`.

### `vais llm-gateway-validate -f <file>` / `vais mcp-gateway-validate -f <file>` / `vais mcp-server-validate -f <file>`

Validate the corresponding manifest type against the runtime's loader without registering it. Same flag shape as `vais graph-validate`. Exit codes `0`, `1`, `2`, `4`.

## Plugins

Plugin-init and plugin-build are local; the others hit the runtime.

### `vais plugin-init`

Scaffold a `plugin.yaml` (and `Dockerfile` for dotnet runtimes) in the current directory. Local only.

| Field | Value |
|---|---|
| `--runtime <dotnet\|python>` | Plugin runtime kind. Required. |
| `--name <name>` | Plugin name. Default: directory name. |
| `-o, --output <dir>` | Target directory. Default: current. |
| Exit codes | `0`, `1` |

### `vais plugin-build`

Build a container plugin image via `docker build`. Local only.

| Field | Value |
|---|---|
| `--image <tag>` | **Required.** Image tag (e.g. `ghcr.io/me/my-plugin:1.0`). |
| `--context <dir>` | Docker build context. Default: current directory. |
| `--push` | Run `docker push` after the build. |
| Exit codes | `0`, `1` |

### `vais plugin-push <plugin-name|image>`

Push a plugin to the runtime. Two modes:

- **Source mode** — argument is a simple plugin name (no `/` or `:`). Packs `./src` and `POST`s to `/v1/plugins/{name}/source` for hot reload.
- **Image mode** — argument contains `/` or `:`, or `--image` is supplied. Runs `docker push` and `POST`s to `/v1/plugins/{name}/image`.

| Field | Value |
|---|---|
| Arguments | `<plugin-name>` or `<image>` (see modes above). |
| `--image <tag>` | Force image mode with this tag. |
| `--source <dir>` | Source directory to pack (source mode). Default: `./src`. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais plugin-deploy <release-name>`

Aggregate command — `helm upgrade --install <release-name> <built-in-chart>` against the embedded `vais-plugin` chart, then `POST /v1/container-plugins` to register the plugin with the runtime.

| Field | Value |
|---|---|
| Arguments | `<release-name>` — Helm release name. |
| `--image <tag>` | **Required.** Plugin image. |
| `--namespace <ns>` | K8s namespace. Default: `vais-agents`. |
| `--replicas <n>` | Deployment replicas. Default: 1. |
| `--port <port>` | Plugin container port. Default: 8080. |
| `--image-pull-policy <policy>` | `Always` / `IfNotPresent` / `Never`. Default: `IfNotPresent`. |
| `-f <values.yaml>` | Helm values overrides. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais plugin-status`

List all loaded plugins (assembly, Python, container) with lifecycle state, image, declared handler type names, and PID where applicable.

| Field | Value |
|---|---|
| `-o, --output <format>` | `table` / `yaml` / `json`. Default: `table`. K8s-topology plugins include replica counts. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais plugin-watch <plugin-name>`

Watch a Python plugin's source directory and hot-reload on every change. Local-process loop that pushes to the runtime on each debounced batch.

| Field | Value |
|---|---|
| Arguments | `<plugin-name>` — plugin to push to. |
| `--source <dir>` | Source directory to watch. Default: `./src`. |
| `--debounce <ms>` | Debounce window in ms. Default: 250. |
| Exit codes | `0`, `1`, `130` (Ctrl-C) |

## Eval

### `vais eval run <suite>`

Start a new eval run for a named suite. Prints the `evalRunId`.

| Field | Value |
|---|---|
| Arguments | `<suite>` — suite name. |
| `--version <value>` | Pin to a specific suite version. |
| `--idempotency-key <value>` | Stamp the call. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais eval results <evalRunId>`

Fetch per-case assertion results for an eval run.

| Field | Value |
|---|---|
| Arguments | `<evalRunId>`. |
| `-o, --output <format>` | `table` / `json` / `junit` (JUnit XML for CI). Default: `table`. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais eval list`

List recent eval runs, optionally filtered by suite name.

| Field | Value |
|---|---|
| `--suite <name>` | Filter by suite. |
| `--limit <n>` | Max entries. Default: 20. |
| `-o, --output <format>` | `table` / `json`. Default: `table`. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais eval cancel <evalRunId>`

Request cancellation of an in-progress eval run.

| Field | Value |
|---|---|
| Arguments | `<evalRunId>`. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais eval diff <runA> <runB>`

Compare two eval runs case-by-case and show assertion deltas, with a `DELTA` column for regressions.

| Field | Value |
|---|---|
| Arguments | `<runA> <runB>` — eval run ids. |
| `-o, --output <format>` | `table` / `json` / `junit`. Default: `table`. |
| Exit codes | `0`, `1`, `2`, `4` |

## Diagnose

### `vais diagnose spans`

Fetch recent OTel spans from the in-process buffer as NDJSON. Requires the runtime to be started with `VAIS_DIAG_SPAN_BUFFER=true`; otherwise returns `urn:vais-agents:diag-span-buffer-not-configured`.

| Field | Value |
|---|---|
| `--since <iso8601>` | Only include spans started after the cutoff. |
| `--limit <n>` | Max spans to return. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais diagnose trace <traceId>`

Pretty-print the span tree for a given trace id.

| Field | Value |
|---|---|
| Arguments | `<traceId>`. |
| Exit codes | `0`, `1`, `2`, `4` |

### `vais diagnose filter-status`

Show per-interface outgoing Orleans grain call counters. Useful for diagnosing whether a custom `IAgentFilter` is being hit on the expected code path.

| Field | Value |
|---|---|
| `-o, --output <format>` | `table` / `json`. Default: `table`. |
| Exit codes | `0`, `1`, `2`, `4` |

## `config` subcommands

Local only — mutate / read `~/.vais/config.yaml`. No HTTP.

### `vais config get-contexts`

List all contexts in the current config file as a table (`CURRENT`, `NAME`, `CLUSTER`, `USER`).

| Field | Value |
|---|---|
| Arguments | — |
| Flags | — |
| Exit codes | `0` |

### `vais config current-context`

Print the active context name.

| Field | Value |
|---|---|
| Arguments | — |
| Flags | — |
| Exit codes | `0`, `1` (no context selected) |

### `vais config use-context <name>`

Switch the active context by updating `currentContext` in the config file.

| Field | Value |
|---|---|
| Arguments | `<name>` — context to switch to. |
| Flags | — |
| Exit codes | `0`, `1` (named context doesn't exist) |

### `vais config set-context <name>`

Create or update a context. Creates implicit cluster + user records with the same `<name>`.

| Field | Value |
|---|---|
| Arguments | `<name>` — context to create/update. |
| `--server <url>` | Set the context's cluster server URL. |
| `--token <value>` | Set the user's inline bearer token. Clears `tokenFile`. |
| `--token-file <path>` | Set the user's tokenFile path. Clears inline token. |
| `--insecure-skip-tls-verify` | Disable TLS verification on the cluster. |
| Exit codes | `0`, `1` |

At least one of `--server` / `--token` / `--token-file` must be passed — an empty `set-context` on a new name errors with exit `1`.

## Exit codes

| Code | Name | Fires on |
|---|---|---|
| `0` | `ExitSuccess` | Command succeeded. |
| `1` | `ExitUsageError` | Client-side error — bad flags, missing file, validation failure, `FileNotFoundException` on `@file` dereference. |
| `2` | `ExitApiError` | Non-2xx from control plane (excluding `401` / `403`). `urn:vais-agents:*` URN typically carries the specific failure class. |
| `3` | `ExitPolicyDenied` | `403` with `urn:vais-agents:policy-denied`. |
| `4` | `ExitAuthFailure` | `401` from control plane. |
| `130` | `ExitSigInt` | Ctrl-C during a streaming command (`invoke --stream`, `logs`). |

## `@file` argument convention

`invoke --text @path` and `signal --payload @path` dereference the `@`-prefixed value as a filesystem path and use the file's contents as the argument. The `@` prefix is stripped; missing file → exit `1`.

```bash
vais invoke weather --text @prompts/weather.txt
vais signal run-42 --kind resume --payload @approvals/42.json
```

Not supported on other flags — `apply -f <path>` takes a path directly (no `@` needed); `logs --since <iso>` takes a literal value.

## Output-format flag

`-o, --output <format>` is accepted on most read commands:

| Command | Formats | Default |
|---|---|---|
| `get` (list), `get-graphs` (list), `get-llm-gateways` (list), `get-mcp-gateways` (list), `get-mcp-servers` (list), `get-eval-suites` (list), `plugin-status`, `get-runs`, `get-remote-runtimes`, `diagnose filter-status`, `eval list`, `eval results`, `eval diff` | `table`, `yaml`, `json` (mix varies; see per-command rows) | `table` |
| `get <id>` and the single-item form of every other `get-*` | `yaml`, `json` | `yaml` |
| `invoke` (unary) | `text`, `json` | `text` |
| `invoke-graph` (unary) | `text`, `json`, `state` | `text` |
| `eval results` / `eval diff` (additionally) | `junit` (JUnit XML) | — |
| `invoke --stream`, `invoke-graph --stream`, `logs`, `graph-logs` | — (fixed terminal rendering) | — |
| `init` | — (`-o`/`--output` takes a filesystem path, not a format) | — |

Unrecognised values fall back to the command's default rather than erroring.

## See also

- [CLI concept](../concepts/cli.md) — the big-picture subcommand map + design choices.
- [CLI config file reference](cli-config-file.md) — YAML schema, env-var overrides, token precedence.
- [Install the CLI](../devops/install-the-cli.md) — bootstrap walkthrough.
- [Apply manifests from CI](../guides/apply-manifests-from-ci.md) — `vais apply` + exit-code handling in scripts.
- [Tail live runs with `vais logs`](../guides/tail-live-runs-with-vais-logs.md) — `vais logs` + filters.
