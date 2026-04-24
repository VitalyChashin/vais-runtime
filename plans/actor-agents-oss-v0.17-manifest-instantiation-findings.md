# v0.17 Manifest-driven agent instantiation — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.17-manifest-instantiation-spike.md`](./actor-agents-oss-v0.17-manifest-instantiation-spike.md). Answers Q1–Q10 plus the flagged open items at the bottom. Landing verdict below.

Created 2026-04-21. **Status**: complete. User confirmed the spike's leans on 2026-04-21 (verbatim: "leans are ok"). This doc locks each answer with evidence; the pillar plan then turns decisions into PRs.

---

## Q1 — Where does the instantiator code live?

### Evidence

- `Vais.Agents.Core` already hosts stack-neutral defaults (`StatefulAiAgent`, `NoopHistoryReducer`, `InMemoryAgentRegistry`, `AsyncLocalAgentContextAccessor`, `InProcessGraphOrchestrator`). No SDK deps — that's the boundary.
- The instantiator needs `OpenAIClient` / `Anthropic.SDK` / `Azure.AI.OpenAI` + MEAI bridges. Adding those to `Core` would violate the no-deep-deps discipline and drag ~20 MB of SDKs into every consumer who referenced `Core`.
- `Vais.Agents.Runtime.Host` is `IsPackable=false` by design (Pillar A precedent, matches `Vais.Agents.Control.KubernetesOperator.Host`). Anyone building a custom host must be able to reuse the instantiator, which means it has to be library-layer.
- The instantiator is also a dependency Pillar D will need when the graph lifecycle manager per-node-instantiates agents. Ship-first-as-lib is cheaper than lift-later.

### Decision (Q1): **New library package `Vais.Agents.Runtime.Instantiation`**

`src/Vais.Agents.Runtime.Instantiation/` — `Microsoft.NET.Sdk`, `IsPackable=true`, PublicAPI analyzer on, `Directory.Build.props` inherits the warnings-as-errors + docs generation. No Orleans dependency — translator is runtime-host-agnostic.

---

## Q2 — Where does the manifest→StatefulAgentOptions translator plug in?

### Evidence

- `ConfigureAgentGrains(IServiceCollection, Func<IServiceProvider, string, StatefulAgentOptions>?)` is already the grain-activation seam (`AgenticHostingOrleansServiceCollectionExtensions.cs:75–86`). v0.16's composition root calls the no-arg overload, so the default factory emits a plain options record.
- Making the manifest translator the *default* of `ConfigureAgentGrains` would force `Vais.Agents.Hosting.Orleans` to reference `Vais.Agents.Runtime.Instantiation`, which transitively pulls the SDKs into the Orleans hosting package. Layering violation.
- The cleanest split: Instantiation package exposes `AddAgentManifestInstantiator(IServiceCollection)` which registers the translator + secret resolver + factory registries. Runtime host calls both it and `ConfigureAgentGrains(sp, translator.TranslateForGrain)`.

### Decision (Q2): **`AddAgentManifestInstantiator()` DI extension + explicit `ConfigureAgentGrains` wiring in `Runtime.Host`**

```csharp
// In CompositionRoot.ConfigureServices(IServiceCollection, RuntimeOptions):
services.AddAgentManifestInstantiator();              // Registers translator + factories
services.ConfigureAgentGrains((sp, id) =>
    sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));
```

The Instantiation package stays reusable; the Orleans hosting package stays SDK-free; the runtime host assembles the pieces.

---

## Q3 — Manifest lookup at grain activation: blocking?

### Evidence

- Orleans grain `OnActivateAsync` is `async Task` — registry lookup can `await` without blocking the silo scheduler.
- Measured registry lookup cost in Pillar A's clustered-mode integration smoke: `OrleansAgentRegistry.GetAsync(id)` is a single grain call (~0.5–2 ms in-silo; ~1–5 ms cross-silo). Eager translation at `apply` time saves one read per cold activation — not meaningful.
- Caching options inside the translator (single-flight + TTL) handles repeated activations for hot agents without round-tripping the registry. Cache invalidation on `UpdateAsync` (Q9) keeps it honest.

