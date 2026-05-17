# Reference: Agent CRD

The `Agent` CustomResourceDefinition that the v0.13 Kubernetes operator reconciles against the v0.6 HTTP control plane. Installed via the Helm chart at `deploy/helm/vais-agents-operator/` (or manually via `kubectl apply -f deploy/crds/vais.io_agents.yaml`).

## CRD metadata

| Field | Value |
|---|---|
| `apiGroup` | `vais.io` |
| `versions[0].name` | `v1alpha1` |
| `kind` | `Agent` |
| `names.plural` | `agents` |
| `names.singular` | `agent` |
| `names.shortNames` | `vagent`, `vagents` |
| `scope` | `Namespaced` |
| `finalizer` | `vais.io/agent-deactivate` (auto-added by operator) |

`v1alpha1` is the API version — expect breaking schema changes before `v1` graduation. Renames to required fields are breaking; additive optional fields land without a version bump.

## Printer columns

`kubectl get vagent` + `kubectl get vagents` display:

| Column | Type | JSON path | Example |
|---|---|---|---|
| `Agent Id` | string | `.spec.agentId` | `weather` |
| `Version` | string | `.spec.version` | `1.0` |
| `Phase` | string | `.status.phase` | `Active` |
| `Ready` | string | `.status.conditions[?(@.type=="Ready")].status` | `True` |
| `Age` | date | `.metadata.creationTimestamp` | `5m` |

## `spec` schema

Every field marked **required** must appear on the CR; missing any causes `ManifestValid = False` with `Reason = ManifestInvalid`. Optional fields pass through to the runtime's `AgentManifest` if set, or are omitted from the outbound call if null.

### Required fields

| Field | Type | Purpose |
|---|---|---|
| `agentId` | string | Stable identifier, unique within the registry namespace. Cannot change after creation. |
| `version` | string | Immutable version tag. Edits bump `metadata.generation`; operator flips to `Updating`. |
| `handler.typeName` | string | Fully-qualified .NET type name (for in-process) or OCI image reference (for containerised handlers). |
| `handler.assemblyName` | string? | Optional assembly name. Omit when `typeName` is already fully qualified. |
| `protocols` | array | Protocol bindings — `{kind: "Http"/"A2A"/"Mcp"/custom, endpoint?: "…"}`. Empty array allowed. |
| `tools` | array | Tool references — `{name, source?}`. Empty array allowed. |

### Optional fields

| Field | Type | Purpose |
|---|---|---|
| `description` | string? | Human-readable description. Passed through to registries + dashboards. |
| `labels` | map<string,string>? | Free-form metadata. Used by `ListAsync(labelPrefix)` filtering. |
| `annotations` | map<string,string>? | Operator-visible metadata not indexed by the registry. K8s-style. |
| `memory` | object? | Pluggable memory-store backing. `{provider, connectionName?, scope?, historyReducer?}`. Null = ephemeral. |
| `identity` | object? | Inbound/outbound auth configuration. `{inboundAuth?, outboundCredentials?, credentials?, inboundClaims?}`. Null = unauthenticated. |
| `autoscaling` | object? | `{minReplicas, maxReplicas?, target?, targetValue?, idleTtl?}`. Consumer-defined target metric string. **Passed through; not wired to HPA by the operator in v0.13.** |
| `model` | object? | LLM binding for declarative agents. `{provider, id, apiKeyRef?, baseUrlRef?, temperature?, topP?, maxTokens?, responseFormat?}` — individual fields, no nested `parameters` object. |
| `systemPrompt` | object? | `{inline?, templateRef?, fileRef?}` — exactly one shape set. |
| `mcpServers` | array? | MCP server refs — each contributes tools at activation time. |
| `a2aRemoteAgents` | array? | A2A remote-agent refs consumed by `a2a:<name>` tool sources. Translator validates; full `A2ARemoteAgentTool` materialisation is preview. |
| `localAgents` | array? | Local same-runtime agent refs consumed by `agent:<name>` tool sources (v0.18 agent-as-tool). |
| `guardrails` | object? | `{input?, output?, tool?}` — three-layer guardrail bindings. |
| `handoffs` | array? | Declarative handoff targets by agent id. |
| `budget` | object? | `{maxTurns?, maxPromptTokens?, maxCompletionTokens?, maxToolCalls?, maxDuration?}`. |
| `contextProviders` | array? | Context-provider refs resolved against host DI keyspace. |
| `outputSchema` | object? | Inline JSON Schema for the final assistant turn's structured output. Mapped to the provider's `response_format` when supported (per `SupportsResponseFormat` DIM). |
| `agentMode` | string | `ToolCalling` (default), `Reasoning` (contract-only in v0.6). |
| `reasoning` | object? | Schema-Guided Reasoning configuration. |
| `observability` | object? | `{langfuseProject?, sampling?, tags?}` — overlay for trace emission. |
| `llmGatewayRef` | string? | Reference to a deployed `LlmGatewayConfigManifest` id. Builds a per-agent gateway pipeline that replaces the DI-global chain. |
| `mcpGatewayRef` | string? | Reference to a deployed `McpGatewayConfigManifest` id. Same shape for tool-gateway middleware. |
| `secretRefs` | map<string, object>? | Logical-name → `{name, key}` mappings. Validation-only in v0.13 — not injected into the runtime-side manifest. |
| `preserveOnDelete` | bool | Default `false`. When `true`, CR deletion skips `EvictAsync`. |

