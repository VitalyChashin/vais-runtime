# v0.17.0-preview — Manifest-driven agent instantiation (Pillar B) — spike

Open-questions research doc for [Phase 3 Pillar B](./actor-agents-oss-phase-3-runtime-productisation.md#pillar-b--declarative-agent-instantiation-us-4-enables-us-2). Answers partner user-stories US-4 (create an agent declaratively in YAML and deploy it) and enables US-2 (create an agent by code, deploy via manifest — kicks in once Pillar C's plugin loader lands).

Written 2026-04-21. Precedes the findings + pillar plan of the same name.

---

## What Pillar A shipped, what's still 501

Pillar A (v0.16) stood up the runtime container and the full HTTP control plane. `vais apply -f agent.yaml` persists the manifest into the Orleans-backed `IAgentRegistry`; `vais get agents` round-trips; `/openapi/v1.json` advertises every verb. But `vais invoke weather --text "hi"` returns `501 urn:vais-agents:agent-not-instantiable` because **the runtime has no pipeline from a stored `AgentManifest` to a running `IAiAgent`**.

The survey pins the gap at ten concrete steps — stored manifest → live agent. Steps 1–3, 5, 9–10 work today. Steps 4, 6, 7, 8 are missing:

| # | Step | Today | Pillar B need |
|---|---|---|---|
| 1 | Manifest persisted on `apply` | ✅ Orleans registry | — |
| 2 | Secret resolution primitives | ✅ env + file + composite | Wire into model-provider factories |
| 3 | Model-provider SDK construction | ⚠️ Consumer hand-builds Kernel / IChatClient | Auto factory per provider |
| 4 | `ICompletionProvider` construction | ❌ No factory | **Build it** |
| 5 | System prompt compose | ✅ `ISystemPromptComposer` | Wire the three `SystemPromptSpec` shapes |
| 6 | Tool registry assemble | ❌ Consumer direct-instantiates | **Build `ToolRef`-string resolver + MCP/A2A wiring** |
| 7 | Guardrail resolution | ❌ Contracts only, no impls | **Ship 4 built-ins + DI-by-name lookup** |
| 8 | `StatefulAgentOptions` build for an id | ❌ `ConfigureAgentGrains` callback is a no-op in Pillar A's host | **Wire the manifest→options translator here** |
| 9 | Grain activation calls factory | ✅ `AiAgentGrain.OnActivateAsync` | — |
| 10 | `invoke` hits a running agent | ❌ `IAgentLifecycleManager.InvokeAsync` throws `AgentHandleNotFound` if nothing pre-registered | **Auto-resolve + invoke grain when registry has the manifest** |

Survey file paths + line numbers captured in the companion research summary under §Background in the coming findings doc.

---

## Scope fence — what ships in v0.17 vs. later Bs / Pillar C

**v0.17 ships** the pure-LLM-plus-tools-plus-guardrails path. That's the 80% shape partners asked for in US-4 and it's what the master plan's acceptance YAML shows. Specifically:

- Model resolution for **OpenAI + Anthropic + AzureOpenAI** (3 providers at launch).
- Tool refs — `static:<name>` (DI-registered `ITool` instances by name) + `mcp:<server>` (pulls from declared `McpServers`) + `a2a:<agent>` (wraps a declared A2A peer).
- SystemPrompt — all three `SystemPromptSpec` shapes (inline / templateRef / fileRef).
- Guardrails — ship **length cap + regex allowlist + regex denylist + LLM-as-judge** as built-ins; DI-by-name for consumer impls.
- Budget — thread `RunBudget` straight through (already wired in `StatefulAgentOptions`).
- Secret resolution — reuse the shipped `CompositeSecretResolver` (env + file cover K8s projected Secrets).

**Defers to v0.17.x or Pillar C:**

- `handler.typeName` (code-authored agent plugins) — Pillar C (v0.18). If a manifest sets `handler.typeName`, v0.17 returns `501 urn:vais-agents:handler-not-loaded` with a pointer at Pillar C.
- `ContextProviders`, `Handoffs`, `Reasoning`, `Observability` — fields exist on the manifest; v0.17 ignores them (documented). Revisit after partner feedback.
- `OutputSchema` — thread through to the provider when supported; don't ship schema-validation wrappers in v0.17 (the provider's structured-output mode does the enforcement).
- Non-SK/MAF backends for Anthropic / Azure — both SK and MAF support OpenAI-shape endpoints; Anthropic-over-SK uses SK's Anthropic connector; AzureOpenAI uses SK's Azure connector. Native Anthropic SDK (Claude via `Anthropic.SDK`) is a later drop if partners need it.
- Custom `ICompletionProvider` impls — the factory dispatches by `ModelSpec.Provider` string; if none match, 501.

