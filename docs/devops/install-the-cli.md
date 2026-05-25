# Install the CLI

`Vais.Agents.Cli` ships as a `dotnet tool` — one install, one `vais` command on your PATH. ~41 commands (29 top-level + `eval` / `diagnose` / `config` branches) covering the HTTP control plane's verb surface, a kubectl-shape config file for switching between hosts, and POSIX exit codes for scripting.

Initial CLI pillar shipped in v0.15; subsequent pillars (graphs, plugins, eval, gateway config) layered commands onto the same surface. Package name on NuGet: `Vais.Agents.Cli` → command on PATH: `vais`.

## Prerequisites

- **.NET 10 SDK** — the tool is published as `net10.0`.
- **An HTTP control plane reachable from your machine** — local `dotnet run` on `http://localhost:5080`, or a cluster-deployed host. The CLI is a pure client; without a backing server it can only bootstrap its config and print its version.

## Install

```bash
dotnet tool install -g Vais.Agents.Cli --prerelease
```

`--global` (`-g`) puts `vais` on PATH under your user profile — typically `~/.dotnet/tools/` on Linux/macOS, `%USERPROFILE%\.dotnet\tools\` on Windows. For per-project scoped installs use `--tool-path <dir>`; for project-local tool manifests use `dotnet new tool-manifest && dotnet tool install Vais.Agents.Cli`.

Update:

```bash
dotnet tool update -g Vais.Agents.Cli
```

Uninstall:

```bash
dotnet tool uninstall -g Vais.Agents.Cli
```

Verify:

```bash
vais version
# 0.15.0-preview
```

If `vais` isn't on PATH after install, make sure `~/.dotnet/tools` is in your shell profile.

## Bootstrap `~/.vais/config.yaml`

The CLI stores connection settings in a kubectl-shape YAML file. First run from an empty config writes the defaults:

```bash
vais config set-context local \
  --server http://localhost:5080 \
  --token "$(cat ./dev-bearer.txt)"
# created context 'local' with cluster 'local' + user 'local'
```

Under the hood this creates `~/.vais/config.yaml`:

```yaml
apiVersion: vais.io/v1
kind: Config
currentContext: local
clusters:
  - name: local
    server: http://localhost:5080
users:
  - name: local
    token: "<redacted>"
contexts:
  - name: local
    cluster: local
    user: local
```

Full schema + field-by-field reference: see [CLI config file reference](../reference/cli-config-file.md).

### Multiple contexts

Add another context for a staging cluster:

```bash
vais config set-context staging \
  --server https://vais.staging.internal \
  --token-file ~/.secrets/vais-staging.token
```

Switch active context:

```bash
vais config use-context staging
vais config current-context
# staging
```

List all contexts:

```bash
vais config get-contexts
# CURRENT   NAME       CLUSTER    USER
#           local      local      local
#   *       staging    staging    staging
```

## Verify against a running control plane

Start a local control plane (see [`docs/concepts/control-plane.md`](../concepts/control-plane.md)), then:

```bash
# Smoke check — fetches the agent list (empty on a fresh server).
vais get agents
# No agents registered. Use `vais apply -f agent.yaml` to register one.

# Register a sample agent from a YAML file.
cat > weather.yaml <<'EOF'
id: weather
version: "1.0"
handler:
  typeName: MyApp.WeatherAgent
protocols:
  - kind: Http
tools: []
EOF

vais apply -f weather.yaml
# ✓ weather:1.0 applied (created)

vais get agents
# NAME      VERSION   PHASE    AGE
# weather   1.0       Active   3s

# Invoke it (unary).
vais invoke weather --text "What's the weather in Tokyo?"
# It's 18°C and sunny in Tokyo.
```

## Auth precedence

The CLI resolves the outbound bearer token in this order (first match wins):

1. `--token <value>` flag on the command line.
2. `$VAIS_TOKEN` environment variable.
3. Active context's user record — `token` (inline) or `tokenFile` (re-read per invocation).
4. Unauthenticated (no `Authorization` header).

Short-lived tokens live better as `$VAIS_TOKEN` (exported from a secret manager) or `tokenFile` (re-read every call). Long-lived dev tokens are fine inline. Never commit `config.yaml` with an inline token to a shared repo — `token: "sk_live_…"` lands in git history the same way every secret does.

## Environment variables

| Variable | Purpose |
|---|---|
| `VAIS_CONFIG` | Override the config-file path. Default: `$HOME/.vais/config.yaml` (`%USERPROFILE%\.vais\config.yaml` on Windows). |
| `VAIS_TOKEN` | Bearer token. Second in the precedence chain. |

No other env vars are consumed. Flags always win over env vars; env vars always win over the config file.

## Exit codes

Standard POSIX shape for shell-friendly scripting:

| Code | Name | Meaning |
|---|---|---|
| `0` | `ExitSuccess` | Command succeeded. |
| `1` | `ExitUsageError` | Bad flags, missing file, client-side validation failure. |
| `2` | `ExitApiError` | Non-2xx from control plane (excluding 401 / 403). |
| `3` | `ExitPolicyDenied` | `403 urn:vais-agents:policy-denied`. |
| `4` | `ExitAuthFailure` | `401` from control plane. |
| `130` | `ExitSigInt` | Ctrl-C during streamed command. |

CI scripts can branch cleanly:

```bash
vais apply -f agent.yaml
case $? in
  0)   echo "applied" ;;
  2|3) echo "control-plane said no — don't retry"; exit 1 ;;
  4)   echo "auth expired — refresh token"; exit 1 ;;
  *)   echo "unexpected failure — investigate"; exit 1 ;;
esac
```

## What's next

- [CLI concept](../concepts/cli.md) — subcommand map, auth precedence, output formats, `@file` argument convention.
- [CLI subcommands reference](../reference/cli-subcommands.md) — full per-command flag / argument / exit-code table.
- [CLI config file reference](../reference/cli-config-file.md) — YAML schema + env-var overrides + token-precedence chain.
- [Apply manifests from CI](../guides/apply-manifests-from-ci.md) — scripted `vais apply -f` patterns.
- [Tail live runs with `vais logs`](../guides/tail-live-runs-with-vais-logs.md) — SSE attach + client-side filters.
