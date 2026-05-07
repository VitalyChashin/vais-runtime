#!/usr/bin/env bash
# apply-from-ci.sh — apply every manifest under agents/ with exit-code branching.
#
# Usage: ./apply-from-ci.sh [--context <ctx>] [--dir <path>]
#   --context  vais context name (default: reads currentContext from config)
#   --dir      directory to scan for YAML manifests (default: agents)
#
# Exit: 0 on full success; non-zero if any manifest fails.

set -o pipefail

CONTEXT_FLAG=""
MANIFEST_DIR="agents"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --context) CONTEXT_FLAG="--context $2"; shift 2 ;;
    --dir)     MANIFEST_DIR="$2";            shift 2 ;;
    *)         echo "Unknown option: $1"; exit 1 ;;
  esac
done

FAILED=0

for manifest in "$MANIFEST_DIR"/**/*.yaml "$MANIFEST_DIR"/*.yaml; do
  [[ -f "$manifest" ]] || continue
  echo "applying $manifest …"

  # Derive a stable idempotency key from the file content + commit SHA.
  # Re-running the same job within the 24h window returns a cached response
  # (Idempotency-Replayed: true) instead of re-executing.
  key="$(sha256sum "$manifest" | cut -c1-40)-${GITHUB_SHA:-local}"

  # shellcheck disable=SC2086
  vais apply -f "$manifest" $CONTEXT_FLAG --idempotency-key "$key" 2>apply-warnings.log
  exit_code=$?

  # Surface any server-side warnings even on success.
  if [[ -s apply-warnings.log ]]; then
    grep -i warning apply-warnings.log && echo "  ^ review apply warnings above"
  fi

  case $exit_code in
    0) echo "  ✓ applied" ;;
    1) echo "  ✗ client-side error — bad manifest or missing file"; FAILED=1 ;;
    2) echo "  ✗ server rejected the request (non-2xx)";           FAILED=1 ;;
    3) echo "  ✗ policy denied — check Rego / OPA audit log";      FAILED=1 ;;
    4) echo "  ✗ auth failure — rotate the CI token secret";       FAILED=1 ;;
    *) echo "  ✗ unexpected exit code $exit_code";                 FAILED=1 ;;
  esac
done

if [[ $FAILED -ne 0 ]]; then
  echo ""
  echo "One or more manifests failed to apply. See output above."
  exit 1
fi

echo ""
echo "All manifests applied successfully."
