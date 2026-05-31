# Diagnose over MCP

A coding agent driving the runtime through `/design-mcp` can close the **diagnose → author-fix → verify** loop without leaving MCP. This guide walks the loop end-to-end using the Part 2c diagnostic verbs.

> **Status:** The request/response examples below are **illustrative** — they show the exact JSON shapes the verbs return based on the production handlers' projection code, validated by unit tests. A live end-to-end capture of the full diagnose→author-fix→verify loop against `dev.ps1 start` has not yet been recorded; that is tracked in `plans/gaps/part2c-live-loop-and-pg-integration-2026-05-31.md`.

## The verbs

Appended to the `/design-mcp` `DesignTools` baseline (no auth gate — read-only):

| Tool | Purpose |
|---|---|
| `vais.diagnose(runId)` | Per-run rollup. Worst level + every attributed failure signal across the run tree. |
| `vais.runHealth([level], [since], [limit])` | Recent runs at or above a severity threshold. |
| `vais.failures([concept], [agentName], [since], [limit])` | Cross-run search by concept / agent. |

Every signal carries:
- **`conceptName`** — Part 2a taxonomy concept (e.g. `McpToolError`, `ToolError/RateLimited`).
- **`attributionPath`** — Part 2b deployment path (e.g. `confluence-agent/confluence-mcp/confluence_search`).

Plus two resources:
- `vais-ontology://Failure[/{conceptName}]` — full taxonomy + per-concept details.
- `vais-diagnostics://run/{runId}` — byte-identical body to `vais.diagnose(runId)`.

## The loop

### 1. Discover

The dashboard or eval shows a recent run that completed but didn't behave right.

```jsonc
// tools/call vais.runHealth
{ "level": "degraded", "since": "2026-05-31T00:00:00Z", "limit": 10 }
// →
{
  "count": 3,
  "items": [
    { "runId": "run-abc", "level": "degraded", "signalCount": 2, "latestAt": "2026-05-31T14:22Z" },
    ...
  ]
}
```

### 2. Attribute

Drill into the most recent degraded run:

```jsonc
// tools/call vais.diagnose
{ "runId": "run-abc" }
// →
{
  "runId": "run-abc",
  "level": "degraded",
  "signals": [
    {
      "source": "confluence_search",
      "kind": "mcpError",
      "level": "warning",
      "errorType": "Unauthorized",
      "conceptName": "McpToolError/AuthExpired",
      "attributionPath": "confluence-agent/confluence-mcp/confluence_search",
      "at": "2026-05-31T14:22Z"
    }
  ]
}
```

The agent now knows **what** failed (`McpToolError/AuthExpired`) and **where** (`confluence-agent` calling the `confluence_search` tool on the `confluence-mcp` server).

### 3. Pattern-check

Is this a one-off or a pattern?

```jsonc
// tools/call vais.failures
{ "concept": "McpToolError", "since": "2026-05-30T00:00:00Z" }
// →
{
  "count": 17,
  "items": [
    { "runId": "run-abc", "conceptName": "McpToolError/AuthExpired", "attributionPath": "confluence-agent/confluence-mcp/confluence_search", ... },
    ...
  ]
}
```

Querying the parent concept (`McpToolError`) matches descendants (`McpToolError/AuthExpired`) via the taxonomy parent-walk + slash-path fallback. Both bus-sourced concepts (in-process tool errors) AND MCP gateway errors are returned in the same list, sorted newest-first.

### 4. Author the fix

The agent has enough context now to propose a change. For an auth-expired pattern, that might mean rotating the `apiKeyRef` on the failing MCP server:

```jsonc
// tools/call vais.validate
{ "manifest": "<full McpServer manifest with new apiKeyRef>" }
// → { "ok": true, "errors": [], "suggestions": [] }

// tools/call vais.apply
{ "manifest": "<same manifest>" }
// → { "ok": true, "kind": "McpServer", "name": "confluence-mcp", "version": "1.1", "action": "updated" }
```

### 5. Verify

Re-run the affected eval suite:

```jsonc
// tools/call vais.eval
{ "suiteRef": "confluence-search-suite" }
// → { "ok": true, "runId": "eval-xyz" }

// tools/call vais.eval.status
{ "runId": "eval-xyz" }
// → { "ok": true, "status": "completed", "totalCases": 8, "passedCases": 8, "failedCases": 0 }
```

Then re-diagnose to confirm `McpToolError/AuthExpired` is gone:

```jsonc
// tools/call vais.failures
{ "concept": "McpToolError/AuthExpired", "since": "2026-05-31T14:30Z" }
// → { "count": 0, "items": [] }
```

Loop closed.

## CLI parity

Every MCP verb has an identical CLI form that hits the same backing services:

```bash
# Per-run
vais diagnose run <graphId> <runId>

# Cross-run
vais diagnose runs [--level=failed|degraded] [--since=…] [--limit=N]

# Cross-run failures
vais diagnose failures [--concept=…] [--agent=…] [--since=…] [--limit=N]
```

So a human operator and a coding agent see the same truth.

## v1 scope

`vais.failures` and `vais.runHealth` query the **persisted** failure stores:

- ✅ Bus-sourced concepts (`ToolError`, `TurnFailed`, `PluginPartial`, `LlmCallRetried`, `LlmFallbackEngaged`, `GuardrailTriggered`) — via `IRunHealthStore`.
- ✅ `McpToolError` and its sub-concepts — via `IMcpGatewayEventStore`.
- ⏳ `NodeFailed` / `LlmCallFailure` (plugin path) / background failures — only via `vais.diagnose` per-run; not yet indexed cross-run.

This v1 scope is documented in the tool descriptions and in `/v1/run-health` / `/v1/run-health/signals` REST responses.

## Related

- `docs/concepts/failure-taxonomy.md` — Part 2a + 2b: the vocabulary and per-deployment attribution that the diagnostic verbs surface.
- `plans/completed/observability-foundation-roadmap-2026-05-30.md` — the foundation rollup the diagnostic verbs interpret.
