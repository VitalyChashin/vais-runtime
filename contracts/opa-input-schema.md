# OPA input schema (v1)

Contract between `Vais.Agents.Control.Policy.Opa` and Rego policies.
Shipped with v0.14.0-preview. Rego policies authored against this
schema consume `input.*` fields via the standard OPA evaluation model.

> **Rendered docs:** [`docs/concepts/opa-policy-engine.md`](../docs/concepts/opa-policy-engine.md)
> (adapter internals, wire contract, FailMode semantics, caching model),
> [`docs/guides/author-a-rego-policy-against-the-vais-input-schema.md`](../docs/guides/author-a-rego-policy-against-the-vais-input-schema.md)
> (four guard patterns).

---

## Envelope

```json
{
  "schemaVersion": "1",
  "operation": "<one of the values in the table below>",
  "principal": { /* nullable — see below */ },
  "agent":     { /* nullable — see below */ }
}
```

### `schemaVersion` (string, required)

Stable discriminator for the input shape. Locked at `"1"` for
v0.14.0-preview. Rego authors gate on this to future-proof against
breaking changes:

```rego
allow := { ... } if {
    input.schemaVersion == "1"
    # ... rest of rule ...
}
```

### `operation` (string, required)

Stringified `PolicyOperation` enum value. The enum has been extended past
the original 7 agent verbs as new resource kinds joined the control
plane. The current shipping set, grouped by the resource the operation
acts on:

| Resource | Operations |
|---|---|
| Agent (v0.6) | `Create`, `Invoke`, `Signal`, `Query`, `Cancel`, `Update`, `Evict` |
| Graph (v0.19) | `GraphCreate`, `GraphInvoke`, `GraphResume`, `GraphQuery`, `GraphCancel`, `GraphUpdate`, `GraphEvict` |
| LLM gateway config (v0.20) | `LlmGatewayConfigCreate`, `LlmGatewayConfigUpdate`, `LlmGatewayConfigQuery`, `LlmGatewayConfigEvict` |
| MCP gateway config (v0.20) | `McpGatewayConfigCreate`, `McpGatewayConfigUpdate`, `McpGatewayConfigQuery`, `McpGatewayConfigEvict` |
| MCP server (v0.20) | `McpServerCreate`, `McpServerUpdate`, `McpServerQuery`, `McpServerEvict` |
| Container plugin (v0.24) | `ContainerPluginCreate`, `ContainerPluginUpdate`, `ContainerPluginQuery`, `ContainerPluginEvict` |
| Eval suite (E1) | `EvalSuiteUpsert`, `EvalSuiteQuery`, `EvalSuiteEvict` |

The shape of `input.agent` depends on the resource: for agent operations
it carries an `AgentManifest`; for graph operations a graph manifest;
for gateway-config / MCP-server / container-plugin / eval-suite
operations the corresponding manifest type. Rego policies that gate by
operation should branch on the operation name first, then dereference
the manifest-specific fields. Policies that don't care about a resource
kind can rely on the default-deny fall-through (no rule matched).

### `principal` (object | null)

The authenticated caller mapped via `IPrincipalMapper`. **Null when
the caller is anonymous** — policies must guard with `input.principal
!= null` before accessing sub-fields.

```json
{
  "id": "sub-claim-string",
  "tenantId": "optional-tenant-or-null",
  "scopes": ["optional", "array", "or", "omitted"]
}
```

- `id` (string, required when principal non-null) — sub / unique-id
  claim. For K8s SA tokens this is
  `"system:serviceaccount:<ns>:<sa>"`.
- `tenantId` (string | null) — caller's tenant scope when the principal
  mapper populates it; `null` for single-tenant deployments or
  unmapped claims.
- `scopes` (array of strings, optional) — OAuth / ABAC scope list when
  the auth layer supplies one. Field omitted when empty.

### `agent` (object | null)

The full `AgentManifest` serialised via STJ `JsonSerializerDefaults.Web`
(camelCase property names). **Null on `Query` against an unknown
agent** — policies must guard with `input.agent != null` for rules
that inspect manifest fields.

