# Reference: CLI subcommands

Full per-command flag + argument + exit-code table for the `vais` CLI (v0.15). Nine top-level verbs + a `config` branch with four sub-verbs = thirteen commands.

Every command (except `version`, `init`, and the `config` branch) supports:

- `--context <name>` — override the active context for this call.
- `--token <value>` — override the resolved bearer token (highest auth precedence).

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

Scaffold a starter agent manifest to stdout or a file. Local only; no HTTP.

| Field | Value |
|---|---|
| Arguments | `<name>` — agent id baked into the manifest's top-level `id` field. |
| `-o, --output <path>` | Write path. Default: stdout. |
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

Create or update an agent manifest. Falls back from `POST` to `PUT` on `409 Conflict`.

| Field | Value |
|---|---|
| Arguments | — |
| `-f, --file <path>` | **Required.** Manifest path (YAML / JSON) or `-` for stdin. |
| `--idempotency-key <value>` | Stamp the call with this key. Default: generated UUID. |
| Exit codes | `0`, `1`, `2`, `3`, `4` |

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

`-o, --output <format>` is accepted on `get`, `invoke`, `init`:

| Command | Formats | Default |
|---|---|---|
| `get` (list) | `table` | `table` |
| `get <id>` | `yaml`, `json` | `yaml` |
| `invoke` (unary) | `text`, `json` | `text` |
| `invoke --stream` | — (fixed terminal rendering) | — |
| `init` | `yaml` (file/stdout) | `yaml` |

Unrecognised values fall back to the command's default rather than erroring.

## See also

- [CLI concept](../concepts/cli.md) — the big-picture subcommand map + design choices.
- [CLI config file reference](cli-config-file.md) — YAML schema, env-var overrides, token precedence.
- [Install the CLI](../getting-started/install-the-cli.md) — bootstrap walkthrough.
- [Apply manifests from CI](../guides/apply-manifests-from-ci.md) — `vais apply` + exit-code handling in scripts.
- [Tail live runs with `vais logs`](../guides/tail-live-runs-with-vais-logs.md) — `vais logs` + filters.
