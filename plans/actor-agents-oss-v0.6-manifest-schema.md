# v0.6 — Agent manifest schema

Detailed design sketch for the YAML / JSON manifest that `AgentManifest` records, the HTTP control plane accepts as request bodies, and the (future) K8s operator reconciles from CRDs. Companion to [`actor-agents-oss-v0.6-control-plane-pillar.md`](./actor-agents-oss-v0.6-control-plane-pillar.md). Created 2026-04-19.

**This is not a spec yet.** It's a design sketch detailed enough to size PR 2 (YAML loader) and scope the expanded `AgentManifest` record that PR 1 needs. Bits marked _(contract-only in v0.6)_ ship the wire shape but not the execution path — the engine accepts and round-trips them, the fields just don't do anything at runtime until a later pillar lands.

---

## 1. Top-level shape

Kubernetes-style `apiVersion` + `kind` + `metadata` + `spec`. Chosen for two reasons:

1. Operators who work with Kubernetes recognise the pattern instantly — zero-cost familiarity.
2. If we ship a K8s CRD later (deferred to post-v0.6), the manifest is already in the right shape — the CRD becomes a native wrapper, not a translation.

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: support
  version: "1.0"
  labels:
    team: support
    env: prod
spec:
  # see §3, §4, §5
```

`apiVersion` pins both the manifest's semantic version (`v1`) and the runtime's compatibility window. Manifest-version bumps are flag-days — `vais.agents/v2` is a wholly new schema, not a negotiated upgrade.

`kind: Agent` is the only kind in v0.6. Future kinds (`AgentPrompt`, `AgentTool`, `AgentPolicy`) are reserved but not used — keeps the namespace clean if / when we split into multiple kinds later.

## 2. `metadata`

| Field | Type | Required | Notes |
|---|---|---|---|
| `id` | string | yes | Slug: `[a-z][a-z0-9-]{0,62}`. Stable; renames are destroy-and-recreate. |
| `version` | string | yes | `major.minor` minimum, `major.minor.patch` recommended. Two agents with the same `id` but different `version` coexist; list / get default to latest-lexicographic. |
| `displayName` | string | no | Human-readable name for UIs. Defaults to `id`. |
| `description` | string | no | Free-form text; surfaces in generated OpenAPI + logs. |
| `labels` | map<string, string> | no | K8s label semantics: keys `[a-z0-9][a-z0-9_.-]*`, max 63 chars; values max 63 chars. Indexed for list-filter queries. |
| `annotations` | map<string, string> | no | Free-form metadata; not indexed. Meant for operator-visible notes (`ownedBy`, `runbook`, etc). |

## 3. `spec` — library layer (parity with OpenAI `Agent(...)`)

The atom-level configuration every agent framework has converged on. Manifest projects 1:1 onto what you'd set in `StatefulAgentOptions` today — same knobs, same defaults, declarative.

### 3.1 `model`

```yaml
model:
  provider: openai           # openai | anthropic | google | mistral | azureOpenAi | local | custom
  id: gpt-4.1                # model identifier the provider understands
  apiKeyRef: secret://keyvault/prod/openai-key
  baseUrlRef: secret://env/OPENAI_BASE_URL   # optional override for Azure / proxy / local
  temperature: 0.2
  topP: 0.9
  maxTokens: 4096
  responseFormat: text        # text | json | structured (maps to Vais.Agents.CompletionResponse shape)
```

Only `provider` + `id` + `apiKeyRef` are required. Everything else has framework or provider defaults.

### 3.2 `systemPrompt`

Three shapes. Exactly one of the three must be present:

```yaml
systemPrompt:
  inline: |
    You are a support agent. Be brief, warm, and honest.
```

```yaml
systemPrompt:
  templateRef: shared-support-prompt   # named template resolved via ISystemPromptComposer
  variables:
    product: Vais.Agents
```

```yaml
systemPrompt:
  fileRef: prompts/support.md          # path relative to manifest file (loader resolves)
```

The `templateRef` + `variables` shape hooks into the existing `IPromptTemplate` + `FormatStringPromptTemplate` surface shipped in v0.4 PR 6.

### 3.3 `tools`

List of refs by name into the DI-registered `IToolRegistry`. Params are tool-specific and opaque to the manifest loader.

```yaml
tools:
  - name: search
  - name: escalate
    params:
      priority: high
