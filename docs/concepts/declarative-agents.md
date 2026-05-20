# Declarative agents

The runtime turns a stored `AgentManifest` into a running `StatefulAiAgent` without the consumer writing C# for the "pure-LLM-plus-tools-plus-guardrails" shape. Apply a YAML manifest with `vais apply -f`, and `vais invoke` returns a real response.

## The pipeline

Ten steps from `vais apply` to a live agent that answered its first prompt:

| # | Step | Where |
|---|---|---|
| 1 | HTTP POST `/v1/agents` with manifest JSON | `Control.Http.Server` |
| 2 | `AgentLifecycleManager.CreateAsync` → policy + audit + registry.Register | `Control.InProcess` |
| 3 | `OrleansAgentRegistry.Register` → per-id grain persists JSON payload | `Hosting.Orleans` |
| 4 | `vais invoke` → HTTP POST `/v1/agents/{id}/invoke` | `Control.Http.Server` |
| 5 | `AgentLifecycleManager.InvokeAsync` → `IAgentRuntime.GetOrCreate(id)` | `Control.InProcess` |
| 6 | `OrleansAgentRuntime` returns grain reference; grain activates | `Hosting.Orleans` |
| 7 | `AiAgentGrain.OnActivateAsync` calls `Func<string, StatefulAgentOptions>` factory | `Hosting.Orleans` |
| 8 | Factory = `(sp, id) => translator.TranslateForGrain(sp, id)` | `Runtime.Host` |
| 9 | `AgentManifestTranslator` loads manifest, resolves model + prompt + tools + guardrails + budget | `Runtime.Instantiation` |
| 10 | Grain constructs `StatefulAiAgent(options.CompletionProvider, options)` + `AskAsync` runs | `Core` |

The glue is step 8 — the host's composition root wires `ConfigureAgentGrains` to call the translator, which means every manifest stored via step 3 becomes a usable agent at step 10 automatically.

## Manifest shape