**Not in Pillar B at all** (Pillar C, D, E, F):

- Plugin DLL loading → Pillar C.
- Graph as first-class deployable → Pillar D.
- Cross-runtime agent refs → Pillar E.
- Polish (image signing, HPA, NetworkPolicy) → Pillar F.

---

## Blocking questions (10)

### Q1 — Where does the instantiator code live?

**Context.** Master plan proposes a new `Vais.Agents.Runtime.Instantiation` package. Survey confirms no existing package owns this. Alternatives:

- **A. New package `Vais.Agents.Runtime.Instantiation`** — matches master plan; sibling to `Runtime.Host`. Library-layer (`IsPackable=true`) so custom hosts can reuse.
- **B. Extend `Vais.Agents.Core`** — the in-process graph orchestrator already lives in `Core`; add the instantiator alongside.
- **C. Extend `Runtime.Host` (non-packable)** — the only caller in v0.17 is the runtime container itself; keep it private.

**Lean: A.** The instantiator needs to be library-consumable so (a) partners building custom hosts can wire it, (b) the Pillar D graph lifecycle manager (v0.19) composes it per-node, (c) integration tests can drive it without Orleans. Core has a no-deep-deps discipline; adding model-provider SDKs would bloat it. Runtime.Host is private-by-design.

### Q2 — Where does the manifest→StatefulAgentOptions translator plug in?

**Context.** `ConfigureAgentGrains` takes a `Func<IServiceProvider, string, StatefulAgentOptions>` callback. v0.16's composition root calls it with no argument, so the default factory emits a plain `new StatefulAgentOptions { AgentName = id }`. Pillar B needs to replace that callback.

- **A. Swap the callback in `Runtime.Host`'s composition root** — call `ConfigureAgentGrains(sp, id => ManifestTranslator.Translate(sp, id))`. The translator resolves `IAgentRegistry.GetAsync(id)` inside.
- **B. Make the translator the default** — wire `ConfigureAgentGrains()` (no arg) to call the translator automatically in Orleans hosting. Requires DI-registered services the translator depends on, which forces `Hosting.Orleans` to reference the new Instantiation package — violates the layering.
- **C. Dedicated new DI extension** — `services.AddAgentManifestInstantiator()` registers the translator + overrides `ConfigureAgentGrains`'s default Func.

**Lean: A + C.** The Instantiation package exposes `AddAgentManifestInstantiator(IServiceCollection)` which registers the translator **and** a `Func<IServiceProvider, string, StatefulAgentOptions>` that calls it. `Runtime.Host` calls both `ConfigureAgentGrains(sp, translator.Translate)` **and** `AddAgentManifestInstantiator()`. The `Hosting.Orleans` package stays stack-neutral. Consumers building custom hosts opt in by calling `AddAgentManifestInstantiator`.

### Q3 — Manifest lookup at grain activation: blocking?

**Context.** Grain activation is synchronous from the Orleans runtime's perspective. `OnActivateAsync` IS async, but blocking on `IAgentRegistry.GetAsync(id)` during activation adds a round-trip per cold start.

- **A. Block on registry lookup in `OnActivateAsync`.** Simple. Cold activation adds one Redis read (~1-2 ms clustered; free in localhost).
- **B. Eager-translate at `apply` time.** Turn the manifest into `StatefulAgentOptions` when `CreateAsync` lands, cache the options against the agent id in a `IManifestOptionsCache`. Grain activation pulls from cache.
- **C. Hybrid — lazy with a short-lived cache in the translator** (single-flight + small TTL).

**Lean: A with a tiny in-memory cache inside the translator.** The registry lookup cost is negligible (Orleans grain calls are typed RPC; Redis is ~1 ms). Eager translation (B) means apply-time errors surface for config bugs — good — but also means updates are harder: on `UpdateAsync` we'd need to invalidate. Cache-inside-translator (C-lite) handles repeated activations fine. Revisit if cold-start profile regresses.

