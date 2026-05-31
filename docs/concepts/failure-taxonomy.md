# Failure Taxonomy

The failure taxonomy is a shared vocabulary of named *failure concepts* that classifies every mechanical signal the runtime emits. It is the interpretive layer that sits on top of the [observability foundation](../../CHANGELOG.md) (Parts 1–4): the foundation made failures *visible*; the taxonomy makes them *named and attributed*.

## Two axes

| Axis | Concepts | Source |
|---|---|---|
| **Mechanical** | `ToolError`, `McpToolError`, `LlmCallRetried`, `LlmFallbackEngaged`, `LlmCallFailure`, `TurnFailed`, `PluginPartial`, `GuardrailTriggered`, `GraphNodeFailed` | Auto-derived from `RunHealthSignalKind` |
| **Quality** | `JudgeMiss`, `AssertionFail` | Seeded; populated by the eval mechanical axis |

## The catalog

`IFailureOntologyCatalog` is a singleton registered in DI. It exposes:

- `Get(name)` — look up a concept by name
- `FromSignalKind(kind)` — map a `RunHealthSignalKind` to its base concept
- `IsMatchOrDescendant(candidate, filter)` — hierarchy walk for concept-filtered assertions

**Default implementation:** `AutoDerivedFailureOntologyCatalog` — pure code, no I/O, auto-derives from the enum.

**Overlay implementation:** `OverlaidFailureOntologyCatalog` — merges deployment-local `*.failure-ontology.json` files over the auto-derived base. Enabled by setting `VAIS_FAILURE_ONTOLOGY_OVERLAY_PATH` to a directory.

## Concept hierarchy

Sub-concepts extend the base via `ParentName`. Example overlay:

```json
{
  "concepts": [
    {
      "name": "McpToolError/AuthExpired",
      "axis": "Mechanical",
      "defaultLevel": "Warning",
      "description": "MCP tool call failed due to expired auth token.",
      "sourceKinds": [],
      "parentName": "McpToolError",
      "tags": ["auth", "transient"]
    }
  ]
}
```

Save as `*.failure-ontology.json` in the directory pointed to by `VAIS_FAILURE_ONTOLOGY_OVERLAY_PATH`.

## RunHealthSignal stamping

`RunHealthSignalSubscriber` stamps `ConceptName` on every signal at write time:

```
ToolCallCompleted{Succeeded: false} → RunHealthSignalKind.ToolError → "ToolError"
LlmCallRetried                      → RunHealthSignalKind.LlmRetry  → "LlmCallRetried"
```

Signals written before Part 2a was deployed have `ConceptName = null`. `RunHealthAggregator` falls back to `FromSignalKind` at read time for those rows.

## Concept-filtered eval assertions

The four mechanical eval assertions accept an optional `concept` filter. The filter narrows which events trigger the assertion:

```yaml
assertions:
  - kind: no-tool-error
    concept: McpToolError       # only fail if McpToolError or sub-concept
  - kind: max-retries
    max: 2
    concept: LlmCallRetried     # scope to LLM retries specifically
```

A filter matches if the event's concept is exactly `concept` **or is a descendant** of it via the `ParentName` chain. Without a filter the assertion behaves as before (all matching events).

## Related

- `IFailureOntologyCatalog` — `agentic/src/Vais.Agents.Abstractions/IFailureOntologyCatalog.cs`
- `AutoDerivedFailureOntologyCatalog` — `agentic/src/Vais.Agents.Abstractions/AutoDerivedFailureOntologyCatalog.cs`
- `OverlaidFailureOntologyCatalog` — `agentic/src/Vais.Agents.Control.Manifests.Json/FailureOntologyCatalog.cs`
- [Ontology substrate](ontology-substrate.md) — the SEP-1763 substrate this taxonomy plugs into