A minimum declarative manifest has `Model` set (that's the switch — `Model != null` opts into the declarative path):

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: weather
  version: "1.0"
spec:
  handler:
    typeName: declarative          # sentinel — no plugin registers this name; translator falls through to the declarative path
  model:
    provider: openai               # or: anthropic, azure-openai, or a custom registered factory
    id: gpt-4o
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: "You help with weather questions."
  tools: []
  budget:
    maxTurns: 5
    maxDuration: PT30S
```

Every `AgentManifest` field the translator consumes is documented in [manifest-schema reference](../reference/manifest-schema.md); the most load-bearing shapes:

### `ModelSpec`

- **`provider`** — case-insensitive match against a registered `IModelProviderFactory`. Ships with `openai`, `anthropic`, `azure-openai`. Register custom factories for Bedrock / Gemini / a native Ollama protocol by adding your own `IModelProviderFactory` to the runtime's composition root.
- **`id`** — model id (e.g. `gpt-4o`) or Azure deployment name.
- **`apiKeyRef`** — `secret://` URI resolved by the registered `ISecretResolver` composite (env + file by default). K8s projected Secrets work via `secret://file/var/run/secrets/vais/openai-key`.
- **`baseUrlRef`** — `secret://` URI resolving to a custom API endpoint. Works with any `openai`-provider agent to point at proxies, self-hosted models, or compatible sidecars (e.g. SGR Agent). Required for `azure-openai`. Optional and unused by default for OpenAI/Anthropic.

End-to-end credential wiring (env vs file, K8s Secret projection, custom endpoints, Fallback pools, custom resolver schemes) is in **[DevOps → Configure LLM providers](../devops/configure-llm-providers.md)**.

Unknown `provider` ⇒ `400 urn:vais-agents:model-provider-unsupported` at apply-validation time.

### `SystemPromptSpec`

Three shapes; set exactly one. Setting more than one ⇒ `urn:vais-agents:prompt-spec-ambiguous`.

1. **`inline: "..."`** — literal string. Optional `variables` dict substitutes `{{key}}` → value.
2. **`templateRef: "triage-intro"`** — resolves through `IPromptTemplateRegistry` (register templates at host startup via `services.AddPromptTemplateRegistry(b => b.Add(...))`).
3. **`fileRef: "triage.prompt"`** — resolves through `IPromptFileLoader`. Default `FileSystemPromptFileLoader` reads from a configured root directory (e.g. `/var/lib/vais/prompts/`) with path-traversal guard.

All three pass through the same `{{variable}}` substitution if `variables` is present.

### `Tools[].Source` prefixes

Four prefixes supported:

- **`static:<name>`** — resolves through `IStaticToolRegistry`. Register at host startup via `services.AddStaticToolRegistry(b => b.Add("weather", sp => new WeatherTool(sp.GetRequiredService<IHttpClientFactory>())))`.
- **`mcp:<server>`** — references a `McpServerRef` declared in `McpServers`. The translator materializes the tool through the MCP server registry at translate time — physical `transport: registered` servers via `PhysicalMcpConnectionService` (`Control.Mcp`), virtual servers via `VirtualMcpToolSource` — so the model calls it at runtime. A server named in a `mcp:<server>` tool entry is in explicit mode (only listed tools imported); other `transport: registered` servers import all their tools.
- **`a2a:<agent>`** — references an `A2ARemoteAgentRef` declared in the `A2ARemoteAgents` manifest section. Validates declaration only in v0.17.
- **`agent:<name>`** — references a `LocalAgentRef` declared in the `LocalAgents` manifest section (v0.18, closes P7 agent-as-tool). Resolves to a `LocalAgentTool` (Blocking mode, default) or `BackgroundLocalAgentTool` (Background mode); background mode additionally registers `list_background_agents` / `get_background_agent_result` / `cancel_background_agent` management tools. See [delegate-to-an-agent guide](../guides/delegate-to-an-agent.md).

Unknown prefix ⇒ `urn:vais-agents:tool-source-unknown`. Undeclared `mcp:` / `a2a:` / `agent:` name ⇒ `urn:vais-agents:{mcp-server,a2a-agent,local-agent}-not-declared`. Background-mode `agent:` without `IBackgroundAgentTracker` registered in DI also raises `tool-source-unknown` with a remediation message.

### `GuardrailsSpec`

Three ordered arrays: `input`, `output`, `tool`. Each entry is a `GuardrailRef(Name, Params)`. The translator dispatches `(Name, Layer)` through `IGuardrailFactory` lookups — six factories ship in v0.17:

| Name | Layer | Params |
|---|---|---|
| `LengthCap` | Input | `maxChars: int` |
| `RegexAllowlist` | Input / Output | `pattern: string` |
| `RegexDenylist` | Input / Output | `pattern: string` |
| `LlmAsJudge` | Output | `judgeModel: ModelSpec`, `judgePrompt: string`, `minScore: double` |

Unknown guardrail name ⇒ `urn:vais-agents:guardrail-not-registered`. Malformed params ⇒ `urn:vais-agents:guardrail-params-invalid`. Custom factories register via `services.AddSingleton<IGuardrailFactory, MyFactory>()` — see [ship-a-guardrail](../guides/ship-a-guardrail.md).

**Evaluation order.** Declaration order in the manifest. Input guardrails run before the completion provider; output guardrails run after the response returns; tool guardrails run around each tool invocation in `DefaultToolCallDispatcher` (the manifest `tool[]` array is wired through the translator into `StatefulAgentOptions.ToolGuardrails`). No Tool-layer guardrail ships as a built-in factory — register a custom `IGuardrailFactory` for the Tool layer.

**LLM-as-judge.** Nested `ModelSpec` builds a second `ICompletionProvider` via the same factory chain — and the `ICompletionProviderPool` memoises, so multiple judges against the same model share one SDK client. Watch for loops: using the agent's own model as its judge wastes tokens.

### `Budget`

`RunBudget` threaded straight through to `StatefulAgentOptions.Budget`. Enforced by the execution loop — the agent throws `AgentBudgetExceededException` when any cap is exceeded.

## Update semantics

`vais apply -f manifest.yaml` on an existing id:

1. Registry re-persists the new manifest.
2. `IAgentRuntime.Remove(id)` drops the cached grain reference.
3. `IAgentManifestInvalidator.InvalidateAsync(id)` clears the translator's options cache.
4. Next `vais invoke` triggers grain reactivation → translator re-runs → new options.

**In-flight runs are not touched** — the `StatefulAiAgent` started before the update keeps running with its original options. Partners who need immediate-drop-in semantics should `vais cancel` the run first.

## `handler.typeName` coexistence with the plugin loader

`AgentHandlerRef.TypeName` is required in the record. The translator consults the v0.18 plugin registry **before** it checks `Model` presence:

| `Handler.TypeName` matches a loaded plugin? | `Model` present | Behaviour |
|---|---|---|
| yes | no | **Plugin path.** Factory-produced `IAiAgent` lands on `StatefulAgentOptions.Agent`; grain uses it verbatim. |
| yes | yes | **Plugin wins + WARN.** Same as above, but the apply response carries `urn:vais-agents:handler-and-declarative-fields-both-set`. Declarative `Model` / `SystemPromptSpec` / `Tools` / `GuardrailsSpec` are silently ignored. |
| no | yes | **Declarative path.** Translator builds options from manifest fields. |
| no | no | `501 urn:vais-agents:handler-not-loaded` at invoke. Either publish a plugin that exports this handler, or switch the manifest to the declarative path with a `Model`. |

Convention: set `handler.typeName: declarative` for pure-YAML agents — no plugin ever registers that sentinel. Plugin authors pick namespaced values like `MyApp.WeatherAgent` to avoid collision. See [runtime-plugins concept](runtime-plugins.md) for plugin authoring + loader semantics.

## Registering custom factories

The three built-ins register via the runtime host's composition root:

```csharp
services.AddAgentManifestInstantiator();   // translator + provider pool
services.AddBuiltinModelProviders();       // openai + anthropic + azure-openai
services.AddBuiltinGuardrails();           // LengthCap + 4 regex factories + LlmAsJudge
services.ConfigureAgentGrains((sp, id) =>
    sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));
```

Consumers add their own factories alongside. The translator dispatches by name (model `Provider` string, guardrail `Name + Layer` pair); duplicate-name registration trips a constructor guard.

## Failure URNs

| URN | Cause |
|---|---|
| `agent-not-found` | Registry has no manifest for the id. |
| `handler-not-loaded` | Manifest has no `Model` and no loaded plugin exports the requested `Handler.TypeName`. Ship a plugin or add a `Model` block. |
| `plugin-factory-throw` | A v0.18 plugin factory threw inside `CreateAsync` during grain activation. Inner exception surfaces via Problem Details. |
| `handler-and-declarative-fields-both-set` | Apply-time WARN (not error) — manifest has both a plugin-matching `Handler.TypeName` AND declarative `Model` fields. Plugin wins; declarative fields are silently ignored. |
| `model-provider-unsupported` | `ModelSpec.Provider` doesn't match any registered `IModelProviderFactory`. |
| `tool-source-unknown` | `ToolRef.Source` prefix is not `static:` / `mcp:` / `a2a:` / `agent:` (also raised for background-mode `agent:` when `IBackgroundAgentTracker` is not registered). |
| `tool-not-registered` | `static:<name>` has no matching `IStaticToolRegistry` entry. |
| `mcp-server-not-declared` | `mcp:<name>` references an undeclared `McpServers[].Name`. |
| `a2a-agent-not-declared` | `a2a:<name>` references an undeclared `A2ARemoteAgents[].Name`. |
| `local-agent-not-declared` | `agent:<name>` references an undeclared `LocalAgents[].Name`. |
| `local-agent-target-not-found` | `agent:<name>` resolved a declared `LocalAgentRef`, but the target `agentId` (`/version`) is not in the registry at translate time. |
| `guardrail-not-registered` | `GuardrailRef.Name` has no matching `IGuardrailFactory` for the layer. |
| `guardrail-params-invalid` | Factory rejected the supplied params (missing key, wrong type, bad value). |
| `prompt-template-not-registered` | `SystemPromptSpec.TemplateRef` doesn't resolve. |
| `prompt-file-unreadable` | `SystemPromptSpec.FileRef` missing / permissioned / outside root. |
| `prompt-spec-ambiguous` | More than one of Inline / TemplateRef / FileRef set. |

All surface as HTTP Problem Details when `vais apply` / `vais invoke` hits them.

## Related

- [author-an-agent-in-yaml guide](../guides/author-an-agent-in-yaml.md) — end-to-end walkthrough.
- [runtime-plugins concept](runtime-plugins.md) — the C# plugin escape hatch for manifests that need behaviour beyond the declarative shape.
- [package-an-agent-as-a-plugin guide](../guides/package-an-agent-as-a-plugin.md) — end-to-end walkthrough for the plugin path.
- [ship-a-guardrail guide](../guides/ship-a-guardrail.md) — custom `IGuardrailFactory`.
- [configure-llm-providers devops guide](../devops/configure-llm-providers.md) — credential wiring + custom-endpoint patterns + Fallback pools, with a note on registering a custom `IModelProviderFactory`.
- [manifest-schema reference](../reference/manifest-schema.md) — canonical field-by-field catalog.
- [control-plane concept](control-plane.md) — `IAgentLifecycleManager` verbs.
- [execution-loop concept](execution-loop.md) — what `StatefulAiAgent` actually does with the translated options.