### Q4 — Model-provider factory contract

**Context.** Need a pluggable seam: `ModelSpec.Provider = "openai"` → `ICompletionProvider`. Survey shows SK's `SkCompletionProvider(Kernel)` and MAF's `MafCompletionProvider(IChatClient)` are the two concrete impls; neither does credential/model-id resolution.

**Shape:**

```csharp
public interface IModelProviderFactory
{
    string Provider { get; }           // "openai", "anthropic", "azure-openai", ...
    ValueTask<ICompletionProvider> CreateAsync(ModelSpec spec, ISecretResolver secrets, CancellationToken ct);
}
```

Registered as DI-keyed singletons; translator looks up by `spec.Provider`. Ships three: `OpenAIModelProviderFactory` (SK or MAF under the hood — see Q5), `AnthropicModelProviderFactory`, `AzureOpenAIModelProviderFactory`.

- **A. Single registration per provider** — each factory picks SK or MAF internally.
- **B. Two factories per provider** — `OpenAI/Sk`, `OpenAI/Maf`. Manifest picks via a new `ModelSpec.Stack` field.

**Lean: A.** Partners don't want to pick SK-vs-MAF per agent; the runtime picks based on what's easier to wire today (OpenAI → MAF via MEAI IChatClient for simplicity; Anthropic → SK via its Anthropic connector; Azure → MAF via Azure.AI.OpenAI). Let the factory-author pick; document the internal choice; future-proof with an optional `Stack` hint.

### Q5 — Backend SDK per provider

**Context.** `OpenAIModelProviderFactory` must construct either a SK `Kernel` or an MEAI `IChatClient`. Both land under `ICompletionProvider`; picking one ties the instantiator to a SDK dependency.

- **A. MEAI/MAF path for all three** — `new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient()` → `MafCompletionProvider`. Anthropic via `new AnthropicClient(apiKey).AsIChatClient()` (Anthropic.SDK + AsChatClient bridge). Azure via `new AzureOpenAIClient(endpoint, credential).GetChatClient(deployment).AsIChatClient()`.
- **B. SK path** — `Kernel.CreateBuilder().AddOpenAIChatCompletion(apiKey, model).Build()` → `SkCompletionProvider`. SK has first-class Anthropic + Azure connectors.
- **C. Mixed** — SK for Anthropic (better connector maturity at 1.74); MAF for OpenAI + Azure (simpler).

**Lean: A.** MEAI is the unifying abstraction both stacks converge on. Going MAF-first minimises the number of SDKs the runtime pulls in and matches the architecture.md framing ("MEAI is the contract that binds SK and MAF"). The Ai.SemanticKernel adapter stays relevant for consumers who already live in SK-land and inject their own `Kernel`. Partner says "use SK" in a manifest? Document how a consumer hosts a custom factory that hands back `SkCompletionProvider` — easy opt-out.

### Q6 — Tool-ref resolution: `static:<name>` semantics

**Context.** `ToolRef.Source = "static:weather-tool"` — where does `weather-tool` come from?

- **A. DI-keyed `ITool` singletons.** Consumer registers `services.AddKeyedSingleton<ITool>("weather-tool", new WeatherTool())`. Translator does `sp.GetKeyedService<ITool>("weather-tool")`. Standard .NET keyed-DI pattern.
- **B. Dedicated `IStaticToolRegistry`** — a string-to-ITool dictionary registered once; resolver looks up by name. Simpler model, one type to reason about.
- **C. Both** — DI-keyed first, fall through to static registry.

**Lean: B.** Keyed DI is well-supported in .NET 8+ but partners find it unfamiliar. A plain `IStaticToolRegistry` is discoverable, testable, and matches the shape of `IToolRegistry` they already know. Registration pattern: `services.AddStaticTool("weather-tool", sp => new WeatherTool())`. The Pillar B package ships this, the runtime host doesn't register any static tools by default (consumers who want them override the host).

### Q7 — MCP + A2A server declaration consumption

**Context.** `AgentManifest.McpServers` lists named servers with transport + URL + auth. `ToolRef.Source = "mcp:weather-server"` pulls from `weather-server`.

