# Guide: tail live runs with `vais logs`

`vais logs <agent-id>` attaches to the v0.12 SSE streaming-invoke route and renders every `AgentEvent` to the terminal as it fires. Pair with `--only` to filter event kinds, `--since` for time-bounded tails, Ctrl-C for graceful shutdown.

Shipped in v0.15 as part of the CLI. Wraps the v0.12 `POST /v1/agents/{id}/invoke/stream` endpoint in attach mode — no new user message is sent, just live events from the in-flight run on the specified session.

## Basic tail

Open a terminal, start tailing:

```bash
vais logs weather --session chat-7821
# ▶  10:15:00.123  turn.started       user: "What's the weather in Tokyo?"
# …  10:15:00.987  delta              "It's"
# …  10:15:00.998  delta              " 18°C"
# …  10:15:01.012  delta              " and sunny"
# ▶  10:15:01.100  tool.started       get_weather (call call-1)
# ✓  10:15:01.634  tool.completed     get_weather (success, 534ms)
# …  10:15:01.700  delta              " in Tokyo."
# ◀  10:15:01.750  turn.completed     42 prompt → 12 completion tokens, 1.6s
```

Leave it running — every subsequent invocation on session `chat-7821` streams into the same terminal. Ctrl-C shuts down cleanly with exit `130`.

## Filter by event kind

`--only <kind1,kind2,…>` accepts a comma-separated list of SSE event names (the kebab-case wire names):

```bash
# Only show tool dispatches + terminal events
vais logs weather --only tool.started,tool.completed,turn.completed,turn.failed

# Only show text deltas (useful for visual debugging)
vais logs weather --only delta

# Just the guardrail decisions
vais logs weather --only guardrail.triggered,interrupt.raised
```

The ten wire-event names available (see the [events reference](../reference/events.md) for the authoritative list):

| Event | Use for |
|---|---|
| `turn.started` | Run-envelope open. |
| `turn.completed` | Run-envelope close — usage + duration. |
| `turn.failed` | Exception in the run. |
| `tool.started` | Pre-dispatch of a tool call. |
| `tool.completed` | Post-dispatch, success or failure. |
| `tool.replayed` | Cache hit — tool outcome served from the journal. |
| `guardrail.triggered` | Any three-layer guardrail returning Deny/Interrupt. |
| `interrupt.raised` | HITL interrupt — run paused. |
| `handoff.requested` | Multi-agent handoff. |
| `delta` | Streamed text chunk (`CompletionDelta`). |

Unrecognised names are silently ignored — the CLI doesn't error on typos, it just filters nothing out. Double-check spelling if `--only foo.bar` shows every event.

## Time-bounded tail with `--since`

`--since <iso-8601-timestamp>` drops events whose `At` field predates the cutoff. Applied **client-side** — the server still streams everything; the CLI filters on the way to stdout:

```bash
# Events from the last minute
vais logs weather --since "$(date -u -d '1 minute ago' -Iseconds)"

# Events since a specific wall-clock moment
vais logs weather --since "2026-04-20T10:15:00Z"
```

`--since` handles most common time formats that `DateTimeOffset.TryParse` understands. Timezone suffix is honoured — mix and match local times + explicit offsets cleanly.

Because the filter is client-side, it does **not** replay historical events the server doesn't retain. The SSE stream is "from now on" — events that fired before you ran the command aren't in the stream. `--since` is useful for skipping events from the initial backlog after a long `logs` pause, not for historical queries.

## Ctrl-C graceful shutdown

The CLI wires `Console.CancelKeyPress` into a `CancellationTokenSource` linked to the SSE parser. First Ctrl-C:

- Sets `e.Cancel = true` — prevents the shell from immediately killing the process.
- Calls `cts.Cancel()` — the `await foreach` unblocks; the SSE parser disposes the underlying `HttpResponseMessage`; any in-flight event is drained.
- Returns exit code `130` (`ExitSigInt`).

Second Ctrl-C — if the first one hangs (e.g. the server never responded to the cancellation) — triggers the runtime's default behaviour, which kills the process. In practice the graceful path completes in under 100 ms.

## Useful pipelines

### Tool-failure alerting

```bash
vais logs weather --only tool.completed | \
  grep -E "succeeded.*false|error:" | \
  while read line; do
    curl -X POST "$SLACK_WEBHOOK" -d "{\"text\":\"Tool failure: $line\"}"
  done
```

### Delta rate-measuring

```bash
vais logs weather --only delta | \
  awk '{ count++; if (count % 100 == 0) print count, $1 }'
```

### Usage sampling

```bash
vais logs weather --only turn.completed --since "$(date -u -Iseconds)" -o json | \
  jq -r '[.at, .promptTokens, .completionTokens] | @csv' > token-usage.csv
```

(Output-format flag is not yet implemented on `logs` — the rendered output is fixed emoji-tagged lines. For machine-readable streams, use `AgentControlPlaneClient.InvokeStreamEventsAsync` from a .NET host instead.)

## Combining with `invoke --stream`

`vais invoke <id> --text ... --stream` streams the run from your own terminal — useful when you want to both initiate + observe in one shell:

```bash
vais invoke weather --text "What's the weather in Tokyo?" --stream
```

For the "someone else initiated the run, I want to watch" case, use `logs` with `--session`:

```bash
# In another shell — or CI job — fire the invoke
vais invoke weather --session chat-7821 --text "…" &

# In your terminal — attach + watch
vais logs weather --session chat-7821
```

The two are symmetric views on the same SSE stream.

## Error paths

- **Agent has no active run** — `logs` blocks indefinitely waiting for the next event. Ctrl-C to exit.
- **Agent not found** — `404 urn:vais-agents:agent-not-found` → exit `2` (ApiError).
- **Agent is registered but doesn't implement `IStreamingAiAgent`** — `501 urn:vais-agents:streaming-not-supported` → exit `2`. Use unary `invoke` without `--stream` to run it.
- **Auth expired mid-stream** — SSE connection drops; the CLI surfaces a clean disconnect + exit `4`.

## Scripted tailing from CI

Tailing runs is less common in CI than interactive use, but it's useful for smoke tests where you want to assert "the agent completed without errors":

```bash
#!/usr/bin/env bash
set -eo pipefail

# Start the tail in the background, capture output to a log file.
vais logs weather --session smoke-test-$RUN_ID > run.log &
LOGS_PID=$!

# Fire the invoke.
vais invoke weather --session smoke-test-$RUN_ID --text "healthcheck"

# Give the tail a beat to drain the final event.
sleep 2

# Kill the tail (graceful — exits 130).
kill -INT $LOGS_PID || true
wait $LOGS_PID || true

# Assert no turn.failed in the log.
if grep -q "turn.failed" run.log; then
  echo "run failed — log contents:"
  cat run.log
  exit 1
fi
echo "smoke test passed"
```

## See also

- [Install the CLI](../devops/install-the-cli.md) — bootstrap + first-run walkthrough.
- [CLI concept](../concepts/cli.md) — full subcommand map.
- [CLI subcommands reference](../reference/cli-subcommands.md) — per-command flag table for `logs`.
- [Stream invocations over HTTP](stream-invocations-over-http.md) — the underlying v0.12 SSE route.
- [Events reference](../reference/events.md) — `AgentEvent` hierarchy + wire-event-name mapping.