```

### 3.4 `mcpServers`

First-class field because an MCP server is a *source* of tools (potentially many), not a single tool. Each entry describes transport + connection + optional tool-name allowlist.

```yaml
mcpServers:
  - name: filesystem
    transport: stdio
    command: mcp-fs
    args: ["--root", "/data"]
    env:
      LOG_LEVEL: info
    tools:                         # optional allowlist; omit to expose all server tools
      - read_file
      - list_dir
  - name: github
    transport: streamableHttp
    url: https://mcp.github.com/v1
    authRef: secret://keyvault/prod/github-mcp-token
```

Transports: `stdio`, `streamableHttp`, `sse`. One of `command`+`args` (stdio) or `url` (http/sse) required.

### 3.5 `guardrails`

Three parallel lists matching the shipped three-layer guardrail split (v0.4 PR 7). Each entry is a ref into the DI-registered guardrail catalog.

```yaml
guardrails:
  input:
    - name: pii-filter
  output:
    - name: json-schema
      params:
        schema: { $ref: "#/$defs/finalAnswer" }
  tool:
    - name: approval-gate
      params:
        requireApprovalFor: ["send_email", "charge_card"]
```

### 3.6 `handoffs`

List of agent ids this manifest can hand off to. Shipped `Handoff` record from v0.4 PR 12.

```yaml
handoffs:
  - toAgent: billing
    when: "user asks about invoices, refunds, or subscription"
    carryHistory: true
```

`when` is a free-form hint used by the orchestrator's termination-condition logic (today) or by a future routing LLM (post-v0.6).

### 3.7 `budget`

Direct projection of `RunBudget` (v0.4 PR 8).

```yaml
budget:
  maxTurns: 5
  maxToolCalls: 10
  maxPromptTokens: 8000
  maxCompletionTokens: 2000
  maxDuration: 30s     # Go-style: 30s | 2m | 1h
```

All fields optional. Missing = unlimited.

### 3.8 `memory`

```yaml
memory:
  kind: redis            # inMemory | redis | postgres | vectorData | custom
  connectionRef: secret://keyvault/prod/redis-url
  scope: session         # session | agent
  historyReducer:
    name: last-n
    params:
      n: 20
```

`kind: custom` takes a `factory: <di-key>` field instead, resolved by the host's DI.

### 3.9 `contextProviders`

Matches `IContextProvider` chain (v0.4 PR 4).

```yaml
contextProviders:
  - name: rag
    params:
      collection: kb-support
      topK: 5
      embeddingModel: text-embedding-3-large
  - name: current-user-context
```

### 3.10 `outputSchema`

Structured-output shape for the *final* assistant turn (distinct from `reasoning.schema` — see §4). Inline JSON Schema or `schemaRef`.

```yaml
outputSchema:
  type: object
  properties:
    answer: { type: string }
    confidence: { type: number, minimum: 0, maximum: 1 }
  required: [answer]
```

## 4. `spec` — reasoning layer (SGR-ready, contract-only in v0.6)

_Shipped as manifest fields + stored on the `AgentManifest` record; engine honours only `agentMode: toolCalling` in v0.6. Schema-guided execution paths land in a post-v0.6 "reasoning pillar" — but reserving the wire shape now keeps that future PR additive._

### 4.1 `agentMode`

```yaml
agentMode: toolCalling   # toolCalling | schemaGuided | schemaGuidedToolCalling
```

- `toolCalling` (default) — current `StatefulAiAgent` behaviour. Free-form assistant output + tool calls via the outer loop.
- `schemaGuided` — LLM completes against `reasoning.schema` as its primary response. No tool calling. Schema IS the output.
- `schemaGuidedToolCalling` — SGR hybrid. Each turn: schema-guided reasoning → tool decision → tool call → repeat.

### 4.2 `reasoning`

```yaml
reasoning:
  pattern: cascade        # cascade | routing | cycle
  schema:                 # inline JSON Schema; field ORDER is load-bearing (see §7)
    type: object
    properties:
      brief_summary:       { type: string }
      key_entities:        { type: array, items: { type: string } }
      keywords:            { type: array, items: { type: string } }
      document_type:       { type: string, enum: [invoice, receipt, contract] }   # conclusion last
    required: [brief_summary, key_entities, keywords, document_type]
  # or:
  # schemaRef: "#/schemas/documentClassification"
  maxIterations: 10          # for pattern: cycle
  maxClarifications: 3       # for any pattern — caps AgentInterrupt rounds
