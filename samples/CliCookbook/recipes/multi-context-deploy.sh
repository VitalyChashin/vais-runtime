#!/usr/bin/env bash
# multi-context-deploy.sh — apply a manifest to staging, smoke-test, then promote to prod.
#
# Usage: ./multi-context-deploy.sh --manifest <file> [--agent-id <id>]
#   --manifest   required — agent manifest YAML to deploy
#   --agent-id   agent id for the smoke-test invoke (default: extracted from manifest)
#
# Expects env vars:
#   STAGING_SERVER   URL of the staging runtime (e.g. https://vais.staging.internal)
#   STAGING_TOKEN    bearer token for staging
#   PROD_SERVER      URL of the prod runtime (e.g. https://vais.prod.internal)
#   PROD_TOKEN       bearer token for prod
#
# Exit: 0 on successful prod deploy; 1 on any failure.

set -o pipefail

MANIFEST=""
AGENT_ID=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --manifest) MANIFEST="$2";  shift 2 ;;
    --agent-id) AGENT_ID="$2";  shift 2 ;;
    *)          echo "Unknown option: $1"; exit 1 ;;
  esac
done

[[ -n "$MANIFEST" ]] || { echo "--manifest is required"; exit 1; }
[[ -f "$MANIFEST" ]] || { echo "File not found: $MANIFEST"; exit 1; }

# Derive agent id from manifest if not provided.
if [[ -z "$AGENT_ID" ]]; then
  AGENT_ID=$(grep -m1 '^\s*id:' "$MANIFEST" | awk '{print $2}' | tr -d '"')
fi
[[ -n "$AGENT_ID" ]] || { echo "Cannot determine agent id from $MANIFEST"; exit 1; }

: "${STAGING_SERVER:?STAGING_SERVER env var is required}"
: "${STAGING_TOKEN:?STAGING_TOKEN env var is required}"
: "${PROD_SERVER:?PROD_SERVER env var is required}"
: "${PROD_TOKEN:?PROD_TOKEN env var is required}"

# ---- configure contexts (ephemeral — safe to overwrite each run) ----
echo "Configuring contexts …"
vais config set-context staging --server "$STAGING_SERVER" --token "$STAGING_TOKEN"
vais config set-context prod    --server "$PROD_SERVER"    --token "$PROD_TOKEN"

# ---- stage 1: apply to staging ----
echo ""
echo "Stage 1 — applying to staging …"
vais apply -f "$MANIFEST" --context staging
[[ $? -eq 0 ]] || { echo "✗ Staging apply failed."; exit 1; }
echo "  ✓ applied to staging"

# ---- stage 2: smoke test on staging ----
echo ""
echo "Stage 2 — smoke-testing staging ($AGENT_ID) …"
response=$(vais invoke "$AGENT_ID" --text "healthcheck" --context staging -o json 2>&1)
invoke_exit=$?

if [[ $invoke_exit -ne 0 ]]; then
  echo "  ✗ Smoke-test invoke failed (exit $invoke_exit)."
  echo "  Response: $response"
  echo "  Aborting before prod deploy."
  exit 1
fi

echo "  ✓ staging smoke-test passed"
echo "  Response: $response"

# ---- stage 3: promote to prod ----
echo ""
echo "Stage 3 — promoting to prod …"
vais apply -f "$MANIFEST" --context prod
[[ $? -eq 0 ]] || { echo "✗ Prod apply failed. Staging is ahead — investigate."; exit 1; }
echo "  ✓ deployed to prod"

echo ""
echo "Promotion complete: staging → prod  ✓"
