# v0.17.0-preview — Manifest-driven agent instantiation pillar

Tactical plan for [Phase 3 Pillar B](./actor-agents-oss-phase-3-runtime-productisation.md#pillar-b--declarative-agent-instantiation-us-4-enables-us-2) — turn a stored `AgentManifest` into a running `IAiAgent` so `vais invoke` stops returning 501. Grounded in the spike + findings: [`actor-agents-oss-v0.17-manifest-instantiation-{spike,findings}.md`](./actor-agents-oss-v0.17-manifest-instantiation-spike.md). Parallel shape to [`actor-agents-oss-v0.16-runtime-container-pillar.md`](./actor-agents-oss-v0.16-runtime-container-pillar.md). Created 2026-04-21.

---

## Scope

**MVP boundary locked 2026-04-21** via the research spike + findings (user confirmed leans: "leans are ok"). 10 decisions:

1. **New package** `Vais.Agents.Runtime.Instantiation` (library-layer, `IsPackable=true`). Holds translator + factory contracts + pool.
2. **DI extension** `AddAgentManifestInstantiator(IServiceCollection)`. Runtime.Host's composition root calls it + wires `ConfigureAgentGrains(sp, translator.TranslateForGrain)`.
3. **Lazy lookup** of manifest at grain activation. Translator owns a per-id `ConcurrentDictionary<string, StatefulAgentOptions>` cache, invalidated on `UpdateAsync`.
4. **`IModelProviderFactory` contract** keyed by `Provider` string. One factory per provider at launch (OpenAI / Anthropic / Azure-OpenAI).
5. **MEAI `IChatClient` path** for all three built-in factories. Consumers who want SK stay on `Vais.Agents.Ai.SemanticKernel` and supply a custom `IModelProviderFactory`.
6. **`IStaticToolRegistry` with factory-based registration** — `"static:<name>"` resolves through it. Keyed DI path not added in v0.17.
7. **Lazy MCP source per declared server + new `A2ARemoteAgents` manifest section** for `"a2a:<name>"` refs. PublicAPI edit: unshipped→shipped at tag.
8. **Four guardrail built-ins in `Core`** (`LengthCap`, `RegexAllowlist`, `RegexDenylist`, `LlmAsJudge`) + `IGuardrailFactory` DI-by-name; LLM-as-judge takes a nested `ModelSpec` + uses the `ICompletionProviderPool` to avoid duplicate allocations.
9. **`UpdateAsync` evicts** from `IAgentRuntime` cache and translator cache; next invoke reactivates. In-flight runs continue with their original manifest.
10. **`handler.typeName` wins when both set**; WARN at apply time; `501 urn:vais-agents:handler-not-loaded` when typeName set and no Pillar C plugin registered.

### Semantic projection chosen

**Manifest-as-wiring.** The runtime takes a YAML manifest and assembles the same `StatefulAgentOptions` a human would have hand-written. Pure mechanical translation + three plug seams for extensibility: model providers, guardrails, static tools. No runtime magic, no DI hocus-pocus — everything is a named factory in DI + a registry lookup.

### Explicitly deferred to post-v0.17

- **`handler.typeName` plugin loading** — Pillar C (v0.18).
- **`ContextProviders`, `Handoffs`, `Reasoning`, `Observability` manifest fields** — contracts exist; runtime ignores. Revisit per partner feedback.
- **Custom `ICompletionProvider` shapes beyond the three providers** — consumer-wired via `IModelProviderFactory`; docs call out the extension point.
- **`OutputSchema` runtime enforcement** — thread through to the provider when supported, no validation wrapper.
- **Schema-validated guardrail params** — partners write typos, they get `ManifestInstantiationException`. Pillar F polish if partners hit it often.
- **Graph lifecycle manager** — Pillar D (v0.19).
- **LRU eviction on translator cache** — v0.17 caches unboundedly. Revisit with cold-start metrics.

---

## Design questions — resolved

Full table + evidence in [`actor-agents-oss-v0.17-manifest-instantiation-findings.md`](./actor-agents-oss-v0.17-manifest-instantiation-findings.md). Summary:

| # | Question | Decision |
|---|---|---|
| 1 | Instantiator code location | New `Vais.Agents.Runtime.Instantiation` library package |
| 2 | Translator plug point | `AddAgentManifestInstantiator()` + explicit `ConfigureAgentGrains` wire in host |
| 3 | Registry lookup cost | Lazy + per-id cache in translator |
| 4 | Model-provider factory contract | `IModelProviderFactory`, keyed by `Provider` string |
| 5 | SDK path | MEAI `IChatClient` for all three; SK stays for consumer-supplied `Kernel` |
| 6 | Static-tool resolution | `IStaticToolRegistry` with factory-based `Add(name, sp=>ITool)` |
| 7 | MCP + A2A refs | Lazy per-server + new `AgentManifest.A2ARemoteAgents` section |
| 8 | Guardrail built-ins | Four impls in `Core`; `IGuardrailFactory` DI-by-(name, layer); LLM-as-judge via `ICompletionProviderPool` |
| 9 | Update semantics | Evict-and-reactivate; in-flight runs untouched |
| 10 | handler.typeName coexistence | typeName wins with WARN; 501 until Pillar C |

---

## Proposed PR shape

Four-PR sequence inside `v0.17`. Each independently shippable.

### PR 1 — `Vais.Agents.Runtime.Instantiation` package + translator + contracts ✅

- [x] Create `src/Vais.Agents.Runtime.Instantiation/Vais.Agents.Runtime.Instantiation.csproj` — `Microsoft.NET.Sdk`, `net9.0`, `IsPackable=true`, PublicAPI analyzer on, `InternalsVisibleTo Vais.Agents.Runtime.Instantiation.Tests`.
- [x] `IAgentManifestTranslator` contract — `TranslateAsync` / `TranslateForGrain` / `InvalidateAsync`.
- [x] `IModelProviderFactory` contract — `Provider` property + `CreateAsync(ModelSpec, ISecretResolver, CancellationToken)`.
- [x] `IGuardrailFactory` contract — `Name`, `Layer`, `Create(JsonElement?, IServiceProvider)`. Keyed on `(Name, Layer)`. Drift from findings: `Create` takes `JsonElement?` not `IReadOnlyDictionary<string, JsonElement>` — matches `GuardrailRef.Params` shape in `Abstractions`.
- [x] `IStaticToolRegistry` + `IStaticToolRegistryBuilder`.
- [x] `IPromptTemplateRegistry` + `IPromptTemplateRegistryBuilder` (added beyond findings — needed for `SystemPromptSpec.TemplateRef` support).
- [x] `IPromptFileLoader` + `FileSystemPromptFileLoader` (added beyond findings — needed for `SystemPromptSpec.FileRef` support with path-traversal guard).
- [x] `ICompletionProviderPool` + `CompletionProviderPool` — memoises `ICompletionProvider` by `ModelSpec` hash with single-flight via `Lazy<Task<T>>`.
- [x] `AgentManifestTranslator` — registry lookup + cache + `Model == null` → `HandlerNotLoaded` early-out + provider warm via pool + `SystemPromptSpec` dispatch (inline/template/file + `{{key}}` variable substitution + ambiguous-shape detection) + `ToolRef.Source` prefix dispatch (static/mcp/a2a/unknown) + `GuardrailsSpec` lookup by (name, layer) + `Budget` threading.
- [x] `ManifestInstantiationException` + `ManifestInstantiationUrns` static class with 13 URNs covering every throw path.
- [x] DI extensions: `AddAgentManifestInstantiator()`, `AddStaticToolRegistry(delegate)`, `AddPromptTemplateRegistry(delegate)`, `AddFileSystemPromptFileLoader(rootPath)`.
- [x] `PublicAPI.Unshipped.txt` populated (40+ entries).
- [x] **`Vais.Agents.Hosting.Orleans.OrleansAgentRegistry`** (scope-expansion bundled per pillar plan risks section) — `IAgentRegistryGrain` per-id + `IAgentRegistryDirectoryGrain` singleton for `ListAsync` enumeration. Exposes concrete `RegisterAsync` / `RemoveAsync` helpers + `AddOrleansAgentRegistry()` DI extension. Composition-root swap in Runtime.Host is PR 3.
- [x] Unit tests (`tests/Vais.Agents.Runtime.Instantiation.Tests/`) — **21 tests, all green**:
  - Happy path — full manifest translates with every slot populated.
  - Registry miss → `AgentNotFound`.
  - `Model is null` → `HandlerNotLoaded` (drift from plan: v0.17 uses `Model != null` as the declarative switch, not `Handler.TypeName` — because `TypeName` is required in the ctor so a null check isn't possible).
  - Unknown `ModelSpec.Provider` → `ModelProviderUnsupported`.
  - Cache hit returns same instance.
  - `InvalidateAsync` drops only the target id.
  - `InvalidateAsync` on unknown id returns false.
  - `SystemPromptSpec.Inline` with `{{variable}}` substitution.
  - `SystemPromptSpec.TemplateRef` resolves via `IPromptTemplateRegistry`.
  - `SystemPromptSpec.TemplateRef` not registered → `PromptTemplateNotRegistered`.
  - `SystemPromptSpec.FileRef` resolves via `IPromptFileLoader`.
  - Ambiguous `SystemPromptSpec` (two shapes set) → `PromptSpecAmbiguous`.
  - `static:*` tool resolves via registry.
  - `static:*` tool unknown → `ToolNotRegistered`.
  - `mcp:*` tool declared passes validation (v0.17 PR 1 validates only; PR 3 materialises).
  - `mcp:*` tool undeclared → `McpServerNotDeclared`.
  - `a2a:*` tool → `A2AAgentNotDeclared` (always errors in PR 1; PR 3 adds the `A2ARemoteAgents` manifest section).
  - Unknown source prefix → `ToolSourceUnknown`.
  - Guardrail unknown → `GuardrailNotRegistered`.
  - Budget threaded through to `StatefulAgentOptions.Budget`.
  - Null `SystemPromptSpec` with `Model` set → `options.SystemPrompt` is null.
- [x] Build clean; **0 warnings, 0 errors on the full solution**. Pillar A's 7 composition-root guards + 21 new translator tests all green.

### PR 2 — Model-provider factories + guardrail built-ins

- [ ] `Directory.Packages.props` — add `Anthropic.SDK` + `Azure.AI.OpenAI` pins.
- [ ] `OpenAIModelProviderFactory` in `Vais.Agents.Runtime.Instantiation`:
  - `Provider` = `"openai"`.
  - Resolves `spec.ApiKeyRef` via `ISecretResolver`.
  - Builds `new OpenAIClient(apiKey).GetChatClient(modelId).AsIChatClient()` → `MafCompletionProvider`.
- [ ] `AnthropicModelProviderFactory` — `Provider` = `"anthropic"`; Anthropic.SDK + MEAI bridge → `MafCompletionProvider`.
- [ ] `AzureOpenAIModelProviderFactory` — `Provider` = `"azure-openai"`; `Azure.AI.OpenAI` 2.3.x → `MafCompletionProvider`. Resolves `BaseUrlRef` as the endpoint.
- [ ] DI extension `AddBuiltinModelProviders(IServiceCollection)` — registers all three.
- [ ] Four guardrail built-ins under `src/Vais.Agents.Core/Guardrails/`:
  - [ ] `LengthCapInputGuardrail` — integer `maxChars` param, denies `request.Input.Length > maxChars`.
  - [ ] `RegexAllowlistInputGuardrail` + `RegexAllowlistOutputGuardrail` — pre-compiled `Regex` from `pattern` param; denies on no-match.
  - [ ] `RegexDenylistInputGuardrail` + `RegexDenylistOutputGuardrail` — denies on match.
  - [ ] `LlmAsJudgeOutputGuardrail` — params: `judgeModel: ModelSpec`, `judgePrompt: string`, `minScore: double`. Resolves judge provider through `ICompletionProviderPool`; parses score from response; denies below threshold.
- [ ] `IGuardrailFactory` concrete factories — one per built-in × layer, keyed on `(Name, Layer)`.
- [ ] DI extension `AddBuiltinGuardrails(IServiceCollection)` — registers all factories.
- [ ] Unit tests for each guardrail (deny + allow paths; param parsing; LLM-as-judge against a fake `ICompletionProvider`).
- [ ] Integration tests for factories against mock HTTP servers (`WireMock.Net` or similar) — OpenAI / Anthropic / AzureOpenAI responses.

### PR 3 — Runtime host wiring + grain seam + A2A manifest ✅ (partial — update-flow + HTTP warnings + integration test split to PR 4)

- [x] Extend `Vais.Agents.Abstractions.AgentManifest` with `A2ARemoteAgents: IReadOnlyList<A2ARemoteAgentRef>?` — init-only, default null.
- [x] Add `A2ARemoteAgentRef(Name, Url, AuthRef?, Metadata?)` record.
- [x] PublicAPI edit in `Abstractions` — `Unshipped.txt` entries (incl. record auto-gen members + operators).
- [x] Translator wired for `a2a:*` refs — validates declaration against `A2ARemoteAgents`; lazy A2ARemoteAgentTool materialization is a followup (documented TODO).
- [x] **Grain seam fix** (not in original PR 3 scope — surfaced as a dependency): `StatefulAgentOptions.CompletionProvider` slot added in `Core`; `AiAgentGrain` uses `supplied.CompletionProvider ?? _defaultProvider`; translator stashes the resolved provider. Without this, translator-selected per-agent providers never reach the grain — makes the rest of Pillar B structurally complete.
- [x] **`AgentLifecycleManager.CreateAsync` Orleans-compat** (not in original PR 3 scope — surfaced during composition-root swap): `OrleansAgentRegistry` now exposes `Register(AgentManifest)` + `Remove(string, string)` sync shims matching the signatures `AgentLifecycleManager.{Register,Remove}Manifest` duck-types onto. Sync-over-async wraps are acceptable (in-process grain RPC, sub-millisecond).
- [x] `Runtime.Host.CompositionRoot.ConfigureServices`:
  - [x] `AddOrleansAgentRegistry()` swap (replaces `InMemoryAgentRegistry`).
  - [x] `AddAgentManifestInstantiator()` + `AddBuiltinModelProviders()` + `AddBuiltinGuardrails()` — registered before `ConfigureAgentGrains`.
  - [x] `TryAddSingleton<ISecretResolver>(_ => CompositeSecretResolver.CreateDefault())` — factories resolve secrets via this.
  - [x] `ConfigureAgentGrains` wired to translator-backed lambda.
- [x] Runtime host unit test additions (3 new, 10 total green):
  - `Composition_Translator_Registered_For_ConfigureAgentGrains` — translator + Func<string, StatefulAgentOptions> resolvable.
  - `Composition_Swaps_InMemoryRegistry_For_OrleansAgentRegistry` — v0.16 → v0.17 registry swap guard.
  - `Composition_Registers_Builtin_Providers_And_Guardrails` — 3 provider factories + 6 guardrail factories.
- [x] Orleans state serialization fix (surfaced in `CrossHostTests`): `IAgentRegistryGrain` shifted from `AgentManifest` to JSON-string payloads + `AgentRegistryGrainState.ManifestJsonByVersion` dictionary; `Abstractions` stays Orleans-free (no `[GenerateSerializer]`); `OrleansAgentRegistry` handles ser/deser at the service boundary.
- [ ] **Deferred to PR 4**: `AgentLifecycleManager.UpdateAsync` eviction wire-through + WARN on both-set + `AgentManifestApplyResponse.Warnings` + HTTP route surface for warnings + CLI warnings plumbing + manifest-validator updates for Json/Yaml (likely auto-handled by System.Text.Json for the new A2A field, but needs verification). Integration test `apply → invoke → update → invoke → delete` shifts to PR 4 (aligns with the docs + tag phase since that's where end-to-end scenarios get exercised).
- [ ] **Deferred to post-v0.17**: lazy `McpToolSource` per declared server + lazy `A2ARemoteAgentTool` per declared A2A agent. Translator validates declarations today; tool materialization ships when partner feedback lands. TODOs in translator code comments point at this.

### PR 4 — Docs + tag ✅ (partial — 3 guides + integration test shipped; custom-provider guide + manifest-schema reference deferred to v0.17.1)

- [x] `docs/concepts/declarative-agents.md` — new. Covers the 10-step pipeline from `vais apply` to live agent, `Model != null` as the declarative switch, all three `SystemPromptSpec` shapes, `static:` / `mcp:` / `a2a:` tool refs, all six guardrail factories, update semantics, `handler.typeName` coexistence, and the 12-URN failure table.
- [x] `docs/guides/author-an-agent-in-yaml.md` — new. Eight-step walkthrough: export key → write manifest → apply → invoke → update → add tools (v0.17 limitation flagged) → add guardrails → delete, plus troubleshooting.
- [ ] `docs/guides/ship-a-guardrail.md` — **deferred to v0.17.1** (partners can follow the contract in `declarative-agents.md` + `IGuardrailFactory` XML docs).
- [ ] `docs/guides/ship-a-custom-model-provider.md` — **deferred to v0.17.1** (same reasoning as above).
- [ ] `docs/reference/manifest-schema.md` — **deferred to v0.17.1**. Per-field reference; the XML-doc comments on `AgentManifest` + sub-records already carry the canonical semantics, and `GET /openapi/v1.json` emits the JSON Schema for the wire-format.
- [ ] `docs/reference/runtime-configuration.md` — no v0.17 env-var knobs surfaced; skipped.
- [x] `docs/reference/packages.md` — version bump `0.16 → 0.17`, new "Manifest-driven instantiation (v0.17)" section + row for `Vais.Agents.Runtime.Instantiation`, `Hosting.Orleans` row updated with `OrleansAgentRegistry` + `AddOrleansAgentRegistry`.
- [x] `docs/concepts/architecture.md` — new "Manifest instantiation tier (v0.17 Pillar B)" section with ASCII pipeline diagram + key invariants. "25 packages" → "26 packages".
- [x] `docs/index.md` — Concepts: `declarative-agents`. Guides: `author-an-agent-in-yaml`. Reference line updated to 26-package table.
- [x] **Update flow**: `AgentLifecycleManager.UpdateAsync` wires through `IAgentRuntime.Remove(id)` + `IAgentManifestInvalidator.InvalidateAsync(id)` — the new `IAgentManifestInvalidator` contract lives in `Control.Abstractions` so layering stays clean; `IAgentManifestTranslator` inherits it. `AgentLifecycleManager`'s ctor grows an optional `IAgentManifestInvalidator? invalidator = null` param (breaking-but-additive; PublicAPI *REMOVED* + new entry). `EvictAsync` wires the same invalidation.
- [x] **End-to-end integration test**: `ManifestInstantiationIntegrationTests` exercises `CompositionRoot.ConfigureServices` → in-memory registry + fake provider → translator → StatefulAiAgent.AskAsync. Two scenarios — apply+invoke+response and register-v2+invalidate+invoke — both green. Skips TestCluster for speed; Orleans grain path is covered by existing `CrossHostTests` + `Hosting.Orleans.Tests`.
- [x] **PublicAPI promotion** across six assemblies (Abstractions / Control.Abstractions / Control.InProcess / Core / Hosting.Orleans / Runtime.Instantiation). All `Unshipped.txt` contents merged into `Shipped.txt`; two `*REMOVED*` entries (old AiAgentGrain ctor + old AgentLifecycleManager ctor) cleaned out.
- [x] Milestone entry in `plans/actor-agents-oss-milestone-log.md`.
- [x] Tick Pillar B in `plans/actor-agents-oss-phase-3-runtime-productisation.md`.
- [x] **Tag `v0.17.0-preview`** — annotated on OSS commit `2b2bb5d`, 2026-04-21. Two-commit merge on OSS `main`: `163a2e9` (library layer: translator + providers + guardrails) + `2b2bb5d` (runtime host + docs + PublicAPI promotion). Local-only, no remote configured.

**Deferred to v0.17.1 or Pillar B polish**:
- `ship-a-guardrail.md` + `ship-a-custom-model-provider.md` guides.
- `manifest-schema.md` full reference (XML docs + OpenAPI schema cover the gap today).
- HTTP warnings surface + CLI warnings plumbing + validator warnings for `handler.typeName` + declarative-fields both-set (Pillar B's "WARN at apply time" per findings §Q10; today this surfaces as a runtime log, not a user-facing warning).
- Lazy `McpToolSource` + `A2ARemoteAgentTool` materialization — translator validates declarations today; tool wiring ships when partners land a specific use case.

---

## Acceptance

Pillar B is done when:

- [ ] `vais apply -f weather-assistant.yaml` → 200 with `warnings: []` (or `[handler-and-declarative-fields-both-set]` if both populated).
- [ ] `vais invoke weather-assistant --text "What's the weather in Tokyo?"` → real model response (not 501). Smoke test covers both a mock OpenAI server and the full declarative surface.
- [ ] `vais apply -f` on a manifest with `handler.typeName` (no declarative fields) succeeds; `vais invoke` returns `501 urn:vais-agents:handler-not-loaded` with message pointing at Pillar C.
- [ ] `vais apply -f` on a manifest with `Model.Provider = "bedrock"` (no registered factory) → `400 urn:vais-agents:model-provider-unsupported` at apply time.
- [ ] `vais apply -f` on a manifest with `Tools: [{source: "mcp:ghost"}]` when `ghost` isn't in `McpServers` → `400 urn:vais-agents:mcp-server-not-declared`.
- [ ] Update flow: apply → invoke → apply-with-new-prompt → invoke → new prompt reflected in provider messages.
- [ ] Composition-root unit tests include the translator-ordering guard.
- [ ] Full Pillar A test suite (7 existing guards) stays green.
- [ ] Build clean; 0 warnings.
- [ ] `Runtime.Instantiation.Tests` + `Runtime.Host.Tests` both green.
- [ ] All new `docs/*` pages reviewed; cross-links from `docs/index.md` / `architecture.md` / `packages.md` intact.
- [ ] Tag `v0.17.0-preview` on the merge commit.

---

## Composition-root extension — sketch

Reference for PR 3's changes to `src/Vais.Agents.Runtime.Host/CompositionRoot.cs`. New registrations marked `// NEW in v0.17`; existing Pillar A wiring compressed to ellipsis.

```csharp
public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
{
    // ...Pillar A: durability sidecars (unchanged, must come first)...
    services.AddOrleansA2ATaskStore();
    services.AddOrleansGraphCheckpointer();
    services.AddOrleansIdempotencyStore();

    // ...Pillar A: Orleans runtime + event bus...
    services.AddOrleansAgentRuntime();
    services.AddOrleansAgentEventBus();

    // ── v0.17 translator + provider + guardrail registrations (BEFORE ConfigureAgentGrains)
    services.AddAgentManifestInstantiator();     // NEW in v0.17
    services.AddBuiltinModelProviders();          // NEW in v0.17
    services.AddBuiltinGuardrails();              // NEW in v0.17
    services.AddSingleton<ISecretResolver>(sp => CompositeSecretResolver.CreateDefault());  // NEW in v0.17 — was composed implicitly in Pillar A

    // ── v0.17: swap ConfigureAgentGrains default with manifest-driven factory
    services.ConfigureAgentGrains((sp, id) =>
        sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));

    // ...Pillar A: in-process control plane scaffolding (unchanged)...
    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
    services.AddSingleton<IAuditLog, LoggerAuditLog>();
    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
        sp.GetRequiredService<IAgentRegistry>(),
        sp.GetRequiredService<IAgentRuntime>(),
        policy: sp.GetService<IAgentPolicyEngine>(),
        audit: sp.GetService<IAuditLog>(),
        contextAccessor: sp.GetService<IAgentContextAccessor>(),
        logger: sp.GetService<ILogger<AgentLifecycleManager>>() ?? NullLogger<AgentLifecycleManager>.Instance));

    // ...rest of Pillar A unchanged...
}
```

Consumer surface — the partner who writes a custom host opts in with three lines:

```csharp
builder.Services.AddAgentManifestInstantiator();
builder.Services.AddBuiltinModelProviders();
builder.Services.AddBuiltinGuardrails();
builder.Services.ConfigureAgentGrains((sp, id) =>
    sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));
```

---

## Timeline

- Spike + findings: complete.
- PR 1 (package + translator + tests): 2 days.
- PR 2 (factories + guardrails): 2-3 days.
- PR 3 (host wiring + UpdateAsync + A2A manifest + invoke smoke): 2-3 days.
- PR 4 (docs + tag): 1-2 days.

Total Pillar B: **7-10 working days** (~2 weeks). Matches the master plan's 1-2 weeks/pillar sizing.

---

## Risks + mitigations

- **MEAI/MAF stack drift.** `Microsoft.Extensions.AI` 10.5 + `Microsoft.Agents.AI` 1.1.0 are relatively new; upstream `IChatClient` shape has already moved once between previews (v0.15 CLI milestone noted MAF's `thread:` → `session:` rename). Mitigation: integration tests pin specific SDK major versions; package pins go through `Directory.Packages.props`; MAF param-rename-style breaks caught at compile.
- **Anthropic SDK immaturity.** `Anthropic.SDK` is community-authored; MEAI bridge may not track upstream Anthropic API changes. Mitigation: integration test against a mock server; partner-facing docs note Anthropic is preview-grade; keep the factory trivially swappable for consumers who want SK's native connector.
- **`IAgentRegistry.GetAsync` contract ambiguity.** Survey found `InMemoryAgentRegistry` but no persistent Orleans-backed registry — Pillar A's runtime host registers `InMemoryAgentRegistry` for v0.16. A durable registry (backed by `IGrainFactory`) is a Pillar B prerequisite; without it, `vais apply` persists to memory and the registry evaporates on pod roll. **Action:** add `OrleansAgentRegistry` (backed by a `RegistryGrain` keyed on agent id) as part of PR 1 — tight scope, no new concepts. Composition root swaps `InMemoryAgentRegistry` for `OrleansAgentRegistry` in Runtime.Host.
- **Grain activation sync-over-async.** `Func<string, StatefulAgentOptions>` is sync; `IAgentManifestTranslator.TranslateAsync` is async. The `TranslateForGrain` method must `GetAwaiter().GetResult()` the registry lookup. Mitigation: the registry call is in-process (Orleans grain → Orleans grain; same silo) — sub-millisecond. Document the discipline; keep the lookup cheap.
- **Guardrail param schema drift.** Manifests carry `params: IReadOnlyDictionary<string, JsonElement>` — free-form. If a partner misnames `maxChars` as `maxChar`, the guardrail silently ignores. Mitigation: each built-in factory validates its params at `Create` time and throws `ManifestInstantiationException` with the malformed key; `vais apply` bubbles the error.
- **`UpdateAsync` race.** If `UpdateAsync` and `InvokeAsync` race on the same agent id, the invoke might hit the old cached options while the translator cache gets invalidated. Mitigation: translator uses `ConcurrentDictionary<string, Task<StatefulAgentOptions>>` — invalidation drops the entry; next invoke does a fresh registry read. In-flight invokes hold their captured options reference; no mutation. Document the "next invoke, not in-flight" semantic.
- **v0.17 `Runtime.Instantiation` PublicAPI instability.** We're shipping a lot of new contracts at once; future pillars will likely tweak. Mitigation: PR 1 puts everything in `Unshipped.txt`; promotion to `Shipped.txt` at tag time is deliberate, not reflexive. Add a note in the `Runtime.Instantiation` README: "v0.17 surface is preview — API shape may change through v1.0."

---

## Progress log

- 2026-04-21 — Pillar plan created. Scope locked from spike + findings. Four-PR sequence: package + translator → factories + guardrails → host wiring + update + A2A → docs + tag. ~7-10 working days. **Pending**: PR 1.
- 2026-04-21 — PR 1 landed. Added `src/Vais.Agents.Runtime.Instantiation/` with 8 contracts + 7 implementations + 4 DI extensions; 21 unit tests all green; full solution clean. OrleansAgentRegistry (grain-per-id + directory grain + concrete service wrapper + `AddOrleansAgentRegistry` extension) bundled into `Vais.Agents.Hosting.Orleans` per the plan's risks section. Two drifts from findings: (1) `IGuardrailFactory.Create` takes `JsonElement?` directly (matches manifest shape), not a dict; (2) the declarative-path switch is `Model != null`, not the `Handler.TypeName`-null check the findings sketched — `TypeName` is non-nullable in the `AgentHandlerRef` ctor, so v0.17's translator pivots on `Model`. Findings doc Q10 table semantic is preserved (typeName-only → HandlerNotLoaded 501; declarative-only → full path), just with a different code-level switch. Added 2 contracts beyond findings scope: `IPromptTemplateRegistry` (for `SystemPromptSpec.TemplateRef`) + `IPromptFileLoader` / `FileSystemPromptFileLoader` (for `SystemPromptSpec.FileRef`). Without them the "all three SystemPromptSpec shapes" PR 1 test criteria could not be satisfied. **Next**: PR 2 — three built-in model-provider factories (OpenAI / Anthropic / Azure-OpenAI) + four guardrail built-ins + `AddBuiltinModelProviders` + `AddBuiltinGuardrails` DI extensions.
- 2026-04-21 — PR 2 landed. Added `Runtime.Instantiation/ModelProviders/` with `OpenAIModelProviderFactory` / `AnthropicModelProviderFactory` / `AzureOpenAIModelProviderFactory` + `AddBuiltinModelProviders` extension. Added `Core/Guardrails/` with 5 guardrail classes (LengthCap / RegexAllowlist×2 / RegexDenylist×2 / LlmAsJudge) + `Runtime.Instantiation/Guardrails/` with 6 factories + `AddBuiltinGuardrails` extension + a `ParamHelpers` utility covering every param shape. 25 new unit tests (14 guardrail + 11 factory) added to `Runtime.Instantiation.Tests`; 46 tests total green; full solution 0 warnings / 0 errors. One drift: `Azure.AI.OpenAI 2.3.0` stable not available — stepped down to `2.2.0-beta.4` (no stable 2.3+ on nuget.org; nearest stable is below 2.3 or beta at 2.5.0-beta.1). Anthropic.SDK 5.5.1 `AnthropicClient.Messages` implements `IChatClient` directly, so no extra bridge extension needed. One Windows-locale quirk caught + fixed: LLM-as-judge's reason-string number formatting was culture-dependent (Russian locale ⇒ comma decimal), now forced to `CultureInfo.InvariantCulture`. **Next**: PR 3 — runtime host composition-root swap (`InMemoryAgentRegistry` → `OrleansAgentRegistry`, `ConfigureAgentGrains` → translator), `AgentLifecycleManager.UpdateAsync` eviction, `AgentManifest.A2ARemoteAgents` extension, end-to-end invoke smoke test.
- 2026-04-21 — PR 3 landed (critical-path). Wired grain + options + translator so per-agent providers from the manifest reach `AiAgentGrain` activation: added `StatefulAgentOptions.CompletionProvider`, changed `AiAgentGrain` to prefer `supplied.CompletionProvider ?? _defaultProvider` with breaking but additive ctor signature change (docs in Unshipped.txt). Added `A2ARemoteAgentRef` record + `AgentManifest.A2ARemoteAgents` property in `Abstractions`; translator now validates `a2a:*` refs against declarations (tool materialization deferred). Runtime host composition-root swapped `InMemoryAgentRegistry` for `OrleansAgentRegistry`, registered `AddAgentManifestInstantiator` + `AddBuiltinModelProviders` + `AddBuiltinGuardrails` + `ISecretResolver`, and wired `ConfigureAgentGrains` at the translator via a Func that resolves `IAgentManifestTranslator` per grain activation. Three new composition-root unit tests (10 total Pillar A+B guards). Two surprises caught + fixed: (1) `CrossHostTests` broke because `IAgentRegistryGrain` passed `AgentManifest` across the grain boundary without an Orleans serializer — switched the grain interface + state to JSON strings, added `OrleansAgentRegistry.{Serialize,Deserialize}Manifest` helpers; Abstractions stays Orleans-free. (2) `AgentLifecycleManager.CreateAsync` duck-types onto `Register(AgentManifest)` which `OrleansAgentRegistry` didn't have — added sync shims on the concrete registry matching the reflection shape. All 20 test projects pass; full solution 0 warnings / 0 errors. **Deferred to PR 4** (bundled with docs + tag): `AgentLifecycleManager.UpdateAsync` eviction-on-update wire-through, `AgentManifestApplyResponse.Warnings` + HTTP route surface, CLI warnings plumbing, manifest validator JSON/YAML verification for A2A field, end-to-end integration test (apply → invoke → update → invoke → delete) against a mock OpenAI server, lazy MCP + A2A tool materialization.
- 2026-04-21 — PR 4 landed (partial). Critical-path docs + integration test + update-flow + PublicAPI promotion all complete. Added `docs/concepts/declarative-agents.md` (10-step pipeline + URN catalog), `docs/guides/author-an-agent-in-yaml.md` (8-step weather-agent walkthrough), updated `docs/concepts/architecture.md` with a Manifest-instantiation-tier section, bumped `docs/reference/packages.md` to 0.17.0-preview with Runtime.Instantiation row + Hosting.Orleans AgentRegistry mention, updated `docs/index.md` entries. Added `Vais.Agents.Control.IAgentManifestInvalidator` contract in `Control.Abstractions` (kept in low-dep layer to avoid flipping layering with `Runtime.Instantiation`); `IAgentManifestTranslator` extends it; `AgentLifecycleManager` grows an optional `IAgentManifestInvalidator? invalidator = null` ctor param and calls it in both `UpdateAsync` and `EvictAsync` alongside `IAgentRuntime.Remove(id)`. Runtime.Host composition root wires the translator as the invalidator via `TryAddSingleton<IAgentManifestInvalidator>(sp => sp.GetRequiredService<IAgentManifestTranslator>())`. Integration test `ManifestInstantiationIntegrationTests` exercises two scenarios end-to-end (apply+invoke-returns-response, register-v2+invalidate+invoke-picks-up-new-prompt) — 12 Runtime.Host tests total green. PublicAPI promotion across 6 assemblies completed; two *REMOVED* ctor entries (old AiAgentGrain + old AgentLifecycleManager) cleaned from Shipped. Full solution 0 warnings / 0 errors across all 20 test projects. **Deferred to v0.17.1**: `ship-a-guardrail.md` + `ship-a-custom-model-provider.md` guides; full `manifest-schema.md` reference; HTTP warnings surface + CLI plumbing; lazy MCP + A2A tool materialization. **Tag pending user confirmation** — matches v0.16 protocol.