- **A. Translator instantiates `McpToolSource` per declared server on demand.** Caches per-server instance inside the translator (connections pool).
- **B. Translator registers all declared servers eagerly at apply time** and keeps them live. Higher baseline resource use.

**Lean: A.** Lazy wiring matches the agent-level caching from Q3; connections open on first invoke. The `McpToolSource` already supports connection pooling + discover-once semantics.

Mirror story for `a2a:<agent>` — wraps a declared `A2ARemoteAgentConfig` as `A2ARemoteAgentTool`. Survey shows `A2ARemoteAgentTool` exists; manifest lacks a first-class `A2ARemoteAgents` list today. **Subquestion:** extend manifest with `A2ARemoteAgents: [{name, url, authRef}]` section? Or stuff them under `McpServers` with a different transport kind?

**Lean subanswer:** extend manifest with a new `A2ARemoteAgents` section alongside `McpServers`. Additive; PublicAPI unshipped→shipped edit per the analyzer discipline.

### Q8 — Guardrails: which 4 ship + where do they live?

**Context.** Contracts exist in `Abstractions`; no impls ship today. Master plan lists length cap + regex allowlist + regex denylist + LLM-as-judge.

- **A. New package `Vais.Agents.Guardrails.Builtin`** — four impls + DI extension `AddBuiltinGuardrails()`.
- **B. Drop them inside `Core`** — `Core` is stack-neutral and already hosts defaults like `NoopHistoryReducer`; four small impls fit.
- **C. Inside the new `Runtime.Instantiation`** — only useful if the manifest-translator is the caller.

**Lean: B.** Guardrails are agent-layer defaults; `Core` is the right home, matches the shape of `NoopHistoryReducer`, `InMemoryAgentRegistry`. The Instantiation package imports them by name.

Resolution pattern: `GuardrailRef.Name = "LengthCap"` + `params: { maxChars: 2000 }` → translator looks up `IGuardrailFactory` keyed on `"LengthCap"` in DI, calls `factory.Create(params)`. Ships four factories registered automatically by `AddBuiltinGuardrails()`; consumers add their own via `services.AddGuardrail("Custom", sp => new CustomGuardrail(...))`.

The **LLM-as-judge** guardrail needs its own `ICompletionProvider` — how does it get one? Option: use a dedicated `ModelSpec` in the guardrail's `params` (e.g., `params: { judgeModel: { provider: openai, id: gpt-4o-mini } }`); the factory recursively invokes the model-provider factory to build its own provider. Subquestion: single-shared judge provider cached in DI, or per-guardrail? **Lean:** per-guardrail with a `ICompletionProviderPool` keyed on `ModelSpec` hash to avoid duplicate allocations.

### Q9 — Cache invalidation on UpdateAsync

**Context.** Partner runs `vais apply -f weather.yaml` with a new `SystemPrompt.Inline`. The in-flight grain still holds the old options. What happens?

- **A. Do nothing.** Next grain activation (after eviction / silo restart / TTL) picks up new options. Partners restart or wait.
- **B. Evict the grain.** `UpdateAsync` calls `runtime.Remove(id)` — invalidates the local cached `IAiAgent` + triggers Orleans grain deactivation on next call. Simplest user-facing semantic ("update takes effect on next invoke").
- **C. In-place options mutation** — grain state update protocol; flaky, leaks state.

**Lean: B.** `AgentLifecycleManager.UpdateAsync` already exists on the contract but is underutilised in v0.6. Pillar B wires it: update manifest → evict from `IAgentRuntime` → evict from translator's options cache → next invoke triggers grain reactivation with the new manifest. Document "updates take effect on next invoke, not in-flight runs."

### Q10 — handler.typeName coexistence with Pillar C

**Context.** The manifest has both `Handler.TypeName` (code-authored plugin, Pillar C) AND the declarative `Model` + `SystemPrompt` + `Tools` fields (Pillar B). Combinations:

| `Handler.TypeName` | `Model + SystemPrompt` | v0.17 behaviour |
|---|---|---|
| null | present | Full Pillar B path — declarative instantiation. |
| set | null | `501 urn:vais-agents:handler-not-loaded` — Pillar C lands the plugin loader. Apply still succeeds (manifest valid). |
| set | present | Either both wired, or typeName wins. |
| null | null | `400 urn:vais-agents:manifest-invalid` — validator rejects at apply. |

