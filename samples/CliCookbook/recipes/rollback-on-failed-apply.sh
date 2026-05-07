#!/usr/bin/env bash
# rollback-on-failed-apply.sh — apply a new manifest; on failure delete it and
# re-apply the previous version.
#
# Usage: ./rollback-on-failed-apply.sh --new <new.yaml> --prev <prev.yaml> [--context <ctx>]
#   --new      path to the new-version manifest
#   --prev     path to the previous-version manifest (the rollback target)
#   --context  vais context (default: currentContext)
#
# Exit: 0 on success (new version applied) or after successful rollback;
#       1 if the rollback itself also fails.

set -o pipefail

NEW_MANIFEST=""
PREV_MANIFEST=""
CONTEXT_FLAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --new)     NEW_MANIFEST="$2";            shift 2 ;;
    --prev)    PREV_MANIFEST="$2";           shift 2 ;;
    --context) CONTEXT_FLAG="--context $2";  shift 2 ;;
    *)         echo "Unknown option: $1"; exit 1 ;;
  esac
done

[[ -n "$NEW_MANIFEST"  ]] || { echo "--new is required";  exit 1; }
[[ -n "$PREV_MANIFEST" ]] || { echo "--prev is required"; exit 1; }
[[ -f "$NEW_MANIFEST"  ]] || { echo "File not found: $NEW_MANIFEST";  exit 1; }
[[ -f "$PREV_MANIFEST" ]] || { echo "File not found: $PREV_MANIFEST"; exit 1; }

# Extract agent id from the new manifest (assumes top-level `id:` field).
AGENT_ID=$(grep -m1 '^\s*id:' "$NEW_MANIFEST" | awk '{print $2}' | tr -d '"')
[[ -n "$AGENT_ID" ]] || { echo "Cannot extract agent id from $NEW_MANIFEST"; exit 1; }

echo "Applying new version: $NEW_MANIFEST  (agent: $AGENT_ID)"
# shellcheck disable=SC2086
vais apply -f "$NEW_MANIFEST" $CONTEXT_FLAG
exit_code=$?

if [[ $exit_code -eq 0 ]]; then
  echo "✓ New version applied successfully."
  exit 0
fi

echo "✗ Apply failed (exit $exit_code). Starting rollback …"

# Delete the possibly-partial new registration before re-applying previous.
echo "  Deleting $AGENT_ID …"
# shellcheck disable=SC2086
vais delete "$AGENT_ID" --force $CONTEXT_FLAG
delete_exit=$?

if [[ $delete_exit -ne 0 ]]; then
  echo "  Warning: delete returned $delete_exit. Proceeding with rollback apply anyway."
fi

echo "  Re-applying previous version: $PREV_MANIFEST"
# shellcheck disable=SC2086
vais apply -f "$PREV_MANIFEST" $CONTEXT_FLAG
rollback_exit=$?

if [[ $rollback_exit -eq 0 ]]; then
  echo "✓ Rolled back to previous version."
  # Exit 1 to signal the pipeline that an apply → rollback occurred.
  exit 1
fi

echo "✗ Rollback apply also failed (exit $rollback_exit). Manual intervention required."
exit 1
