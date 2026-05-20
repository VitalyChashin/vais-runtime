# CLI

`Vais.Agents.Cli` — a `dotnet tool` that wraps the HTTP control plane in a kubectl-shape command-line surface. Shipped in v0.15 as a preview; subsequent pillars (v0.18 plugins, v0.19 graphs, v0.20 cross-runtime refs, eval harness, gateway config) layered commands onto the same shape. Today: ~30 top-level commands plus five branches (`eval`, `ext`, `agent`, `diagnose`, `config`). POSIX exit codes. Kubeconfig-style `~/.vais/config.yaml` for context switching.

Install: `dotnet tool install -g Vais.Agents.Cli` → `vais` on PATH. See [install the CLI](../devops/install-the-cli.md) for the bootstrap walkthrough.

## Subcommand map

Grouped by functional area. Every command targets the HTTP control plane unless flagged `— (local)`. Run `vais --help` for the live, version-correct verb list.

### Local / scaffolding

| Command | Purpose |
|---|---|
| `vais version` | Print the CLI version. *(local)* |
| `vais init <name>` | Scaffold a starter agent-manifest YAML. *(local)* |
| `vais plugin-init` | Scaffold a `plugin.yaml` (and `Dockerfile` for dotnet) in the current directory. *(local)* |
| `vais plugin-build` | Build a container plugin image via `docker build`. `--push` to publish. *(local)* |

### Agents

| Command | HTTP target |
|---|---|
| `vais apply -f <file> [--idempotency-key]` | `POST /v1/apply` — mixed-kind YAML (Agents, Graphs, LlmGateway, McpGateway, McpServer, ContainerPlugin, EvalSuite, Extension) applied in dependency order. |
| `vais get [name] [--version] [--label-prefix] [--limit]` *(alias `list`)* | `GET /v1/agents` or `GET /v1/agents/{id}` |
| `vais delete <id> [--version] [--force]` | `DELETE /v1/agents/{id}` |
| `vais cancel <id> [--version]` | `POST /v1/agents/{id}/cancel` |
| `vais invoke <id> --text <payload> [--stream]` | `POST /v1/agents/{id}/invoke` (or `…/invoke/stream`) |
| `vais logs <id> [--session] [--only] [--since]` | SSE attach to `/v1/agents/{id}/invoke/stream` |
| `vais signal <id> --kind <kind> --payload <json>` | `POST /v1/agents/{id}/signal` |

### Graphs

| Command | HTTP target |
|---|---|
| `vais get-graphs [id]` | `GET /v1/graphs` or `GET /v1/graphs/{id}` |
| `vais delete-graph <id> [--force]` | `DELETE /v1/graphs/{id}` |
| `vais graph-validate -f <file>` | Validate a graph manifest without registering it. |
| `vais invoke-graph <id> [--stream] [--resume-from <runId>]` | `POST /v1/graphs/{id}/invoke` (or `…/invoke/stream`). `--resume-from` resumes an interrupted run. |
| `vais graph-logs <id> [--from-run-id <runId>]` | SSE tail of `AgentGraphEvent`s. `--from-run-id` replays stored history. |
| `vais get-runs [--graph <id>] [--run <runId>]` | List historical graph runs or inspect one run's node executions. |

### Gateways + MCP servers

| Command | Purpose |
|---|---|
| `vais get-llm-gateways [id]` | List LLM gateway configs, or fetch one. |
| `vais get-mcp-gateways [id]` | List MCP gateway configs, or fetch one. |
| `vais get-mcp-servers [id]` | List MCP server manifests, or fetch one. |
| `vais llm-gateway-validate -f <file>` | Validate an LLM gateway config manifest. |
| `vais mcp-gateway-validate -f <file>` | Validate an MCP gateway config manifest. |
| `vais mcp-server-validate -f <file>` | Validate an MCP server manifest. |

### Plugins (v0.18 + container plugin CLI)

| Command | Purpose |
|---|---|
| `vais plugin-status` | List loaded plugins (assembly, Python, container) with lifecycle state, image, handlers, PID. |
| `vais plugin-push` | Push a plugin to the runtime. Source mode packs `./src` and hot-reloads; image mode `docker push` + `POST /v1/plugins/{name}/image`. |
| `vais plugin-deploy` | Deploy a container plugin to Kubernetes via the built-in Helm chart (`helm upgrade --install`). |
| `vais plugin-watch` | Watch a Python plugin's source directory and hot-reload on every change. |
| `vais plugin-import-existing` | Load (or hot-reload) a plugin whose DLL is already in the runtime's plugins directory. |

### Extensions (`ext` + `agent` branches)

| Command | Purpose |
|---|---|
| `vais ext list` | List loaded extensions with host, version, and handler/seam summary. |
| `vais ext get <id>` | Fetch a single loaded extension (full manifest + handler details). |
| `vais ext logs <id>` | Show container-extension logs (`host: container` only; redirects to docker/kubectl). |
| `vais ext metrics <id>` | Per-handler latency metrics (p50/p95) for a loaded extension. |
| `vais agent extensions <id>` | List extension handlers bound to an agent, with scope-match diagnostics. |

