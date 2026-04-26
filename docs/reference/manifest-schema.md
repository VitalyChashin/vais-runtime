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
| `spec.guardrails` | object | no | `{input?, output?, tool?}` guardrail bindings. |
| `spec.memory` | object | no | `{backend, config?}` pluggable memory store. Null = ephemeral. |
| `spec.identity` | object | no | `{provider, audience?}` inbound/outbound auth. |
| `spec.observability` | object | no | `{langfuseProject?, sampling?, tags?}` trace-emission overlay. |

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

The `docker-compose.sgr.yml` overlay sets `SGR_ENDPOINT=http://sgr:8010/v1` in the
runtime container's environment, so the manifest above works without any other
configuration. See `deploy/compose/docker-compose.sgr.yml`.

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