### Decision (Q3): **Lazy registry lookup inside the translator, with a small per-id `ConcurrentDictionary<string, StatefulAgentOptions>` cache**

No TTL in v0.17 — cache lives for the translator's lifetime. Explicit eviction on `UpdateAsync` / `EvictAsync` through a `InvalidateAsync(string id)` method on `IAgentManifestTranslator`. If cold-start profiles regress in clustered mode with many unique agent ids, revisit with LRU + TTL in a follow-up.

---

## Q4 — Model-provider factory contract

### Evidence

- Survey confirms `ICompletionProvider` is the single seam both SK and MAF land under. The runtime doesn't care which stack built the provider.
- Per-provider factory with DI-keyed lookup is the standard .NET 8+ pattern. Anthropic needs different SDK wiring from OpenAI needs different wiring from AzureOpenAI — three factories, one contract.
- Per-agent stack selection (`ModelSpec.Stack = "sk"` or `"maf"`) is scope creep; partners in Pillar A didn't ask for it.

### Decision (Q4): **`IModelProviderFactory` keyed by provider string; one factory per provider at launch**

```csharp
public interface IModelProviderFactory
{
    string Provider { get; }     // "openai" | "anthropic" | "azure-openai"
    ValueTask<ICompletionProvider> CreateAsync(
        ModelSpec spec,
        ISecretResolver secrets,
        CancellationToken cancellationToken);
}
```

Registered via `services.AddBuiltinModelProviders()` which registers all three. Translator resolves `IEnumerable<IModelProviderFactory>` and picks by `Provider` matching `ModelSpec.Provider` (case-insensitive). Unknown provider → `ManifestInstantiationException` with `urn:vais-agents:model-provider-unsupported`.

---

## Q5 — Backend SDK path per provider

### Evidence

- MEAI 10.5 ships `IChatClient` as the unifying abstraction. `OpenAI`, `Anthropic.SDK`, and `Azure.AI.OpenAI` all have `AsChatClient` / `AsIChatClient` bridges to MEAI.
- `MafCompletionProvider` (survey: lines 45–79) is MEAI-native — takes `IChatClient` directly. No Kernel dance.
- SK's native Anthropic connector (1.74's `Microsoft.SemanticKernel.Connectors.Anthropic`) is more mature than Anthropic.SDK + MEAI bridge, but running two SDK stacks for Anthropic adds maintenance surface without user-facing value.
- The `Vais.Agents.Ai.SemanticKernel` package stays in the library — consumers who already own a `Kernel` inject it + build `SkCompletionProvider` themselves. The runtime's built-in factories don't use SK.

### Decision (Q5): **MEAI `IChatClient` path for all three built-in factories**

- **`OpenAIModelProviderFactory`** — `new OpenAIClient(apiKey).GetChatClient(modelId).AsIChatClient()` → `MafCompletionProvider`.
- **`AnthropicModelProviderFactory`** — `new AnthropicClient(apiKey).AsIChatClient(modelId)` via `Anthropic.SDK.Messaging` 5.x (+ MEAI bridge) → `MafCompletionProvider`.
- **`AzureOpenAIModelProviderFactory`** — `new AzureOpenAIClient(endpoint, apiKeyCredential).GetChatClient(deploymentName).AsIChatClient()` → `MafCompletionProvider`.

Consumer opt-out: custom `IModelProviderFactory` with `Provider = "openai-sk"` (or any non-default string) + matching `ModelSpec.Provider` on their manifests. Documented in `docs/guides/ship-a-custom-model-provider.md` (new for v0.17).

**Package pins (add to `Directory.Packages.props`):**
- `Anthropic.SDK` — latest stable (track 5.x).
- `Azure.AI.OpenAI` — latest stable (2.3.x+, pairs with MEAI 10.5).
- `OpenAI` — already pinned at 2.10.0.

**Azure AI Inference subquestion.** `Microsoft.Extensions.AI` 10.5 ships a generic OpenAI-compatible client. `Azure.AI.OpenAI` remains the stable path for Azure-hosted OpenAI deployments. Pick `Azure.AI.OpenAI` for the v0.17 factory; revisit Azure-AI-Inference if partners deploy non-OpenAI Azure Foundry models.

---

## Q6 — `static:<name>` tool resolution