**Lean for the both-set row: typeName wins, declarative fields are ignored with a WARN at apply.** The plugin's `IAiAgent` implementation is full-custom; mixing is a layering violation. Manifest-validation-time WARN surfaces the conflict without blocking partners who have a transitional manifest.

---

## Proposed PR shape

Four PRs inside `v0.17.0-preview`. Each independently shippable.

### PR 1 — `Vais.Agents.Runtime.Instantiation` package + factory contracts + manifest translator

- [ ] New library project `src/Vais.Agents.Runtime.Instantiation/` — `Microsoft.NET.Sdk`, `IsPackable=true`, PublicAPI analyzer on.
- [ ] Core contracts: `IModelProviderFactory`, `IStaticToolRegistry` (+ `IStaticToolRegistryBuilder`), `IGuardrailFactory`, `IAgentManifestTranslator`.
- [ ] `AgentManifestTranslator` — takes `IAgentRegistry` (to look up by id) + factory registries, returns `StatefulAgentOptions`. Handles the `handler.typeName` early-out (returns sentinel so the caller can 501).
- [ ] DI extension `services.AddAgentManifestInstantiator()`.
- [ ] Unit tests — 10+ covering every `SystemPromptSpec` shape, `ToolRef` source prefixes, empty-guardrails / missing-budget defaults, registry-miss (throws `AgentNotFoundException`), handler.typeName early-out.

### PR 2 — Model-provider factories + guardrail built-ins

- [ ] `OpenAIModelProviderFactory` — MEAI-IChatClient-backed; resolves API key via `ISecretResolver`.
- [ ] `AnthropicModelProviderFactory` — MEAI-IChatClient-backed (Anthropic.SDK + MEAI bridge).
- [ ] `AzureOpenAIModelProviderFactory` — AzureOpenAIClient.GetChatClient.AsIChatClient.
- [ ] `services.AddBuiltinModelProviders()` registers all three.
- [ ] Guardrail built-ins in `Core`:
  - [ ] `LengthCapInputGuardrail` — `maxChars` param.
  - [ ] `RegexAllowlistInputGuardrail` / `RegexAllowlistOutputGuardrail`.
  - [ ] `RegexDenylistInputGuardrail` / `RegexDenylistOutputGuardrail`.
  - [ ] `LlmAsJudgeOutputGuardrail` — takes a nested `ModelSpec`; resolves via `IModelProviderFactory`.
- [ ] `services.AddBuiltinGuardrails()` registers all four + DI-keyed `IGuardrailFactory` entries.
- [ ] Integration tests against mock / fake `IChatClient`.

### PR 3 — Runtime host wiring + UpdateAsync seam + A2A remotes

- [ ] `Runtime.Host`'s composition root calls `AddAgentManifestInstantiator()` + wires `ConfigureAgentGrains(sp, translator.TranslateForGrain)`.
- [ ] `AgentLifecycleManager.UpdateAsync` — wire manifest-update flow: replace in registry, evict from runtime cache, evict from translator cache.
- [ ] Extend `AgentManifest` with optional `A2ARemoteAgents: IReadOnlyList<A2ARemoteAgentRef>` section (name, url, authRef). PublicAPI unshipped→shipped edit.
- [ ] `ToolRef` resolver — `static:*` via `IStaticToolRegistry`, `mcp:*` via `McpServers`, `a2a:*` via `A2ARemoteAgents`.
- [ ] Integration test: docker-compose localhost + `vais apply -f weather.yaml` + `vais invoke weather --text "hi"` → returns model response (against a mock OpenAI server). Partner-acceptance equivalent.

### PR 4 — Docs + tag

