# Reference: AgentManifest schema

Hand-written field reference for the `AgentManifest` wire format. The
authoritative machine-readable schema is the OpenAPI component at
`GET /openapi/v1.json` (`#/components/schemas/AgentManifest`); this page adds
intent, defaults, and cross-field constraints that JSON Schema cannot express.

See also: [Declarative agents concept](../concepts/declarative-agents.md) ·
[Author an agent in YAML guide](../guides/author-an-agent-in-yaml.md) ·
[Agent CRD reference](agent-crd.md) (Kubernetes-specific fields)

---

## Top-level fields

| Field | Type | Required | Notes |
|---|---|---|---|
| `apiVersion` | string | yes | Always `vais.agents/v1`. |
| `kind` | string | yes | `Agent` for a single agent; `AgentGraph` for a multi-agent graph. |
| `metadata.id` | string | yes | Stable identifier — immutable after first apply. |
| `metadata.version` | string | yes | Version tag. Increment on every schema-breaking change. |
| `metadata.description` | string | no | Human-readable description shown in dashboards. |
| `metadata.labels` | map | no | Free-form key/value pairs for `ListAsync` filtering. |
| `spec.handler.typeName` | string | yes | `declarative` for LLM-backed agents; fully-qualified .NET type name or OCI ref for plugins. |
| `spec.protocols` | array | yes | Protocol bindings. Empty array is valid. See `{kind: Http}`. |
| `spec.tools` | array | yes | Tool references. Empty array is valid. |
| `spec.model` | object | no | LLM binding. Required for declarative agents; absent for pure plugin agents. |
| `spec.systemPrompt` | object | no | Prompt source. Exactly one of `inline`, `templateRef`, `fileRef`. |
| `spec.budget` | object | no | Run budget. All sub-fields optional. See [budget reference](budget.md). |
| `spec.mcpServers` | array | no | MCP server declarations consumed by `mcp:<name>` tool sources. |
| `spec.a2aRemoteAgents` | array | no | Remote A2A agent declarations consumed by `a2a:<name>` tool sources. Translator validates declarations today; lazy `A2ARemoteAgentTool` instantiation is preview (see [delegate-to-a2a-remote-agent](../guides/delegate-to-a2a-remote-agent.md)). |
| `spec.localAgents` | array | no | Local (same-runtime) agent bindings consumed by `agent:<name>` tool sources. v0.18. |
| `spec.guardrails` | object | no | `{input?, output?, tool?}` guardrail bindings. |
| `spec.handoffs` | array | no | Declarative handoff targets — other agents this manifest can delegate control to by id. |
| `spec.contextProviders` | array | no | DI-keyed `IContextProvider` references bound to this agent. |
| `spec.outputSchema` | object | no | Inline JSON Schema for the final assistant turn. Mapped to provider `response_format` when the provider's `SupportsResponseFormat` DIM is true. |
| `spec.agentMode` | string | no | Execution-loop flavour. Default `toolCalling`. Other values (`sgr`) are contract-only at the wire layer. |
| `spec.reasoning` | object | no | Schema-Guided Reasoning configuration. Contract-only — engine still treats as tool-calling. |
| `spec.memory` | object | no | Pluggable memory store. `{provider, connectionName?, scope?, historyReducer?}`. Null = ephemeral. |
| `spec.identity` | object | no | Inbound/outbound auth. `{inboundAuth?, outboundCredentials?, credentials?, inboundClaims?}`. Null = unauthenticated dev / single-tenant. |
| `spec.autoscaling` | object | no | Replica/concurrency hints. `{minReplicas, maxReplicas?, target?, targetValue?, idleTtl?}`. Consumer-defined target metric string. |
| `spec.observability` | object | no | `{langfuseProject?, sampling?, tags?}` trace-emission overlay. |
| `spec.annotations` | map | no | Free-form operator-visible metadata. Parallel to K8s annotations. Not indexed by the registry. |
| `spec.llmGatewayRef` | string | no | Reference to a deployed `LlmGatewayConfigManifest` id. When set, the translator builds a per-agent `LlmGatewayPipeline` from that config (replaces — not appends to — the DI-global chain). |
| `spec.mcpGatewayRef` | string | no | Reference to a deployed `McpGatewayConfigManifest` id. When set, a per-agent `ToolGatewayMiddleware` chain is built from that config. |

---

## `spec.model` — ModelSpec