Minimum required fields (mirror the manifest's positional ctor):

```json
{
  "id": "agent-id",
  "version": "v1",
  "handler": { "typeName": "...", "assemblyName": null },
  "protocols": [{ "kind": "Http", "endpoint": null }],
  "tools": [{ "name": "weather", "source": null }]
}
```

Optional fields. Note: STJ is configured with
`JsonIgnoreCondition.WhenWritingNull`, so unset fields are **omitted**
from the wire payload, never emitted as `null`. Rego policies should
prefer `not input.agent.memory` over `input.agent.memory == null`.

- `memory` (object) — pluggable memory backing.
- `identity` (object) — inbound/outbound auth.
- `autoscaling` (object) — replica hints.
- `description` (string).
- `labels` (object) — registry-scope key/value pairs. **Common gating
  target** — tenant-scope samples use `input.agent.labels.tenant`.
- `model` (object) — LLM binding
  `{ provider, id, apiKeyRef, baseUrlRef, temperature, topP, maxTokens, responseFormat }`.
- `systemPrompt` (object).
- `mcpServers` (array).
- `a2aRemoteAgents` (array) — `a2a:<name>` tool-source targets (v0.17).
- `localAgents` (array) — `agent:<name>` tool-source targets (v0.18; agent-as-tool).
- `guardrails` (object).
- `handoffs` (array).
- `budget` (object)
  `{ maxTurns, maxToolCalls, maxPromptTokens, maxCompletionTokens, maxDuration }`.
- `contextProviders` (array).
- `outputSchema` (arbitrary JSON).
- `agentMode` (string) — `"ToolCalling"` default.
- `reasoning` (object).
- `observability` (object).
- `annotations` (object) — operator-visible metadata; not indexed.
- `llmGatewayRef` (string) — id of a deployed `LlmGatewayConfigManifest` (v0.6+).
- `mcpGatewayRef` (string) — id of a deployed `McpGatewayConfigManifest` (v0.6+).

---

## Response shape

The adapter accepts two shapes for the rule result:

### Boolean

```rego
allow := true   # or false
```

Maps to `PolicyDecision.Allow` / `PolicyDecision.Deny("Policy denied")`.

### Object (recommended for denials with reasons)

```rego
allow := {"allowed": true}
allow := {"allowed": false, "reason": "cross-tenant access denied"}
```

- `allowed` (bool, required).
- `reason` (string, optional) — carried through to
  `PolicyDecision.Reason` on denial.

---

## Rego guard patterns

### Null-safe principal access

```rego
allow := {"allowed": false, "reason": "unauthenticated"} if {
    input.principal == null
}

allow := {"allowed": true} if {
    input.principal != null
    input.principal.tenantId == "expected-tenant"
}
```

### Null-safe agent access (for Query on unknown)

```rego
allow := {"allowed": true} if {
    input.operation == "Query"
    input.agent == null   # querying non-existent agent — allow read
}
```

### Operation-specific gating

```rego
default allow := {"allowed": true}

allow := {"allowed": false, "reason": "..."} if {
    input.operation == "Create"
    # ... gate the Create path ...
}
```

### Multi-rule composition

Use `import` or split into multiple packages; the adapter queries a
single configured `DataPath`, so build an outer rule that OR's the
sub-rule results.

---

## Schema evolution protocol

- **Additive changes** (new optional fields on `principal` or
  `agent`, new operation values) stay at `schemaVersion: "1"`.
  Consumers who don't care ignore the new fields and the default-deny
  fall-through handles unknown operation names; consumers who do
  gate on them via existence checks (`input.principal.newField`).
- **Breaking changes** (renamed fields, removed fields, changed
  shapes, or a different envelope) bump `schemaVersion` to `"2"`.
  A dual-ship transition window has not been implemented yet — the
  current `OpaInputBuilder.SchemaVersion` constant is a single value
  and rolls forward atomically when bumped. Rego policies should
  always gate on `input.schemaVersion == "1"` to fail closed when
  the bump lands.
- **`PolicyOperation` enum extensions** are additive (per the table
  above the enum already grew from 7 to 33 values across v0.6 →
  E1 without a schema-version bump). New resource kinds are expected
  to keep extending it.
- **Schema doc lives here** (`contracts/opa-input-schema.md`) and is
  the authoritative reference. Integration tests in
  `Vais.Agents.Control.Policy.Opa.Tests/OpaInputBuilderTests.cs` are
  the drift guard — any shape change breaks them.
