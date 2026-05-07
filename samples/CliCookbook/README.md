# CliCookbook

Copy-paste recipes for common `vais` CLI workflows — CI/CD apply pipelines, rollback on failure, log tailing + filtering, and multi-context staging→prod promotion. Every recipe is a self-contained shell script in `recipes/`.

> **Doc-only sample.** No C# to build; `vais` CLI must be installed (`dotnet tool install -g Vais.Agents.Cli`).

## Recipes

| Script | What it does |
|---|---|
| [`apply-from-ci.sh`](recipes/apply-from-ci.sh) | Apply all manifests under `agents/`; branch on POSIX exit codes; fail fast on non-zero |
| [`rollback-on-failed-apply.sh`](recipes/rollback-on-failed-apply.sh) | Apply a new version; on failure delete it and re-apply the previous version |
| [`stream-and-filter.sh`](recipes/stream-and-filter.sh) | Tail `turn.completed` + `tool.completed` events; alert on tool failures; sample usage stats |
| [`multi-context-deploy.sh`](recipes/multi-context-deploy.sh) | Configure staging + prod contexts; apply to staging, smoke-test, then promote to prod |

## Sample config files

`sample-configs/` contains three starter `~/.vais/config.yaml` files. Copy the one that fits your setup to `~/.vais/config.yaml`:

| File | Scenario |
|---|---|
| [`single-context.yaml`](sample-configs/single-context.yaml) | One runtime, inline bearer token |
| [`multi-context.yaml`](sample-configs/multi-context.yaml) | Staging + prod runtimes, per-context tokens |
| [`token-file-rotation.yaml`](sample-configs/token-file-rotation.yaml) | Token rotated by an external agent; CLI re-reads the file on every call |

## Quick-start

Install the CLI and point it at a running runtime:

```bash
dotnet tool install -g Vais.Agents.Cli

# Configure a local dev context
vais config set-context local \
  --server http://localhost:5080 \
  --token dev-token

vais config use-context local
vais get                          # list agents — should return empty table
```

Then run any recipe:

```bash
bash samples/CliCookbook/recipes/apply-from-ci.sh
```

## Exit codes

All recipes use the CLI's POSIX exit-code taxonomy:

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Client-side error (bad flags, missing file) |
| `2` | Server rejected the request (non-2xx) |
| `3` | Policy denied (`urn:vais-agents:policy-denied`) |
| `4` | Auth failure (401) |
| `130` | Ctrl-C during a streaming command |

## Docs

- [CLI concept](../../docs/concepts/cli.md) — auth precedence, output formats, exit-code design
- [CLI subcommands reference](../../docs/reference/cli-subcommands.md) — per-command flag tables
- [Apply manifests from CI](../../docs/guides/apply-manifests-from-ci.md) — full CI/CD guide
- [Tail live runs with `vais logs`](../../docs/guides/tail-live-runs-with-vais-logs.md) — `--only`, `--since`, Ctrl-C
- [`OpaPolicyGateLocal`](../OpaPolicyGateLocal) — policy gate that produces exit 3
