# Guide: apply manifests from CI

Wire `vais apply -f` into a CI pipeline so every merge to `main` rolls manifest changes out to the target control plane — idempotent, auditable, exit-code-gated. Follows the same declarative-upsert pattern as `kubectl apply -f` but against the Vais.Agents HTTP surface.

Shipped in v0.15 as part of the CLI. `apply` stamps every call with an `Idempotency-Key` and falls back from `POST` (create) to `PUT` (update) on `409 Conflict` — one-shot create-or-update with no prior-state lookup.

## Pipeline shape

A minimal GitHub Actions job:

```yaml
# .github/workflows/apply-agents.yml
name: Apply agent manifests
on:
  push:
    branches: [main]
    paths: ['agents/**.yaml']

jobs:
  apply:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Install vais CLI
        run: dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview

      - name: Configure context
        run: |
          vais config set-context prod \
            --server ${{ secrets.VAIS_SERVER_URL }} \
            --token ${{ secrets.VAIS_TOKEN }}

      - name: Apply every manifest under agents/
        run: |
          for manifest in agents/**/*.yaml; do
            echo "::group::$manifest"
            vais apply -f "$manifest" --context prod
            echo "::endgroup::"
          done
```

Every agent YAML in `agents/` gets applied on push. `set-context` writes `~/.vais/config.yaml` in the runner's home directory — ephemeral per-job, which is what you want.

## Idempotency

`vais apply` auto-generates a fresh `Idempotency-Key` (UUID) per call — unique per apply invocation. Re-running the same CI job within the v0.11 idempotency middleware's 24h TTL would **not** dedupe (different key each time); a retry of the same pipeline step from the same runner generates a new key and goes through.

For true idempotent retries (same payload, same key, no double-execution), pass `--idempotency-key` explicitly:

```bash
# Key derived from git SHA + manifest path — stable across pipeline reruns.
key="$(sha256sum "$manifest" | cut -c1-40)-$GITHUB_SHA"
vais apply -f "$manifest" --idempotency-key "$key"
```

On replay within 24h, the control plane returns the cached response with `Idempotency-Replayed: true`. See [enable HTTP idempotency](enable-http-idempotency.md) for the middleware details.

## Exit-code handling

The CLI's POSIX exit codes map cleanly to bash `case` branches:

```bash
#!/usr/bin/env bash
set -o pipefail

for manifest in agents/**/*.yaml; do
  echo "applying $manifest"
  vais apply -f "$manifest"
  case $? in
    0)
      echo "  ✓ applied"
      ;;
    1)
      echo "  ✗ client-side error — bad manifest?"
      exit 1
      ;;
    2)
      echo "  ✗ server rejected the request (non-2xx)"
      exit 1
      ;;
    3)
      echo "  ✗ policy denied — check Rego"
      exit 1
      ;;
    4)
      echo "  ✗ auth expired — refresh the CI secret"
      exit 1
      ;;
    *)
      echo "  ✗ unexpected failure"
      exit 1
      ;;
  esac
done
```

Quick-fail via `set -e` works for the simple case (any non-zero exit trips the shell), but losing the exit-code-specific branching means every failure looks alike. `case` + explicit `exit 1` is the disciplined pattern.

## Non-interactive delete with `--force`

`vais delete` prompts for confirmation when stdout is a TTY. CI shells are non-interactive — they'd hang waiting for input. Pass `--force` to skip the prompt:

```bash
# Destroy everything in the registry tagged env=staging
for id in $(vais get agents --label-prefix env:staging -o json | jq -r '.[].id'); do
  vais delete "$id" --force
done
```

`--force` is specific to `delete`; other mutating verbs (`apply`, `cancel`, `signal`) don't prompt at all.

## Large payloads via `@file`

`vais invoke` and `vais signal` both accept `@file` — the argument's contents become the value. Useful for long payloads (system prompts, structured JSON for signals) that would be awkward to inline in a shell command:

```bash
# Stash the payload in a file the CI repo checks in.
cat > /tmp/approval.json <<'EOF'
{
  "approved": true,
  "approver_email": "platform@example.com",
  "notes": "Compliance reviewed — ok to proceed."
}
EOF

vais signal workflow-42 --kind resume --payload @/tmp/approval.json
```