Any sub-schema not listed above but accepted by the CRD is covered by `x-kubernetes-preserve-unknown-fields: true` — the CRD validator doesn't reject unknown keys. Fields the KubeOps 10.3.4 transpiler couldn't shape-check (TimeSpan fields like `RunBudget.MaxDuration`, `AutoscalingSpec.IdleTtl`) land under opaque sub-schemas.

### Secret references

```yaml
spec:
  secretRefs:
    openai-key:
      name: openai-credentials
      secretKey: api-key
    stripe-key:
      name: stripe-credentials
      secretKey: secret-key
```

- `name` — K8s Secret name in the CR's namespace.
- `key` (serialised as `secretKey` on the CR) — data key within the Secret.

**v0.13 behaviour:** operator resolves each ref against the K8s API at reconcile time; missing secret or missing key → `ManifestValid = False` / `Reason = SecretResolutionFailed`. Resolved values are **not** injected into the manifest — callers reference secrets in manifest fields via `env:` / `file:` URIs, and the runtime's existing `ISecretResolver` composite picks them up at agent-instantiation. Inline-value wire format lands in a later pillar.

## `status` schema

Operator-managed; read-only from the CR author's perspective.

| Field | Type | Purpose |
|---|---|---|
| `agentHandle` | object? | `{agentId, version, instanceId}` — three-tuple returned by `IAgentLifecycleManager.CreateAsync`. Null until first successful create. |
| `manifestRevision` | string? | `sha256:<hex>` of canonical-JSON(spec) at last successful upsert. Drives the Create-vs-Update decision on next reconcile. |
| `phase` | enum | See table below. |
| `lastReconciledAt` | dateTime? | UTC timestamp of the last reconcile pass (success or failure). |
| `lastError` | string? | Exception message from the last failed reconcile. Null on success. |
| `observedGeneration` | int | `metadata.generation` as of the last status write. Compare against live `metadata.generation` to detect stale status. |
| `conditions` | array | Three entries — `Ready`, `Synced`, `ManifestValid`. |

### `phase` enum

| Value | Name | Meaning |
|---|---|---|
| 0 | `Pending` | CR seen but not yet reconciled. Transient. |
| 1 | `Creating` | `CreateAsync` call in flight. Transient. |
| 2 | `Active` | Registered in runtime, last reconcile succeeded. |
| 3 | `Updating` | `UpdateAsync` call in flight after spec-hash drift. Transient. |
| 4 | `Error` | Last reconcile failed, backing off. |
| 5 | `Terminating` | `metadata.deletionTimestamp` set, finalizer running. |

### Condition shape

Each entry:

```yaml
- type: Ready                                # "Ready" | "Synced" | "ManifestValid"
  status: True                               # "True" | "False" | "Unknown"
  reason: ReconcileSucceeded                 # CamelCase stable reason code
  message: Agent registered with control plane.
  lastTransitionTime: 2026-04-20T10:15:00Z
  observedGeneration: 1
```

### Reason vocabulary

Stable CamelCase strings — consumers script against them.

| Reason | Fires on | Paired conditions |
|---|---|---|
| `ReconcileSucceeded` | Successful reconcile pass. | `Ready=True`, `Synced=True`, `ManifestValid=True` |
| `RuntimeMatchesSpec` | Spec-hash equals `manifestRevision`; no action taken. | `Synced=True` |
| `ValidationPassed` | Manifest validation succeeded (both locally + on the runtime side). | `ManifestValid=True` |
| `ManifestInvalid` | Manifest failed validation (schema, shape, handoff references). | `Ready=False`, `ManifestValid=False` |
| `SecretResolutionFailed` | `spec.secretRefs` points at a missing secret or key. | `Ready=False`, `ManifestValid=False` |
| `ReconcileFailed` | Operational error — control plane unreachable, network timeout, non-4xx 5xx. | `Ready=False`, `Synced=Unknown`, `ManifestValid=Unknown` |
| `EvictionFailed` | `EvictAsync` returned an error during finalizer run. | `Ready=False` (CR stays in `Terminating`) |

## Full example

```yaml
apiVersion: vais.io/v1alpha1
kind: Agent
metadata:
  name: support-triage
  namespace: customer-support
  annotations:
    vais.io/tenant-id: tenant-42
    vais.io/owner-team: support-platform
spec:
  agentId: support-triage
  version: "2.3"
  description: Routes customer queries to billing or technical specialists.
  handler:
    typeName: MyApp.SupportTriageAgent
  protocols:
    - kind: Http
    - kind: A2A
  tools:
    - name: lookup_customer
      source: mcp:crm-server
    - name: escalate_to_human
  memory:
    backend: redis
    config:
      keyPrefix: support-triage
  model:
    provider: openai
    id: gpt-4o
    parameters:
      temperature: 0.3
  budget:
    maxTurns: 6
    maxToolCalls: 8
    maxDuration: 30s
  labels:
    tenantId: tenant-42
    tier: customer-facing
  secretRefs:
    openai-key:
      name: openai-credentials
      secretKey: api-key
  preserveOnDelete: false
```

After reconcile:

```yaml
status:
  agentHandle:
    agentId: support-triage
    version: "2.3"
    instanceId: a8b9c7de-f012-4567-89ab-cdef01234567
  manifestRevision: sha256:9f2d8c5e4b7a6d3c2f1e0d9c8b7a6d5e4c3b2a1
  phase: Active
  lastReconciledAt: 2026-04-20T10:15:00Z
  observedGeneration: 1
  conditions:
    - type: Ready
      status: "True"
      reason: ReconcileSucceeded
      message: Agent registered with control plane.
      lastTransitionTime: 2026-04-20T10:15:00Z
      observedGeneration: 1
    - type: Synced
      status: "True"
      reason: RuntimeMatchesSpec
      message: Runtime state matches desired spec.
      lastTransitionTime: 2026-04-20T10:15:00Z
      observedGeneration: 1
    - type: ManifestValid
      status: "True"
      reason: ValidationPassed
      message: Spec passed all validation checks.
      lastTransitionTime: 2026-04-20T10:15:00Z
      observedGeneration: 1
```

## See also

- [Kubernetes operator concept](../concepts/kubernetes-operator.md) — reconcile loop, phase state machine, condition wiring.
- [Deploy the Kubernetes operator](../guides/deploy-the-kubernetes-operator.md) — apply CRs against a running operator.
- [Control plane concept](../concepts/control-plane.md) — the `AgentManifest` shape the operator projects into.
- [Problem-details URNs](problem-details-urns.md) — error URNs the operator surfaces on failure.
- `deploy/crds/vais.io_agents.yaml` — the authoritative CRD manifest.