- [ ] `docs/concepts/declarative-agents.md` — the v0.17 story: what shapes work, handler.typeName coexistence, update semantics.
- [ ] `docs/guides/author-an-agent-in-yaml.md` — end-to-end pure-YAML agent walkthrough (the master plan's `weather-assistant.yaml` example).
- [ ] `docs/guides/ship-a-guardrail.md` — custom `IGuardrailFactory` registration + use in a manifest.
- [ ] `docs/reference/manifest-schema.md` — canonical schema reference (was implicit in `Control.Abstractions` XML docs; elevate to a reference page).
- [ ] `docs/reference/runtime-configuration.md` — add new env-var knobs (if any emerged from PR 3).
- [ ] Milestone entry in `plans/actor-agents-oss-milestone-log.md`.
- [ ] Tick Pillar B in `plans/actor-agents-oss-phase-3-runtime-productisation.md`.
- [ ] Tag `v0.17.0-preview` on the merge commit.

Sizing: PR 1 ≈ 2 days, PR 2 ≈ 2-3 days, PR 3 ≈ 2-3 days, PR 4 ≈ 1-2 days. **Total 7-10 working days.** Matches the master-plan's 1-2 weeks/pillar sizing.

---

## Flagged risks + open items

- **MEAI-vs-SK path split.** If a partner already uses SK's Anthropic connector (1.74's native Anthropic flow) and we ship Anthropic via Anthropic.SDK + MEAI bridge, we're running parallel SDK stacks for the same model. Accept the diversity but document clearly in `docs/concepts/declarative-agents.md` so partners don't `AddAgenticOrleansHosting` + Anthropic-SK-connector and wonder which code path fires. (Lean Q5 says MEAI across all three; don't special-case.)
- **Guardrail ordering.** With multiple Input / Output guardrails, what's the evaluation order? Pre-Pillar-B contracts don't specify. **Decide in findings:** deterministic order = declaration order in the manifest. Document.
- **LLM-as-judge provider loops.** A judge guardrail using the same model as its agent = wasted dollars. Add a note in the built-in's description; don't enforce.
- **Manifest validator gaps.** The v0.6 validator doesn't check `ModelSpec.apiKeyRef` resolvability (that happens at invoke-time); it doesn't check `McpServers[].name` is referenced by any `ToolRef`; it doesn't check `GuardrailRef.Name` is registered. Pillar B adds these checks at apply-time — fail early, not at first invoke.
- **ConfigureAgentGrains is a lambda closure over IServiceProvider.** The translator must be registered before the Orleans silo boots so the closure resolves the translator correctly. Pillar A's composition-root unit tests guard against ordering regressions; Pillar B's version needs a matching test that asserts translator registration precedes silo wiring.
- **A2A subquestion.** Extending manifest with `A2ARemoteAgents` is additive but needs a contract decision. Findings doc should lock the exact shape.
- **Partners expecting Azure-AI-Inference.** Microsoft's newer MEAI-native Azure AI Inference client differs from Azure.AI.OpenAI. Check which ships in MEAI 10.5 and whether `AzureOpenAIModelProviderFactory` should use it or `Azure.AI.OpenAI`. Likely the latter for stability; track as a follow-up.

---

## What the findings doc will commit to

The findings doc locks each blocking question to an answer with evidence. Rough sketch of decisions the spike leans toward:

| # | Decision |
|---|---|
| 1 | New `Vais.Agents.Runtime.Instantiation` package (library-layer, IsPackable=true). |
| 2 | `AddAgentManifestInstantiator()` DI extension + `ConfigureAgentGrains(sp, translator.Translate)` wiring in Runtime.Host. |
| 3 | Lazy registry lookup with per-id cache inside the translator; revisit if cold-start regresses. |
| 4 | `IModelProviderFactory` keyed by provider string; one factory per provider at launch. |
| 5 | MEAI-IChatClient path for OpenAI / Anthropic / AzureOpenAI. `Ai.SemanticKernel` stays for consumer-supplied Kernels. |
| 6 | `IStaticToolRegistry` (plain string-keyed) over keyed DI. |
| 7 | Lazy `McpToolSource` per declared server + new `A2ARemoteAgents` manifest section for `a2a:*` refs. |
| 8 | Four guardrail built-ins live in `Core`; `IGuardrailFactory` DI-registered by name. LLM-as-judge uses nested `ModelSpec`. |
| 9 | `UpdateAsync` evicts from both `IAgentRuntime` cache and translator cache; in-flight runs are not terminated; next invoke reactivates. |
| 10 | `handler.typeName` set + declarative fields present → typeName wins with a WARN. typeName set alone → 501 until Pillar C. |

Timeline-wise: findings doc next, then pillar plan, then PR 1 kicks off the implementation.