Controls which LLM the agent calls. Setting `model` opts the agent into the
declarative path; the runtime routes to the matching `IModelProviderFactory` and
constructs the provider at activation time.

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `provider` | string | yes | — | Case-insensitive key matched against registered factories. Built-ins: `openai`, `anthropic`, `azure-openai`. |
| `id` | string | yes | — | Model identifier the provider understands. E.g. `gpt-4o`, `claude-3-7-sonnet`, Azure deployment name. |
| `apiKeyRef` | string | no | — | `secret://` URI resolved at activation time by the `ISecretResolver` composite. |
| `baseUrlRef` | string | no | — | `secret://` URI that resolves to the API endpoint URL. See [Custom endpoints](#custom-endpoints-baseurlref) below. |
| `temperature` | number | no | provider default | Sampling temperature `[0.0, 2.0]`. |
| `topP` | number | no | provider default | Nucleus-sampling threshold. |
| `maxTokens` | int | no | provider default | Max completion tokens. |
| `responseFormat` | string | no | `text` | `text`, `json`, or `structured`. |

### Secret reference format

Both `apiKeyRef` and `baseUrlRef` accept `secret://` URIs. The default resolver
composite (`CompositeSecretResolver.CreateDefault()`) supports two schemes:

| URI form | Resolution |
|---|---|
| `secret://env/VAR_NAME` | `Environment.GetEnvironmentVariable("VAR_NAME")` |
| `secret://file/absolute/path` | File contents, trimmed. Useful for K8s projected Secrets. |

Unresolvable refs → `400 urn:vais-agents:model-provider-unsupported` at
invocation time.

### Custom endpoints — `baseUrlRef`

`baseUrlRef` lets you point any `openai`-provider agent at an
OpenAI-compatible REST endpoint without writing a custom factory. Practical uses:

- **SGR Agent sidecar** — `http://sgr:8010/v1` (Schema-Guided Reasoning)
- **Self-hosted models** — Ollama, llama.cpp, vLLM, etc.
- **Proxy gateways** — LiteLLM, Portkey, OpenRouter
- **Azure OpenAI** — use the `azure-openai` provider instead (it also uses `baseUrlRef` internally)

Example — SGR Agent sidecar wired via compose env var:

```yaml
spec:
  model:
    provider: openai
    id: sgr-deep-research
    apiKeyRef: secret://env/OPENAI_API_KEY
    baseUrlRef: secret://env/SGR_ENDPOINT   # resolves to http://sgr:8010/v1
```

Inject `SGR_ENDPOINT` (or whatever variable your `baseUrlRef` resolves) into
the runtime container's environment — via a Compose `environment:` block, a K8s
Secret, or the host shell for local dev. The
[Configure LLM providers](../devops/configure-llm-providers.md) devops guide
walks through end-to-end wiring for vLLM, Ollama, LiteLLM, Azure, and SGR.

Resolution errors:

| Condition | Error |
|---|---|
| `baseUrlRef` resolves to an empty string | `urn:vais-agents:model-provider-unsupported` — "resolved to an empty value" |
| Resolved string is not a valid URI | `urn:vais-agents:model-provider-unsupported` — "not a valid URI" |

---

## `spec.systemPrompt` — SystemPromptSpec

Exactly one sub-field must be set; setting more than one ⇒
`urn:vais-agents:prompt-spec-ambiguous`.

| Sub-field | Description |
|---|---|
| `inline: "..."` | Literal string. Supports `{{variable}}` substitution via `variables` map. |
| `templateRef: "name"` | Resolves through `IPromptTemplateRegistry`. |
| `fileRef: "file.prompt"` | Resolves through `IPromptFileLoader` (default: filesystem with path-traversal guard). |

---

## `spec.localAgents` — LocalAgentRef

Declares local (same-runtime) agents this coordinator can invoke as tools.
Referenced by `ToolRef.Source = "agent:<name>"`. Added in v0.18 (closes P7 —
agent-as-tool over peer A2A is the default pattern).

Use `agent:<name>` instead of `a2a:` when the target agent is in the same runtime;
reserve `a2a:` for cross-runtime or cross-org delegation. See the
[delegate-to-a-local-agent](../guides/delegate-to-a-local-agent.md) guide.

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `name` | string | yes | — | Unique binding name within this manifest. Referenced as `agent:<name>`. |
| `agentId` | string | no | same as `name` | Target agent id in the registry. Defaults to `name` when omitted. |
| `agentVersion` | string | no | latest | Pinned version. Null = latest lexicographic version. |
| `mode` | string | no | `Blocking` | `Blocking` (coordinator waits for result) or `Background` (fire-and-forget, Phase 2). |
| `description` | string | no | derived | Tool description override. Null = derived from target agent's manifest `description`. |
| `allowCallerSuppliedSession` | bool | no | `false` | When `true`, the LLM may pass an optional `sessionId` argument to enable multi-turn sub-conversations. |
| `propagateAllowedTools` | bool | no | `true` | Propagate the caller's `allowedTools` set to the child. Set `false` to let the child run under its own manifest's full tool set. |

### Tool source syntax

All four prefixes:

```yaml
spec:
  tools:
    - { name: local_weather,    source: static:weather }        # IStaticToolRegistry-backed in-process tool
    - { name: search_web,       source: mcp:open-meteo }        # references mcpServers[].name (materialised)
    - { name: ask_translator,   source: a2a:translator }        # references a2aRemoteAgents[].name (validation-only today)
    - { name: call_summarizer,  source: agent:summarizer }      # references localAgents[].name (materialised)
  mcpServers:
    - { name: open-meteo, transport: streamableHttp, url: https://mcp.example.org/weather }
  a2aRemoteAgents:
    - { name: translator, agentUrl: https://a2a.partner.example/agents/translator }
  localAgents:
    - { name: summarizer, agentId: summarizer-agent }
```

Unknown prefix ⇒ `urn:vais-agents:tool-source-unknown`. Undeclared name ⇒ `urn:vais-agents:{mcp-server,a2a-agent,local-agent}-not-declared`. See [declarative-agents concept](../concepts/declarative-agents.md) for the full URN table.

---

## `spec.budget` — RunBudget

See [RunBudget reference](budget.md) for full field descriptions and enforcement behaviour.

| Field | Type | Notes |
|---|---|---|
| `maxTurns` | int | LLM invocations per run. |
| `maxDuration` | duration | ISO 8601 duration string — e.g. `PT30S`, `PT2M`. |
| `maxPromptTokens` | int | Cumulative prompt tokens across all turns. |
| `maxCompletionTokens` | int | Cumulative completion tokens across all turns. |
| `maxToolCalls` | int | Total tool dispatches per run. |

---

## Minimal example

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: my-agent
  version: "1.0"
spec:
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: "You are a helpful assistant."
```