Extensions are applied with `vais apply -f` (`kind: Extension`) and removed with `vais delete extensions/<id>`.

### Eval harness (`eval` branch)

| Command | Purpose |
|---|---|
| `vais get-eval-suites [id]` | List `EvalSuite` manifests, or fetch one. |
| `vais eval run <suite>` | Start a new eval run. Prints `evalRunId`. |
| `vais eval results <evalRunId>` | Fetch per-case assertion results. |
| `vais eval list [--suite <name>]` | List recent eval runs. |
| `vais eval cancel <evalRunId>` | Request cancellation of an in-progress run. |
| `vais eval diff <runA> <runB>` | Compare two runs case-by-case with assertion deltas. |

### Topology

| Command | Purpose |
|---|---|
| `vais get-remote-runtimes` | List remote runtimes configured on the target host (v0.20 cross-runtime refs). |

### Diagnostics (`diagnose` branch)

| Command | Purpose |
|---|---|
| `vais diagnose spans [--since]` | Fetch recent OTel spans from the in-process buffer as NDJSON. Requires `VAIS_DIAG_SPAN_BUFFER=true`. |
| `vais diagnose trace <traceId>` | Pretty-print a span tree for a trace id. |
| `vais diagnose filter-status` | Show per-interface outgoing Orleans grain call counters. |

### Local config (`config` branch — no HTTP)

| Command | Purpose |
|---|---|
| `vais config get-contexts` | Table of all contexts. |
| `vais config current-context` | Print active context name. |
| `vais config use-context <name>` | Switch active context. |
| `vais config set-context <name> [--server] [--token] [--token-file] [--insecure-skip-tls-verify]` | Create or update a context + implicit cluster/user records. |

Every HTTP-bound command accepts `--context <name>` to override the active context for that call and `--token <value>` to override auth. Local commands (`version`, `init`, `plugin-init`, `plugin-build`, the `config` branch) ignore both.

See [CLI subcommands reference](../reference/cli-subcommands.md) for the full per-command flag table.

## Config file

Lives at `$HOME/.vais/config.yaml` (`%USERPROFILE%\.vais\config.yaml` on Windows); override with `VAIS_CONFIG`. Structure mirrors kubectl's kubeconfig — three parallel lists of `clusters` + `users` + `contexts` joined by reference:

```yaml
apiVersion: vais.io/v1
kind: Config
currentContext: local
clusters:
  - name: local
    server: http://localhost:5080
  - name: prod
    server: https://vais.prod.example.com
    insecureSkipTlsVerify: false
users:
  - name: local
    token: dev-token-for-localhost
  - name: prod-sa
    tokenFile: /var/run/secrets/tokens/vais-token
contexts:
  - name: local
    cluster: local
    user: local
  - name: prod
    cluster: prod
    user: prod-sa
```

Switch with `vais config use-context prod`; the file's `currentContext` updates in place. The split-fields shape means a user record can be reused across clusters (SSO-backed token file pointing at the same OIDC issuer) or a cluster can host multiple tenant identities.

See [CLI config file reference](../reference/cli-config-file.md) for the full schema.

## Auth precedence

Bearer token resolution, first match wins:

1. `--token <value>` flag on the command line.
2. `$VAIS_TOKEN` environment variable.
3. Active context's user record — `token` (inline) or `tokenFile` (read per invocation).
4. Unauthenticated (no `Authorization` header).

Flags always beat env vars; env vars always beat the config file. Resolution happens per-command — in a shell where `VAIS_TOKEN` is exported, a one-off `vais invoke … --token <alternate>` overrides without needing to unset the env var.

## Exit codes

POSIX-standard shape for script-friendly branching:

| Code | Name | Fires on |
|---|---|---|
| `0` | `ExitSuccess` | Command succeeded. |
| `1` | `ExitUsageError` | Bad flags, missing file, client-side validation failure. |
| `2` | `ExitApiError` | Non-2xx from control plane (excluding 401 / 403). |
| `3` | `ExitPolicyDenied` | `403` + `urn:vais-agents:policy-denied`. |
| `4` | `ExitAuthFailure` | `401` from control plane. |
| `130` | `ExitSigInt` | Ctrl-C during a streaming command (`invoke --stream`, `logs`). |

The exit-code taxonomy is stable — scripts can discriminate between "the server said no" (`2`, `3`), "my auth is broken" (`4`), "I killed the stream" (`130`), and "I typo'd a flag" (`1`). Broad `exit 1 || exit 2` fallthrough is not sufficient for CI gating; the specific code matters.

See [CLI config file reference](../reference/cli-config-file.md) for the full exit-code table + CI script examples.

## Output formats

`-o` / `--output` flag controls rendering on `get` + `invoke` + `init`:

| Value | Available on | Default on |
|---|---|---|
| `table` | `get` (list) | `get` (list) |
| `yaml` / `yml` | `get` (single item), `init` | `get` (single item) |
| `json` | `get` (single item), `invoke` | — |
| `text` | `invoke` | `invoke` |

Unrecognised values fall back to the command's default rather than erroring — `vais get weather -o xml` prints YAML (the default for single-item `get`) with no flag-parse failure.

`get` without an argument lists; with an argument fetches a single manifest. Output-format defaults flip accordingly — `get` without args defaults to `table`, `get <id>` defaults to `yaml`. Override with `-o json` for jq-friendly piping:

```bash
vais get weather -o json | jq '.spec.tools[].name'
# "get_weather"
# "format_forecast"
```

## `@file` argument convention

`invoke --text` and `signal --payload` both accept `@file` — an argument starting with `@` reads the remainder as a filesystem path and uses the file's contents as the argument value:

```bash
# Inline
vais invoke weather --text "What's the weather in Tokyo?"

# From a file (useful for multi-line payloads or large JSON)
vais invoke weather --text @prompts/weather-query.txt
vais signal workflow-123 --kind resume --payload @approval.json
```

The `@` prefix is a CLI-side convention — not an HTTP-level feature. The file's content is dereferenced before the call is made. `FileNotFoundException` → exit 1 (`ExitUsageError`).

## `apply -f` — declarative upsert

`vais apply -f <file>` reads a manifest (YAML or JSON), then:

1. Attempts `CreateAsync` against the control plane with the manifest's `id` + `version`.
2. On `409 Conflict` (manifest already exists), falls back to `UpdateAsync` without re-prompting the caller.
3. Stamps every call with `Idempotency-Key` — generated as a fresh UUID unless `--idempotency-key` is provided. This combines with the v0.11 idempotency middleware so apply-replays are safe inside the 24h TTL.

One-shot create-or-update — same flow as `kubectl apply -f`. The caller doesn't have to know whether the manifest already exists.

Stdin is supported via `-f -`:

```bash
cat agent.yaml | vais apply -f -
```

See [apply manifests from CI](../guides/apply-manifests-from-ci.md) for the script-patterns guide.

## `vais logs` — SSE attach

`vais logs <agent-id>` attaches to the v0.12 `POST /v1/agents/{id}/invoke/stream` endpoint and renders every event to the terminal:

```
▶  10:15:00.123  turn.started
…  10:15:00.987  delta              "It's 18°C and sunny"
✓  10:15:01.234  tool.completed     get_weather (success)
◀  10:15:02.500  turn.completed     42 prompt → 12 completion tokens
```

`--only <kind1,kind2,…>` filters client-side (comma-separated event names from the SSE wire taxonomy — `turn.started`, `tool.completed`, etc.). `--since <iso-8601>` drops events with `At` earlier than the cutoff. Ctrl-C unblocks the `await foreach`, shuts down the SSE parser cleanly, and exits with code `130`.

See [tail live runs with `vais logs`](../guides/tail-live-runs-with-vais-logs.md) for the full walkthrough.

## When to use the CLI vs. `AgentControlPlaneClient`

- **Use the CLI** for interactive exploration, manifest apply from CI scripts, log tailing during debugging, ad-hoc invocations.
- **Use `AgentControlPlaneClient` directly** (library via `Vais.Agents.Control.Http.Client`) from .NET services, backgrounds jobs, any caller already running inside a .NET process.

The CLI is a thin shell over the library; anything the CLI does, `AgentControlPlaneClient` can do too.

## Limitations

- **No `dry-run` mode on `apply`.** Preview / diff against a hypothetical create-or-update lands in a later pillar. Today the flow is "apply + inspect status."
- **No paging on `get` / `get-graphs`.** `--limit` caps a single call at `N` entries; there's no continuation token. Large registries return the first `N`.
- **No shell completion.** Tab-completion scripts for bash/zsh/pwsh are deferred. Spectre.Console has a path forward; not shipped today.
- **No diff view on apply.** The update fallback prints a terse `applied (updated)`; seeing the before/after shape diff takes a separate `vais get <id> -o yaml` call.

## See also

- [Install the CLI](../devops/install-the-cli.md) — bootstrap walkthrough.
- [CLI subcommands reference](../reference/cli-subcommands.md) — per-command flag + argument + exit-code table.
- [CLI config file reference](../reference/cli-config-file.md) — YAML schema, env-var overrides, token precedence.
- [Apply manifests from CI](../guides/apply-manifests-from-ci.md) — `vais apply -f` in shell scripts.
- [Tail live runs with `vais logs`](../guides/tail-live-runs-with-vais-logs.md) — SSE attach + filters.
- [Control plane concept](control-plane.md) — the HTTP surface the CLI wraps.
- [Problem-details URNs](../reference/problem-details-urns.md) — the error shapes the CLI maps to exit codes 2-4.