### Evidence

- Keyed DI (`services.AddKeyedSingleton<ITool>(name, impl)`) works but demands partners know the pattern. Discoverability is low.
- A dedicated `IStaticToolRegistry` with `Register(string name, Func<IServiceProvider, ITool> factory)` has one type to reason about, is trivially testable, and mirrors the shape of `IToolRegistry` (which partners already know).
- Keyed DI is still available for partners who want it — the `IStaticToolRegistry` can itself look up keyed services as a second-chance path. But not in v0.17; keep the seam minimal.

### Decision (Q6): **`IStaticToolRegistry` with factory-based registration**

```csharp
public interface IStaticToolRegistry
{
    ITool? Get(string name, IServiceProvider sp);
}

public interface IStaticToolRegistryBuilder
{
    IStaticToolRegistryBuilder Add(string name, Func<IServiceProvider, ITool> factory);
}

// Registration:
services.AddStaticToolRegistry(b => b
    .Add("weather", sp => new WeatherTool(sp.GetRequiredService<IHttpClientFactory>()))
    .Add("currency", sp => new CurrencyTool()));
```

`ToolRef.Source = "static:weather"` → translator calls `registry.Get("weather", sp)`. Missing name → `ManifestInstantiationException` with `urn:vais-agents:tool-not-registered`.

---

## Q7 — MCP + A2A tool-ref resolution + manifest extension for A2A

### Evidence

- `McpToolSource` (Protocols.Mcp) already handles named MCP servers with connection pooling — v0.7 shipped this.
- `AgentManifest.McpServers: IReadOnlyList<McpServerRef>` exists today; `ToolRef.Source = "mcp:weather-server"` references a `McpServers[].name`. Translator lazy-builds an `McpToolSource` per declared server, caches per translator scope.
- `A2ARemoteAgentTool` exists (Protocols.A2A) and wraps a remote A2A agent as `ITool`. Manifest has no equivalent `A2ARemoteAgents` collection — `ToolRef.Source = "a2a:foo"` has no place to look up `foo`'s URL today.
- Adding the collection is additive and matches the shape of `McpServers`.

### Decision (Q7): **Lazy per-server MCP source caching + new `AgentManifest.A2ARemoteAgents` section**

Manifest extension (in `Vais.Agents.Abstractions`):

```csharp
public sealed record A2ARemoteAgentRef(
    string Name,
    Uri Url,
    string? AuthRef = null,              // Secret URI for bearer token (optional)
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentManifest(...)
{
    // ...existing fields...
    public IReadOnlyList<A2ARemoteAgentRef>? A2ARemoteAgents { get; init; }
}
```

PublicAPI move: `A2ARemoteAgentRef` + the new `AgentManifest.A2ARemoteAgents` property land in `PublicAPI.Unshipped.txt` at PR 3 time, promoted to `.Shipped.txt` at tag time.

Translator behaviour:
- `mcp:<name>` → find `McpServerRef` with matching `Name`; lazy-construct `McpToolSource`; merge discovered tools into agent's `IToolRegistry`.
- `a2a:<name>` → find `A2ARemoteAgentRef` with matching `Name`; lazy-construct `A2ARemoteAgentTool` (one `ITool` per remote agent).
- Missing name → `ManifestInstantiationException` with `urn:vais-agents:mcp-server-not-declared` / `urn:vais-agents:a2a-agent-not-declared`.

---

## Q8 — Guardrail built-ins: which 4 + where?

### Evidence

- `Core` already hosts `NoopHistoryReducer`, `InMemoryMemoryStore`, `NullMemoryStore.Instance` — stack-neutral agent-layer defaults. Guardrail impls fit the same mould.
- A separate `Vais.Agents.Guardrails.Builtin` package for 4 small classes + 4 factories is overkill — the impls have no deps `Core` doesn't already carry.
- LLM-as-judge needs its own `ICompletionProvider`. Cleanest wiring: guardrail params include a nested `ModelSpec`; factory resolves it via the same `IModelProviderFactory` registry.

### Decision (Q8): **Four guardrail built-ins in `Core`; `IGuardrailFactory` DI-by-name keyed on the factory name**

Four impls under `Vais.Agents.Core.Guardrails`:

- `LengthCapInputGuardrail(int maxChars)` — rejects with `GuardrailOutcome.Deny` when `request.Input.Length > maxChars`.
- `RegexAllowlistInputGuardrail(Regex pattern)` / `RegexAllowlistOutputGuardrail(Regex pattern)` — deny on no-match.
- `RegexDenylistInputGuardrail(Regex pattern)` / `RegexDenylistOutputGuardrail(Regex pattern)` — deny on match.
- `LlmAsJudgeOutputGuardrail(ICompletionProvider judge, string judgePrompt, double minScore)` — run judge, parse score from response, deny if below threshold.

Factory contract:

```csharp
public interface IGuardrailFactory
{
    string Name { get; }                             // "LengthCap", "RegexAllowlist", ...
    GuardrailLayer Layer { get; }                    // Input | Output | Tool
    object Create(IReadOnlyDictionary<string, JsonElement> parameters, IServiceProvider sp);
}
```

Registration: `services.AddBuiltinGuardrails()` registers 5 factories (one per name above — the two regex kinds each register as both Input and Output factories, different `Name` vs. `Layer` combinations keyed on `(Name, Layer)`).

Manifest resolution:

```yaml
guardrails:
  input:
    - name: LengthCap
      params: { maxChars: 2000 }
    - name: RegexDenylist
      params: { pattern: "(?i)credit-?card" }
  output:
    - name: LlmAsJudge
      params:
        minScore: 0.7
        judgePrompt: "Is this response helpful? Score 0.0-1.0."
        judgeModel:
          provider: openai
          id: gpt-4o-mini
          apiKeyRef: "secret://env/OPENAI_API_KEY"
```

**Guardrail evaluation order.** Declaration order in the manifest. Documented in `docs/concepts/declarative-agents.md`.

**LLM-as-judge provider pooling.** Add `ICompletionProviderPool` (new in `Vais.Agents.Runtime.Instantiation`) that memoises providers by `ModelSpec.GetHashCode()` — multiple judges against the same model share a provider. Not premature optimisation; judges get called per turn.

---

## Q9 — Cache invalidation on `UpdateAsync`

### Evidence

- `InMemoryAgentRuntime.GetOrCreate` caches `IAiAgent` per id (survey: lines 55–59). `IAgentRuntime.Remove(id)` evicts. Already in the contract.
- Partner mental model: "apply new manifest → next invoke uses it." Updating in-flight invocations is undefined behaviour and surfaces as subtle bugs.
- Orleans grain reactivation on a fresh `GetGrain` call happens automatically after eviction + a quiet period — no special-case needed.

### Decision (Q9): **`UpdateAsync` evicts from `IAgentRuntime` and translator's options cache; in-flight runs untouched; next invoke reactivates with new manifest**

```csharp
// In AgentLifecycleManager.UpdateAsync (new in v0.17):
public async ValueTask<AgentHandle> UpdateAsync(
    AgentHandle handle, AgentManifest newManifest, CancellationToken ct)
{
    // ... policy + audit + validation ...
    await _registry.ReplaceAsync(newManifest, ct);
    _runtime.Remove(handle.Id);
    _translator.InvalidateAsync(handle.Id);   // new in v0.17
    return new AgentHandle(newManifest.Id, newManifest.Version, InstanceId: null);
}
```

Documented in `docs/concepts/declarative-agents.md`: "Updates take effect on next invoke. In-flight runs continue with the manifest they started with."

---

## Q10 — `handler.typeName` coexistence with Pillar C

### Evidence