`@file` is a CLI-side convention — the file's content is dereferenced before the HTTP call is made. Missing file → exit 1 (`ExitUsageError`).

## Tighten with `--context` per call

CI jobs that touch multiple environments (staging + prod) keep both contexts in the same config file and target each per-call:

```bash
vais config set-context staging --server https://vais.staging.internal --token $STAGING_TOKEN
vais config set-context prod    --server https://vais.prod.internal    --token $PROD_TOKEN

# Apply staging first, verify, then prod.
vais apply -f agents/weather.yaml --context staging
[[ $? -eq 0 ]] || exit 1

echo "smoke-testing staging..."
vais invoke weather --text "healthcheck" --context staging -o json | jq -e '.output'

echo "rolling to prod..."
vais apply -f agents/weather.yaml --context prod
```

`--context` overrides the active context for a single call without switching the file's `currentContext`. Thread-safe for parallel CI jobs sharing a config file.

## Token management

Three equivalent ways to supply auth to the CLI — pick based on how your CI stores secrets:

```bash
# 1. Baked into the config via set-context (visible in the file).
vais config set-context prod --token $VAIS_TOKEN_FROM_CI_SECRET

# 2. Environment variable (never hits the file).
export VAIS_TOKEN=$VAIS_TOKEN_FROM_CI_SECRET
vais apply -f agent.yaml --context prod

# 3. Per-call flag (highest precedence, useful for mixing principals).
vais apply -f agent.yaml --context prod --token $PLATFORM_ADMIN_TOKEN
```

Precedence: flag > env > config. CI secret manager → env var is the cleanest pattern — the token never lands on disk, no cleanup needed between jobs.

## Common issues

- **`Idempotency-Replayed: true` on every re-run.** You passed a stable `--idempotency-key` + the control plane still has the previous response in its 24h window. Expected on retry; surprising if you didn't intend it. Either accept the dedupe or rotate the key.
- **`exit 3` from `vais apply`.** `urn:vais-agents:policy-denied` — the server's policy engine blocked the call. Check the OPA audit log; the Rego said no. See [gate agents with OPA](gate-agents-with-opa.md).
- **`exit 4` on the first call after a token rotation.** The CI secret was updated but the runner picked up the old cached token. Clear the runner's env or trigger a fresh pipeline run.
- **`kubectl`-style context namespacing.** The CLI's `--context` is the Vais context name, unrelated to K8s namespaces. Don't mix the two in your pipeline mental model.
- **`vais apply -f -`** — `apply` supports piping a manifest on stdin via the conventional `-` shorthand:

  ```bash
  envsubst < agent-template.yaml | vais apply -f -
  ```

## Reading apply-time warnings (v0.28)

`vais apply` may succeed (exit `0`) while the server records non-fatal warnings about the manifest. These are surfaced via `IManifestApplyDiagnosticsSink` and returned in the HTTP response body as a `diagnostics` array.

Example warning — manifest sets both `handler.typeName` and `model`, which is ambiguous:

```json
{
  "id": "planner-agent",
  "version": "1.0",
  "diagnostics": [
    {
      "severity": "Warning",
      "urn": "urn:vais-agents:handler-and-declarative-fields-both-set",
      "message": "Both handler.typeName and model are set. The plugin handler wins; model/systemPromptSpec/tools/guardrails are ignored."
    }
  ]
}
```

The CLI prints warnings to stderr when they are present; the exit code is still `0`. In CI, capture stderr to a log file so these do not go unnoticed:

```bash
vais apply -f agent.yaml 2>apply-warnings.log
cat apply-warnings.log | grep -i warning && echo "Review apply warnings above"
```

To inspect the raw JSON response with full `diagnostics` detail, use `--output json` (if supported) or call the HTTP endpoint directly and inspect the response body.

## See also

- [Install the CLI](../devops/install-the-cli.md) — bootstrap + config-file first-run.
- [CLI concept](../concepts/cli.md) — full subcommand map, auth precedence, exit codes.
- [CLI subcommands reference](../reference/cli-subcommands.md) — per-command flag tables.
- [Enable HTTP idempotency](enable-http-idempotency.md) — the v0.11 middleware `apply` depends on.
- [Problem-details URNs](../reference/problem-details-urns.md) — the server errors the CLI maps to exit codes 2-4.
