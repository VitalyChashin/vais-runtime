# Reference: CLI config file

The `vais` CLI stores connection settings in a kubectl-shape YAML file. Structure: three parallel lists (`clusters`, `users`, `contexts`) joined by name reference, plus a `currentContext` pointer.

## File location

| Platform | Default path |
|---|---|
| Linux / macOS | `$HOME/.vais/config.yaml` |
| Windows | `%USERPROFILE%\.vais\config.yaml` |

Override with the `VAIS_CONFIG` environment variable. The CLI calls `VaisConfigFile.ResolveConfigPath()` on every invocation — no caching; edits land immediately.

Missing file → empty in-memory config (no contexts, no current context). The first `vais config set-context` call materialises the file on disk.

## Top-level schema

```yaml
apiVersion: vais.io/v1      # required — locked at "vais.io/v1" for v0.15
kind: Config                # required — locked at "Config"
currentContext: <name>      # required when any commands need an active context
clusters:                   # list of VaisCluster records
  - …
users:                      # list of VaisUser records
  - …
contexts:                   # list of VaisContext records
  - …
```

The kubeconfig-shape join model — a `context` references a `cluster` + a `user` by name. Cluster and user records can be reused across multiple contexts.

## `clusters[]`

```yaml
- name: local
  server: http://localhost:5080
  insecureSkipTlsVerify: false     # optional, default false
```

| Field | Type | Required | Purpose |
|---|---|---|---|
| `name` | string | ✓ | Unique identifier referenced by `contexts[].cluster`. |
| `server` | string | ✓ | Base URL. `http://` or `https://`. No trailing slash convention — the CLI tolerates both. |
| `insecureSkipTlsVerify` | bool | — | Disable TLS verification. Default `false`. Use only in dev against self-signed certs. |

## `users[]`

```yaml
- name: local
  token: "sk_live_abc123…"         # inline bearer token
# OR
- name: sa
  tokenFile: /var/run/secrets/tokens/vais-token    # token read per call from disk
```

| Field | Type | Required | Purpose |
|---|---|---|---|
| `name` | string | ✓ | Unique identifier referenced by `contexts[].user`. |
| `token` | string | — | Inline bearer token. Mutually exclusive with `tokenFile` — `vais config set-context --token` clears any existing `tokenFile`. |
| `tokenFile` | string | — | Path to a file whose contents are the bearer token. Re-read on every CLI invocation, trimmed. Use this for short-lived tokens rotated out-of-band. |

Token fields are optional — a user with neither produces an unauthenticated outbound call. Useful for dev / anonymous endpoints.

## `contexts[]`

```yaml
- name: prod
  cluster: prod-cluster
  user: prod-sa
```

| Field | Type | Required | Purpose |
|---|---|---|---|
| `name` | string | ✓ | Unique identifier. Referenced by `currentContext` + `--context <name>` flag. |
| `cluster` | string | ✓ | Must match one of `clusters[].name`. |
| `user` | string | ✓ | Must match one of `users[].name`. |

Dangling references (context points at a missing cluster / user name) are caught when the context is activated — `vais get agents` against a broken context fails with exit `1` (`ExitUsageError`).

## `currentContext`

```yaml
currentContext: prod
```

Top-level string referencing one of `contexts[].name`. Selected by `vais config use-context <name>` or manual edits. Changed by every `use-context` call. Empty / null means no active context — every command must pass `--context <name>` explicitly, or falls through to unauthenticated + default server (rare — typically used only by `vais config` subcommands themselves).

## Full example

```yaml
apiVersion: vais.io/v1
kind: Config
currentContext: prod
clusters:
  - name: local
    server: http://localhost:5080
  - name: prod-cluster
    server: https://vais.prod.example.com
    insecureSkipTlsVerify: false
  - name: staging-cluster
    server: https://vais.staging.example.com
users:
  - name: local-dev
    token: dev-token-abc123
  - name: prod-sa
    tokenFile: /var/run/secrets/tokens/vais-prod-token
  - name: staging-sa
    tokenFile: /var/run/secrets/tokens/vais-staging-token
contexts:
  - name: local
    cluster: local
    user: local-dev
  - name: prod
    cluster: prod-cluster
    user: prod-sa
  - name: staging
    cluster: staging-cluster
    user: staging-sa
```

Switch between environments:

```bash
vais config use-context staging
vais get agents
vais config use-context prod
vais apply -f weather.yaml
```

## Environment variables

| Variable | Purpose | Precedence |
|---|---|---|
| `VAIS_CONFIG` | Override the config-file path. | N/A — affects config loading, not auth. |
| `VAIS_TOKEN` | Bearer token. | Wins over context's `token` / `tokenFile`. Loses to `--token` flag. |

No other env vars consumed.

## Token-precedence chain

Outbound `Authorization: Bearer <token>` header resolution, first match wins:

1. `--token <value>` flag on the command line.
2. `$VAIS_TOKEN` environment variable.
3. Active context's user record: `token` field (inline).
4. Active context's user record: `tokenFile` field — re-read + trimmed on every invocation.
5. Unauthenticated (no `Authorization` header).

Each resolution happens per-command — the CLI doesn't cache the resolved token across calls within a shell. `tokenFile` specifically reads the file on each invocation, supporting out-of-band rotation without CLI restart.

## `set-context` flag mapping

| Flag | Writes to | Clears |
|---|---|---|
| `--server <url>` | `clusters[name].server` | — |
| `--token <value>` | `users[name].token` | `users[name].tokenFile` |
| `--token-file <path>` | `users[name].tokenFile` | `users[name].token` |
| `--insecure-skip-tls-verify` | `clusters[name].insecureSkipTlsVerify` | — |

`set-context <name>` with no flags errors with exit `1` — an empty update on a new name isn't useful. Existing contexts can be updated in place by passing only the field(s) you want to change — other fields retain prior values.

Implicit cluster + user records: `vais config set-context prod --server ... --token ...` creates a cluster named `prod` + a user named `prod` + a context that joins them. For more complex topologies (one cluster + multiple users, or one user + multiple clusters), edit the YAML directly — the split-fields shape is designed for reuse, but `set-context` only writes 1:1 records.

## Migrating / sharing configs

The config file is self-contained — copy it between machines or commit (with secrets redacted) to provision new shells:

```bash
# Bootstrap a fresh machine from a template
cp ~/.vais/config.yaml /tmp/vais-template.yaml
# ... remove token fields, check in ...

# On the new machine
cp team-config.yaml ~/.vais/config.yaml
export VAIS_TOKEN=$(pass show vais/prod-token)
vais get agents --context prod
```

Secrets belong in `tokenFile` (out-of-band) or `$VAIS_TOKEN` (session-scoped), never checked in.

## See also

- [CLI concept](../concepts/cli.md) — subcommand map + auth precedence in context.
- [CLI subcommands reference](cli-subcommands.md) — full per-command flag table.
- [Install the CLI](../devops/install-the-cli.md) — first-run bootstrap walkthrough.
- Kubernetes `kubeconfig` docs — the shape this file mirrors. Most kubeconfig patterns (context switching, env-var overrides) translate directly.