```

**Pattern semantics:**

- **`cascade`** — schema fields fill top-to-bottom; each field's completion sees prior fields as primed context. The canonical SGR pattern.
- **`routing`** — one `enum` field gates which subsequent fields are populated. State-machine-shaped.
- **`cycle`** — same schema completes repeatedly with a `continue_reasoning` boolean field; caps at `maxIterations`.

`schema` / `schemaRef` — exactly one. `schemaRef` is an opaque name resolved by the host's DI (lets large schemas live outside the manifest).

## 5. `spec` — control-plane layer

Everything that distinguishes a managed agent from an embedded-library agent. These fields have no equivalent in the OpenAI / MAF / SGR code-first SDKs — they're specifically what turning a library into a runtime needs.

### 5.1 `protocols`

Which wire protocols this agent exposes. Plugs into the HTTP control plane's routing + the (future) MCP/A2A inbound adapters.

```yaml
protocols:
  - kind: Http
    path: /support         # mounted under /v1/agents/support/invoke...
  - kind: Mcp              # inbound MCP endpoint (post-v0.6)
    config: { toolNames: [ask_support] }
  - kind: A2A              # inbound A2A endpoint (post-v0.6)
```

### 5.2 `identity`

```yaml
identity:
  inboundAuth: oidc              # oidc | apiKey | anonymous
  inboundClaims:                 # optional claim requirements for JWT bearer
    scope: "agent:invoke"
  outboundCredentials:
    - name: openai-api
      ref: secret://keyvault/prod/openai-key
      type: bearer
    - name: stripe
      ref: secret://keyvault/prod/stripe-client
      type: oauth2ClientCredentials
```

`outboundCredentials[n].name` is the key tools / adapters inside the agent use to look up credentials at invocation time (via the identity provider).

### 5.3 `autoscaling`

```yaml
autoscaling:
  minReplicas: 1
  maxReplicas: 5
  target:
    metric: activeInvocations
    value: 3
  idleTtl: 5m              # deactivate idle replicas after this
```

Hint-shaped. The host's scheduler decides whether to honour it.

### 5.4 `observability`

```yaml
observability:
  langfuseProject: support-prod
  samplingRate: 0.1
  tags:
    team: support
    env: prod
  tracingEnabled: true
```

Overlays atop the OTel GenAI + `vais.*` semantic conventions already shipped.

## 6. Refs + secrets

### `secret://` URIs

All credential-bearing fields use `secret://<backend>/<path>`. Backends pluggable via DI:

- `secret://env/<VAR>` — read from environment variable.
- `secret://file/<path>` — read from file (path relative to host, not manifest).
- `secret://keyvault/<path>` — Azure Key Vault.
- `secret://awssm/<path>` — AWS Secrets Manager.
- `secret://custom/<key>` — resolved via host-registered `ISecretResolver`.

Manifest loader never dereferences these — secret resolution happens at agent activation time, inside the runtime. Manifests are safe to commit.

### Ref-vs-inline policy

| Field | Default | Alternative |
|---|---|---|
| `systemPrompt` | `inline` | `templateRef` / `fileRef` |
| `tools[n].params` | inline | — |
| `mcpServers[n]` | inline | — |
| `guardrails[n].params` | inline | — |
| `reasoning.schema` | inline | `schemaRef` |
| `outputSchema` | inline | JSON Schema `$ref` |
| credentials | always `secret://` | — (never inline) |

Principle for MVP: **inline everything but secrets**. If two agents end up duplicating the same prompt, we split it out in a follow-up — not before.

## 7. Validation

Enforced by `YamlAgentManifestLoader` before returning an `AgentManifest`:

| Rule | Error class |
|---|---|
| `metadata.id` matches `^[a-z][a-z0-9-]{0,62}$` | `manifest-invalid` |
| `metadata.version` matches `^\d+\.\d+(\.\d+)?$` | `manifest-invalid` |
| `metadata.labels` keys match K8s label-key regex, values ≤ 63 chars | `manifest-invalid` |
| Exactly one of `systemPrompt.{inline, templateRef, fileRef}` set | `manifest-invalid` |
| Exactly one of `reasoning.{schema, schemaRef}` set (when `reasoning` present) | `manifest-invalid` |
| `budget.*` numeric fields > 0 | `manifest-invalid` |
| `budget.maxDuration` parses as duration (`30s` / `2m` / `1h`) or ISO 8601 | `manifest-invalid` |
| `autoscaling.minReplicas` ≤ `maxReplicas` | `manifest-invalid` |
| No duplicate agent `id`s within a batch load | `manifest-invalid` |
| `mcpServers[n]` has either `command` or `url` (mutually exclusive) | `manifest-invalid` |
| **Key-ordering preserved end-to-end** for `reasoning.schema` + `outputSchema` properties | loader invariant, not an error |

The key-ordering preservation matters for SGR: YAML preserves key order in parsers; our loader must preserve it into the `AgentManifest` record's schema fields (use `JsonNode` / `JsonObject`, not a plain `Dictionary<string,object>`) so the ordering survives until the runtime emits a completion request.

