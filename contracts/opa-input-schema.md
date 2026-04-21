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
  "operation": "Create|Invoke|Signal|Query|Cancel|Update|Evict",
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

One of: `Create`, `Invoke`, `Signal`, `Query`, `Cancel`, `Update`,
`Evict`. Mirrors the shipped `PolicyOperation` enum; the 7-verb
universal set routed through `IAgentLifecycleManager`.

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

Optional fields (null or omitted when not set):

- `memory` (object | null) — pluggable memory backing.
- `identity` (object | null) — inbound/outbound auth.
- `autoscaling` (object | null) — replica hints.
- `description` (string | null).
- `labels` (object | null) — registry-scope key/value pairs. **Common
  gating target** — tenant-scope samples use
  `input.agent.labels.tenant`.
- `model` (object | null) — LLM binding
  `{ provider, id, apiKeyRef, baseUrlRef, temperature, topP, maxTokens, responseFormat }`.
- `systemPrompt` (object | null).
- `mcpServers` (array | null).
- `guardrails` (object | null).
- `handoffs` (array | null).
- `budget` (object | null)
  `{ maxTurns, maxToolCalls, maxPromptTokens, maxCompletionTokens, maxDuration }`.
- `contextProviders` (array | null).
- `outputSchema` (arbitrary JSON | null).
- `agentMode` (string) — `"ToolCalling"` default.
- `reasoning` (object | null).
- `observability` (object | null).
- `annotations` (object | null).

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
  `agent`) stay at `schemaVersion: "1"`. Consumers who don't care
  ignore the new fields; consumers who do gate on them via existence
  checks (`input.principal.newField`).
- **Breaking changes** (renamed fields, removed fields, changed
  shapes) bump `schemaVersion` to `"2"`. The adapter dual-ships both
  versions for **one minor version** (v0.15.0 emits v1 by default,
  v2 opt-in; v0.16.0 flips the default; v0.17.0 removes v1). Rego
  policies gate on `input.schemaVersion == "1"` to stay pinned.
- **New `operation` values** don't bump the schema version — the
  `PolicyOperation` enum is closed at 7 verbs shipped in v0.6 and
  extending it is a v0.x breaking change at the Abstractions level
  (not just the OPA schema).
- **Schema doc lives here** (`contracts/opa-input-schema.md`) and is
  the authoritative reference. Integration tests in
  `Vais.Agents.Control.Policy.Opa.Tests/OpaInputBuilderTests.cs` are
  the drift guard — any shape change breaks them.