- Manifest already has `Handler.TypeName` + optional `AssemblyName` for code-authored agents (Pillar C's v0.18 plugin seam).
- Four combinations in the spike's table. v0.17 only covers declarative fields; `typeName` without declarative fields yields 501 until Pillar C.
- Both-set is a user transition state. Silent precedence is user-hostile; WARN at apply time gives partners a chance to clean up.

### Decision (Q10): **`handler.typeName` wins when both set; WARN at apply time; `501 urn:vais-agents:handler-not-loaded` until Pillar C**

| `Handler.TypeName` | `Model + SystemPrompt` | v0.17 behaviour |
|---|---|---|
| null | present | Full declarative path. ✅ |
| set | null | `apply` succeeds (manifest valid); `invoke` returns `501 urn:vais-agents:handler-not-loaded` with message pointing at Pillar C. |
| set | present | `apply` succeeds with WARN `handler-and-declarative-fields-both-set`; invoke returns the same `501 urn:vais-agents:handler-not-loaded` (declarative fields ignored). |
| null | null | `400 urn:vais-agents:manifest-invalid` — validator rejects. |

WARN surfaces via:
- Apply-time response body carries a `warnings` array (new in `AgentManifestApplyResponse`).
- Runtime audit log `AgentManifestWarning` entry.
- CLI's `vais apply -f` prints each warning prefixed with `warn:` + continues with exit 0.

---

## Open items resolved

### A2A manifest section

Locked in Q7. `A2ARemoteAgents: IReadOnlyList<A2ARemoteAgentRef>` — additive field, PublicAPI unshipped→shipped move at PR 3.

### Guardrail ordering

Locked in Q8. Declaration order. Document.

### LLM-as-judge provider sharing

Locked in Q8. `ICompletionProviderPool` memoises by `ModelSpec` hash.

### Manifest-validator gaps

Pillar B's translator does the following apply-time validation in addition to the v0.6 syntactic checks:

- `ModelSpec.Provider` is a registered `IModelProviderFactory` key.
- `ModelSpec.ApiKeyRef` format matches `secret://scheme/path` (actual resolution still happens at invoke time — otherwise apply would need secret access).
- Every `ToolRef` source prefix is one of `static:` / `mcp:` / `a2a:`.
- Every `static:<name>` resolves in `IStaticToolRegistry` (if registry is registered; absent is fine — registry is optional).
- Every `mcp:<name>` references a `McpServers[].Name`.
- Every `a2a:<name>` references an `A2ARemoteAgents[].Name`.
- Every `GuardrailRef.Name` has a registered `IGuardrailFactory` for the matching `Layer`.

Failures surface as `400 urn:vais-agents:manifest-invalid` with a structured `details` field enumerating each problem.

### ConfigureAgentGrains ordering

The translator must be registered before `ConfigureAgentGrains(sp, translator.Translate)` runs (the lambda captures `sp.GetRequiredService<IAgentManifestTranslator>()`, deferred to silo-activation time). Runtime.Host's `CompositionRoot.ConfigureServices` calls `AddAgentManifestInstantiator()` before `ConfigureAgentGrains` — same ordering discipline as Pillar A's durability sidecars.

Lock with a unit test: `Composition_Translator_Registered_Before_ConfigureAgentGrains` — asserts `IAgentManifestTranslator` resolves after full composition + the `Func<string, StatefulAgentOptions>` yields a translator-backed options instance (not the plain default).

### Azure AI Inference vs Azure.AI.OpenAI

Covered in Q5. `Azure.AI.OpenAI` for v0.17; revisit Azure-AI-Inference when a partner asks for non-OpenAI Azure Foundry model support.

---

## Landing verdict

All ten blocking questions and five open items resolved with evidence. No new open questions surfaced during findings.

**Scope for v0.17.0-preview is now frozen:**

1. Manifest translator + 3 model-provider factories (OpenAI / Anthropic / Azure-OpenAI) + 4 guardrail built-ins.
2. Static tool registry + `mcp:*` / `a2a:*` lazy resolution.
3. Runtime host composes the above; `ConfigureAgentGrains` wired through.
4. `UpdateAsync` eviction contract.
5. `handler.typeName` early-out with a 501 pointer at Pillar C.

**Out of scope explicitly:**

- `ContextProviders`, `Handoffs`, `Reasoning`, `Observability` manifest fields (contracts exist; runtime ignores).
- Fourth+ model provider (Anthropic-via-Bedrock, Google Gemini, etc.).
- Plugin DLL loading (Pillar C).
- Graph-level manifest (Pillar D).
- Image signing / SBOM / NetworkPolicy / HPA (Pillar F).

**Ready for pillar plan.** Spike → findings cycle complete; next doc is `plans/actor-agents-oss-v0.17-manifest-instantiation-pillar.md` — locks the 4-PR sequence with per-PR checklists, acceptance criteria, and timeline.
