# Failure Prior Induction

Failure priors are inducted from the cross-run failure signal corpus by the `FailurePatternInducer`. When a `(concept, attribution-path)` group accumulates ≥ 5 failure events, the inducer emits a `FailurePrior` recipe proposal. An operator approves it; the runtime writes `induced.failure-ontology.json` into the configured overlay directory. On the next restart (or hot-reload if `IOntologyCatalogReloader` is wired for the failure catalog), the prior becomes visible in the `vais-ontology://Failure/{concept}` MCP resource under the `priors` array — so a coding agent authoring fixes for that agent/tool path can read the historical failure pattern before touching the manifest.

## Prerequisites

| Requirement | Environment variable |
|---|---|
| Postgres run-health store | `VAIS_RUN_HEALTH_STORE_CONNECTION` |
| Postgres MCP gateway event store | `VAIS_MCP_GATEWAY_EVENT_STORE_CONNECTION` |
| Behavioral recipe overlay dir (activates decorator) | `VAIS_ONTOLOGY_OVERLAY_PATH` |
| Failure recipe overlay dir | `VAIS_FAILURE_ONTOLOGY_OVERLAY_PATH` |

Both `VAIS_ONTOLOGY_OVERLAY_PATH` **and** `VAIS_FAILURE_ONTOLOGY_OVERLAY_PATH` must be non-empty. `MaybeWrapWithOverlayPublishing` returns the unwrapped store when the behavioral overlay path is absent — this silently disables the `FailurePrior` write-back even if the failure path is set.

The local-dev stack (`local-dev/dev.ps1 start`) configures all four automatically since 2026-05-31.

## End-to-end verified

Verified on 2026-05-31 against the local-dev stack (`vais-runtime:local`, Postgres `pgvector-db-1`, runtime healthy at `http://localhost:8080`).

### Step 1 — Seed failures

Inject 6 `McpToolError/AuthExpired` events directly into `vais_mcp_gateway_events` so the group
`(concept=McpToolError, attributionPath=search)` has `COUNT(*) = 6 ≥ MinSupport (5)`.

```sql
INSERT INTO vais_mcp_gateway_events
    (event_id, gateway_id, tool_name, event_kind, duration_ms, cache_hit,
     blocked_reason, error_type, at, correlation_id, run_id)
SELECT
    'fp11-seed-' || i::text,
    'research-mcp-gateway',
    'search',
    'ToolError',
    NULL,
    false,
    NULL,
    'AuthExpired',
    NOW() - (i * interval '5 minutes'),
    'fp11-correlation-' || i::text,
    'fp11-run-' || i::text
FROM generate_series(1, 6) AS i
ON CONFLICT (event_id) DO NOTHING;
-- INSERT 0 6
```

Confirm one group above threshold:

```sql
SELECT tool_name, error_type, COUNT(*) AS failure_count
FROM vais_mcp_gateway_events
WHERE error_type IS NOT NULL AND event_id LIKE 'fp11-seed-%'
GROUP BY tool_name, error_type
HAVING COUNT(*) >= 5;

 tool_name | error_type  | failure_count
-----------+-------------+---------------
 search    | AuthExpired |             6
(1 row)
```

### Step 2 — Propose

```
$ vais recipes propose --source failures --since 1h --endpoint http://localhost:8080

┌──────────────┬──────────────┬──────┬─────────┬────────────┬──────────────────┐
│ ID           │ CONCEPT      │ RISK │ SUPPORT │ CONFIDENCE │ BODY             │
├──────────────┼──────────────┼──────┼─────────┼────────────┼──────────────────┤
│ 019e7f5c8e0c │ McpToolError │ low  │ 6       │ 0 %        │ {"AgentName":"se │
│              │              │      │         │            │ arch","ConceptNa │
│              │              │      │         │            │ me":"McpToolErro │
│              │              │      │         │            │ r","Attribut…    │
└──────────────┴──────────────┴──────┴─────────┴────────────┴──────────────────┘
1 proposal(s) emitted. Review with 'vais recipes list --status Pending'
```

Confidence is always `0 %` for failure priors — the inducer uses support count as the operative signal, not a true fail-rate.

### Step 3 — List proposals

```
$ vais recipes list --kind FailurePrior --output json --endpoint http://localhost:8080

[
  {
    "proposalId": "019e7f5c8e0c7f32b04892e381bfa4c4",
    "kind": 3,
    "concept": "McpToolError",
    "body": "{\"AgentName\":\"search\",\"ConceptName\":\"McpToolError\",\"AttributionPath\":\"search\",\"FailureCount\":6,\"FirstSeen\":\"2026-05-31T18:16:58.705018+00:00\",\"LastSeen\":\"2026-05-31T18:41:58.705018+00:00\"}",
    "support": 6,
    "confidence": 0,
    "sourceTraceIds": [],
    "riskLevel": 0,
    "status": 0,
    "createdAt": "2026-05-31T18:47:12.652186+00:00",
    "reviewedAt": null,
    "reviewerId": null,
    "name": null
  }
]
```

### Step 4 — Approve

```
$ vais recipes approve 019e7f5c8e0c7f32b04892e381bfa4c4 --endpoint http://localhost:8080

Proposal 019e7f5c8e0c7f32b04892e381bfa4c4 approved (by wchf).
  Status: approved
```