## 8. Examples

### 8.1 Minimal agent

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: echo
  version: "1.0"
spec:
  model:
    provider: openai
    id: gpt-4.1-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: "Repeat back what the user said, verbatim."
```

### 8.2 RAG agent

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: kb-support
  version: "1.2"
  labels: { team: support }
spec:
  model:
    provider: openai
    id: gpt-4.1
    apiKeyRef: secret://keyvault/prod/openai-key
    temperature: 0.1
  systemPrompt:
    fileRef: prompts/kb-support.md
  memory:
    kind: redis
    connectionRef: secret://keyvault/prod/redis-url
    scope: session
  contextProviders:
    - name: rag
      params:
        collection: support-kb
        topK: 6
        embeddingModel: text-embedding-3-large
  tools:
    - name: open_ticket
  budget:
    maxTurns: 8
    maxDuration: 45s
  protocols:
    - { kind: Http, path: /support }
  identity:
    inboundAuth: oidc
    inboundClaims: { scope: "agent:invoke" }
  observability:
    langfuseProject: support-prod
    samplingRate: 0.2
```

### 8.3 SGR deep-research agent (reasoning-layer preview — contract-only in v0.6)

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: sgr-research
  version: "0.1"
spec:
  model:
    provider: openai
    id: gpt-4.1
    apiKeyRef: secret://env/OPENAI_API_KEY
    responseFormat: structured
  systemPrompt:
    inline: "You are a research agent. Decompose the question, plan, execute, adapt."
  agentMode: schemaGuidedToolCalling     # contract-only in v0.6; engine treats as toolCalling for now
  reasoning:
    pattern: cycle
    maxIterations: 12
    maxClarifications: 3
    schema:
      type: object
      properties:
        understanding:       { type: string, description: "What the user actually asked" }
        plan_steps:          { type: array, items: { type: string } }
        next_action:         { type: string, enum: [search, extract, clarify, conclude] }
        action_argument:     { type: string }
        findings_so_far:     { type: string }
        continue_reasoning:  { type: boolean }
  tools:
    - { name: web_search }
    - { name: extract_page }
    - { name: clarify_with_user }
  budget:
    maxTurns: 12
    maxToolCalls: 24
    maxDuration: 10m
```

### 8.4 Multi-agent handoff

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata: { id: triage, version: "1.0" }
spec:
  model: { provider: openai, id: gpt-4.1-mini, apiKeyRef: secret://env/OPENAI_API_KEY }
  systemPrompt:
    inline: "Decide which specialist handles the request. Don't answer yourself."
  handoffs:
    - { toAgent: kb-support, when: "product or technical questions" }
    - { toAgent: billing,    when: "invoices, refunds, subscription" }
    - { toAgent: sales,      when: "upgrade, new features, pricing" }
```

## 9. Open questions

1. **SGR execution depth for v0.6** — ship `agentMode` + `reasoning` as contract-only (current plan), OR delay the reasoning-layer fields to v0.7 entirely and keep v0.6's manifest thinner? Contract-only argues for "forward-compat now, zero implementation cost"; delaying argues for "don't ship fields that don't do anything."
2. **Schema versioning for SGR reasoning schemas** — is the reasoning schema version coupled to `metadata.version`, or does it need its own `reasoning.schemaVersion` field so you can evolve prompts independently of schemas?
3. **Schema composition / extension** — do we allow base manifests + overlays (`extends: base-support`) for shared config? K8s-wise, Kustomize solves this separately. Answer might be "no, use Kustomize or Helm" rather than building it into our loader.
4. **CRD mapping when K8s operator lands** — 1:1 structural mapping or a restructured CRD with standard `spec.replicas` etc? Leaning 1:1 for simplicity.
5. **Multi-document YAML vs multi-file** — both work (`---` separator for multi-doc, or a directory of individual files); loader supports both. Is that the right call or does one hurt more than it helps?
6. **JSON alongside YAML** — ship both loaders (`YamlAgentManifestLoader`, `JsonAgentManifestLoader` sharing a parser core) or YAML only? Leaning both — JSON is what the HTTP API uses natively; YAML loader is really a YAML→JSON normaliser in front of the same validator.
7. **Schema extension via `annotations`** — K8s uses annotations as the escape hatch for fields a CRD doesn't natively model. Same approach here? Reduces pressure to add fields prematurely.

---

## Progress log

- 2026-04-19 — initial draft. Covers library + reasoning + control-plane layers at enough depth to size PR 1 (manifest record expansion) and PR 2 (YAML loader). Open questions flagged for resolution before PR 1 coding starts.
