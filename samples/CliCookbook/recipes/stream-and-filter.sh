#!/usr/bin/env bash
# stream-and-filter.sh — tail turn.completed + tool.completed events;
# alert on tool failures; sample usage stats from the event stream.
#
# Usage: ./stream-and-filter.sh <agent-id> [--session <id>] [--context <ctx>]
#   <agent-id>  required — agent to tail
#   --session   correlate with a specific session (default: latest)
#   --context   vais context (default: currentContext)
#
# Press Ctrl-C to stop tailing (exit 130).

set -o pipefail

AGENT_ID=""
SESSION_FLAG=""
CONTEXT_FLAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --session) SESSION_FLAG="--session $2"; shift 2 ;;
    --context) CONTEXT_FLAG="--context $2"; shift 2 ;;
    -*)        echo "Unknown option: $1"; exit 1 ;;
    *)         AGENT_ID="$1"; shift ;;
  esac
done

[[ -n "$AGENT_ID" ]] || { echo "Usage: $0 <agent-id> [--session <id>] [--context <ctx>]"; exit 1; }

LOG_FILE="run-$(date +%Y%m%dT%H%M%S).log"

echo "Tailing $AGENT_ID — writing to $LOG_FILE"
echo "Filter: turn.completed, tool.completed  |  Ctrl-C to stop"
echo ""

# Pipe through tee so events land on stdout AND in the log file.
# shellcheck disable=SC2086
vais logs "$AGENT_ID" \
  --only turn.completed,tool.completed,turn.failed \
  $SESSION_FLAG \
  $CONTEXT_FLAG \
  | tee "$LOG_FILE"

tail_exit=$?

# After the stream ends (or Ctrl-C), emit a quick summary from the log.
echo ""
echo "=== summary ==="

turn_count=$(grep -c 'turn\.completed' "$LOG_FILE" 2>/dev/null || echo 0)
fail_count=$(grep -c 'turn\.failed'    "$LOG_FILE" 2>/dev/null || echo 0)
tool_count=$(grep -c 'tool\.completed' "$LOG_FILE" 2>/dev/null || echo 0)

echo "  turns completed : $turn_count"
echo "  turns failed    : $fail_count"
echo "  tool dispatches : $tool_count"

# Alert if any tool failures were logged.
if grep -q 'status.*fail\|error\|denied' "$LOG_FILE" 2>/dev/null; then
  echo ""
  echo "⚠  Tool failures detected — review $LOG_FILE"
fi

# Preserve Ctrl-C exit code so the caller can detect a deliberate stop.
exit $tail_exit