### Step 5 — Confirm overlay file

`VAIS_FAILURE_ONTOLOGY_OVERLAY_PATH` is `local-dev/overlays/failure/` (mounted at `/var/lib/vais/overlays/failure` inside the container). The approval writes:

```json
{
  "Attributions": {
    "search": {
      "FailurePriors": [
        {
          "AgentName": "search",
          "ConceptName": "McpToolError",
          "AttributionPath": "search",
          "FailureCount": 6,
          "FirstSeen": "2026-05-31T18:16:58.705018+00:00",
          "LastSeen": "2026-05-31T18:41:58.705018+00:00"
        }
      ]
    }
  }
}
```

File path: `local-dev/overlays/failure/induced.failure-ontology.json` (on the host; symlinked into the container via Docker bind-mount).

### Step 6 — Confirm diagnose visibility (MCP)

After restarting the runtime so `FailureOntologyOverlayLoader.LoadAllFromDirectory` picks up the updated file, read the `McpToolError` concept via the `/design-mcp` MCP server:

```
POST http://localhost:8080/design-mcp
Accept: application/json, text/event-stream
Content-Type: application/json

{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"vais-ontology://Failure/McpToolError"}}
```

Response (SSE frame):

```
event: message
data: {
  "result": {
    "contents": [{
      "uri": "vais-ontology://Failure/McpToolError",
      "mimeType": "application/json",
      "text": "{
        \"name\": \"McpToolError\",
        \"axis\": \"Mechanical\",
        \"defaultLevel\": \"Warning\",
        \"description\": \"An MCP gateway tool call failed.\",
        \"parentName\": null,
        \"sourceKinds\": [\"McpError\"],
        \"children\": [],
        \"priors\": [{
          \"attributionPath\": \"search\",
          \"agentName\": \"search\",
          \"toolName\": null,
          \"failureCount\": 6,
          \"firstSeen\": \"2026-05-31T18:16:58.705018+00:00\",
          \"lastSeen\": \"2026-05-31T18:41:58.705018+00:00\"
        }]
      }"
    }]
  },
  "id": 1,
  "jsonrpc": "2.0"
}
```

The inducted prior appears under `priors`. A coding agent using `resources/read vais-ontology://Failure/McpToolError` will see the historical failure pattern (6 failures on `search` tool, attribution path `search`) before authoring a manifest fix.

**Note on `toolName: null`:** The `FailurePatternInducer` derives `AgentName` from the first result's `Source` (which equals `tool_name` for MCP events); `ToolName` is not yet extracted from the group. This is a data-quality gap and does not affect prior visibility or approval flow.

### Step 6 (CLI convenience) — `vais diagnose failures`

```
$ vais diagnose failures --concept McpToolError --since 2026-05-31T18:00:00Z --endpoint http://localhost:8080

┌───────────┬───────────┬───────────┬────────┬─────────┬───────────┬───────────┐
│ RUN       │ CONCEPT   │ ATTRIBUT… │ SOURCE │ LEVEL   │ ERROR_TY… │ AT        │
├───────────┼───────────┼───────────┼────────┼─────────┼───────────┼───────────┤
│ fp11-run- │ McpToolEr │ search    │ search │ warning │ AuthExpir │ 2026-05-3 │
│ 1         │ ror       │           │        │         │ ed        │ 1 ...     │
│ fp11-run- │ McpToolEr │ search    │ search │ warning │ AuthExpir │ 2026-05-3 │
│ 2         │ ror       │           │        │         │ ed        │ 1 ...     │
│ fp11-run- │ McpToolEr │ search    │ search │ warning │ AuthExpir │ 2026-05-3 │
│ 3         │ ror       │           │        │         │ ed        │ 1 ...     │
│ fp11-run- │ McpToolEr │ search    │ search │ warning │ AuthExpir │ 2026-05-3 │
│ 4         │ ror       │           │        │         │ ed        │ 1 ...     │
│ fp11-run- │ McpToolEr │ search    │ search │ warning │ AuthExpir │ 2026-05-3 │
│ 5         │ ror       │           │        │         │ ed        │ 1 ...     │
│ fp11-run- │ McpToolEr │ search    │ search │ warning │ AuthExpir │ 2026-05-3 │
│ 6         │ ror       │           │        │         │ ed        │ 1 ...     │
└───────────┴───────────┴───────────┴────────┴────────┴───────────┴───────────┘
```

This shows the six underlying failure signals that drove the induction. Use the MCP `resources/read` path above to see the inducted prior itself.

## Catalog hot-reload

`IFailureOntologyCatalog` is registered at startup from files on disk (`FailureOntologyOverlayLoader.LoadAllFromDirectory`). The overlay file written by `vais recipes approve` is not hot-reloaded by the current `IOntologyCatalogReloader` implementation (which targets the behavioural ontology). After each new approval:

```powershell
cd local-dev
.\dev.ps1 stop
.\dev.ps1 start
```

A full hot-reload path for the failure catalog is tracked at `plans/gaps/failure-ontology-catalog-hot-reload-gap-2026-05-31.md`.
