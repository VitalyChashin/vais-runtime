# Actor-Agents OSS — Milestone log

Dated, append-only record of what each milestone actually delivered. Written at wrap-up, not predicted. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md); the research doc now carries only a thematic findings summary (§8) and links here for chronology.

Process: tick the task checkboxes in the research doc workstream sections, then drop an entry here with what landed, what surprised us, and what's next.

---

### 2026-04-17 — Phase 0 spike complete

**Goal.** Prove that a single `ICompletionProvider` abstraction can sit cleanly behind both Semantic Kernel and Microsoft Agent Framework without forcing either into a `Microsoft.Extensions.AI.IChatClient` pass-through.

**What landed (`spike/agentic-phase0/`).**
- Throwaway 5-project solution: `Abstractions`, `Core`, `Sk`, `Maf`, `Sample`. Outside `Vais2Platform.sln`; does not touch VAIS2.
- `StatefulAiAgent` drives history; `SkCompletionProvider` uses SK's native `IChatCompletionService`; `MafCompletionProvider` uses MAF's `ChatClientAgent` via `IChatClient.CreateAIAgent(...)`.
- `dotnet build` clean; `dotnet run` against OpenAI produced coherent two-turn conversations on both stacks with identical neutral-core behaviour (second turn answered using context from the first — proving history passes through).

**Surprises / decisions forced.**
- MAF's `RunAsync(IEnumerable<ChatMessage>, ...)` parameter was renamed `thread:` → `session:` between preview `1.0.0-preview.251009.1` (what we pin) and `1.0.0-rc2` (documented on Microsoft Learn). Not blocking — just a version-bump adjustment.
- SK 1.62 transitively pins `OpenAI = 2.2.0` exactly; `Microsoft.Extensions.AI.OpenAI 9.10-preview` requires `>= 2.5.0`. Tolerated via `NU1608` suppression in the spike (and eventually `NU1107` + `NU1605` as well in M1 when CPM made the resolver stricter).
- MAF ships native A2A protocol exposure (`AIAgent.MapA2A(...)`) as an extension in `Microsoft.Agents.AI.Hosting.A2A` — we do not need to write the A2A bridge in Phase 3.

**What's next.** Use the spike's shape for Phase 1 M1 without change: `ChatRole`, `ChatTurn`, `CompletionRequest/Response`, `ICompletionProvider`, `IAiAgent`, `StatefulAiAgent`, `SkCompletionProvider`, `MafCompletionProvider`.

---

### 2026-04-17 — Phase 1 Milestone 1 complete

**Goal.** Stand up a real OSS repo — Apache-2.0, tests, CI, deterministic build, XML docs, public-API analyzer — and productionise the spike's contracts and two adapters. VAIS2 untouched.

**What landed (`oss/agentic/`, its own git repo, tagged `v0.0.1-alpha`, commit `f98c44c`).**
- Governance: `LICENSE` (Apache 2.0), `NOTICE`, `README.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1), `SECURITY.md`.
- Build: `Directory.Build.props` (deterministic, XML docs, warnings-as-errors with targeted suppressions), `Directory.Packages.props` (CPM, transitive pinning off), `.editorconfig`.
- Solution (6 projects) built clean, 0 warnings, 0 errors:
  - `Vais2.Agents.Abstractions` — 6 public types (`ChatRole`, `ChatTurn`, `CompletionRequest`, `CompletionResponse`, `ICompletionProvider`, `IAiAgent`), zero stack deps.
  - `Vais2.Agents.Core` — `StatefulAiAgent` with logger support.
  - `Vais2.Agents.Ai.SemanticKernel` — `SkCompletionProvider` via native `IChatCompletionService`.
  - `Vais2.Agents.Ai.MicrosoftAgentFramework` — `MafCompletionProvider` via `ChatClientAgent`.
  - `tests/Vais2.Agents.Core.Tests` — 11 xUnit tests with `FluentAssertions`, fake provider, no network. **11/11 green.**
  - `samples/HelloAgent` — runs both adapters against OpenAI. **Verified end-to-end** on the provided key: both produced "The capital of France is Paris." / "The Seine River runs through Paris." confirming history propagation on both paths.
- CI: `.github/workflows/ci.yml` — build + test on ubuntu-latest and windows-latest.

**Surprises / decisions forced.**
- One parity test failed first run because `StatefulAiAgent` was passing the live `List<ChatTurn>` to the adapter by reference; subsequent turns mutated it and the test saw the wrong counts. **Fixed:** `AskAsync` now passes `_history.ToArray()` — an immutable snapshot. Real design improvement, not just a testing quirk: adapters cannot race on or mutate our state.
- Central Package Management with `CentralPackageTransitivePinningEnabled=true` propagated the `OpenAI 2.5.0` pin into the SK adapter's graph (which does not reference MEAI.OpenAI directly) and produced `NU1107` "version conflict" errors. **Fixed:** turned off transitive pinning; dependencies resolve naturally.
- `GenerateDocumentationFile` evaluated true for the test project because `Directory.Build.props` is imported before the csproj sets `IsTestProject=true`. **Fixed:** explicit `<GenerateDocumentationFile>false</GenerateDocumentationFile>` in the test and sample csprojs.
- The `Microsoft.CodeAnalysis.PublicApiAnalyzers` package version `3.11.0-beta1.24629.1` doesn't exist on nuget.org (only `.2`); bumped.

**What's deferred (explicitly not done).**
- Orleans host, in-memory runtime, observability package, persistence providers, tool-calling, multi-agent orchestrator, VectorData RAG, formal parity-test suite, docs site, release pipeline, issue/PR templates, CODEOWNERS.
- Public API analyzer is wired but in advisory-only mode (`RS0016/…` silenced); `PublicAPI.*.txt` files are empty placeholders. M2 baselines them.
- VAIS2 migration (W1 + W9) — still not touched in this effort.

**What's next.** See §7. First M2 task: add `IUsageSink` + the other missing neutral contracts.

---

### 2026-04-17 — Phase 1 Milestone 2a complete

**Goal.** Land the cross-cutting plumbing and the rest of the foundational contracts in the OSS repo: telemetry sink, context accessor, filter pipeline, resilient execution, in-memory host, and enforced public-API baselines. VAIS2 untouched.

**What landed (`oss/agentic/`, commit `93f64ea`, tagged `v0.0.2-alpha`).**
- **New abstractions** (all in `Vais2.Agents.Abstractions`):
  - `UsageRecord` + `IUsageSink` — neutral generic sink for token/cost telemetry. Implementations must not throw.
  - `AgentContext` + `IAgentContextAccessor` — replaces VAIS2's `RequestContext.Get("UserId"/…)` pattern.
  - `IAgentFilter` — ordered middleware around each turn (before/after completion). Stack-neutral replacement for SK's per-kernel filter attach.
  - `IAgentRuntime` — host-level contract for addressing agents by stable id.
- **New Core types** (`Vais2.Agents.Core`):
  - `NullUsageSink` (default singleton), `AsyncLocalAgentContextAccessor` (with disposable `Push` scoping).
  - `StatefulAgentOptions` — DI-friendly construction record (`AgentName`, `SystemPrompt`, `Filters`, `UsageSink`, `ContextAccessor`, `ResiliencePipeline`).
  - `StatefulAiAgent` rewritten to run each turn through: `Microsoft.Extensions.Resilience` pipeline → ordered filter chain → provider → usage-sink emission (including on failure). Cancellation is treated as not-a-failure and does not emit a usage record.
- **New package**: `Vais2.Agents.Hosting.InMemory` with `InMemoryAgentRuntime` (`ConcurrentDictionary`-backed, per-id options factory) and `AddInMemoryAgentRuntime()` DI extension.
- **Public API surface enforced**: `.editorconfig` flipped RS0016/RS0017/RS0025/RS0026/RS0037 to warning; **171 entries** baselined into `PublicAPI.Unshipped.txt` across five src projects (128 Abstractions, 28 Core, 4 SK adapter, 4 MAF adapter, 7 Hosting.InMemory). Build now fails on any undeclared addition/removal to the public surface.
- **ADR 0001**: keyed `IChatClient` DI convention — colon-delimited string keys (`openai:gpt-4o:primary`). Library code does not register clients; consumers do.
- **Tests**: 22/22 green (was 11/11). New coverage: usage-sink invocation on success and failure, sink-failure is swallowed, context flows into usage record, AsyncLocal scope restoration, filter registration order (`f1 → f2 → provider → f2 → f1`), filter short-circuit, resilience pipeline retry success, `InMemoryAgentRuntime` caching + isolation.
- **HelloAgent** re-verified end-to-end against OpenAI — no regression from the added pipeline.

**Surprises / decisions forced.**
- The public-API analyzer path turned out to be the most friction-heavy part. `TreatWarningsAsErrors=true` + RS0016 means every undeclared public API fails the build. We had to do a one-shot `dotnet build -p:TreatWarningsAsErrors=false`, extract all 171 entries via a throwaway Python script, write them into five `PublicAPI.Unshipped.txt` files, remove the script, and rebuild strict. Works cleanly going forward — any new public symbol now prints exactly the line to add.
- We picked `string` keys over a structured key record for DI (ADR 0001). The structured-record ergonomics win is small; the coupling it would add across every consumer is not.
- Choosing `Microsoft.Extensions.Resilience` over raw Polly: Resilience is built on Polly v8 and wraps it in the `IOptions<>` / DI-friendly `ResiliencePipelineProvider` pattern that matches the rest of the `.NET 9` stack. The spike's "3 retries with exponential back-off" is a literal translation of what VAIS2's `AiAgent<T>.CallFunction` did — we preserved that as the default, but now every consumer can override with their own `ResiliencePipeline`.
- The decision that **cancellation is not a usage event** was a small design call: if the caller asked to stop, it's not a failed call, and we shouldn't inflate error metrics. Re-throw without reporting.
- `IUsageSink` is `ValueTask`-returning, not `Task`, because sinks like `NullUsageSink` should allocate nothing. Heavy sinks (DB, HTTP) queue internally and return `default`.

**What's deferred (explicitly not done).**
- Observability package itself (`Vais2.Agents.Observability.OpenTelemetry` + `.Langfuse`) — M2b.
- `IKernelConfigurator` / `IAgentConfigurator`, `IToolRegistry` + `ITool`, `IAgentEventBus`, `IAgentStateStore` — M2c (tool-calling parity).
- Formal `Vais2.Agents.ParityTests` project — M2c.
- Orleans host, persistence providers, VectorData — M3.
- VAIS2 migration (W1 + W9) — still out of scope for this effort.

**What's next.** See §7. First M2b task: design the OTel activity source + meter names and write the `OpenTelemetryUsageSink`.

---

### 2026-04-17 — Phase 1 Milestone 2b complete

**Goal.** Make `Vais2.Agents` observable via OpenTelemetry using the GenAI semantic conventions, ship the Langfuse enricher as a first-party opt-in package, and emit a per-turn Activity from `StatefulAiAgent` so every downstream exporter (Jaeger, Tempo, Datadog, Langfuse) lights up without consumers writing any glue.

**What landed (`oss/agentic/`, tagged `v0.0.3-alpha`).**
- **New package `Vais2.Agents.Observability.OpenTelemetry`**:
  - `OpenTelemetryUsageSink : IUsageSink` — emits `gen_ai.client.token.usage` (histogram, unit `{token}`, split by `gen_ai.token.type=input|output`) and `gen_ai.client.operation.duration` (histogram, unit `s`, decorated with `error.type` on failure). Common dimensions: `gen_ai.system`, `gen_ai.response.model`, `gen_ai.operation.name`.
  - `AgenticOpenTelemetryExtensions` — `TracerProviderBuilder.AddAgenticInstrumentation()`, `MeterProviderBuilder.AddAgenticInstrumentation()`, `IServiceCollection.AddAgenticOpenTelemetrySink()`.
  - Transitively pulls `OpenTelemetry 1.13.1` (matches what VAIS2 uses).
- **New package `Vais2.Agents.Observability.Langfuse`**:
  - `LangfuseEnrichmentFilter : IAgentFilter` — stack-neutral replacement for VAIS2's SK-bound filter. Reads `IAgentContextAccessor` (instead of Orleans `RequestContext`) and tags the active Activity with `langfuse.user.id`, `langfuse.session.id`, `langfuse.trace.name`, `langfuse.trace.metadata.*`, and `langfuse.tags`.
  - `LangfuseEnrichmentOptions` — default tags, static metadata, anonymous-user fallback.
  - `AgenticLangfuseExtensions.AddLangfuseEnrichment()` DI helper.
- **Core additions** (`Vais2.Agents.Core`):
  - `AgenticDiagnostics` — `ActivitySourceName` / `MeterName` constants (`"Vais2.Agents"`) plus the shared `ActivitySource` instance.
  - `AgenticTags` — centralised tag names (GenAI + `vais2.*` extensions) so renames are single-file changes.
  - `AgenticMetrics` — instrument-name constants.
  - `StatefulAiAgent` now starts a `chat` Activity per turn, kind `Client`, zero-allocation when no listener is attached. On end it sets `gen_ai.response.model`, `gen_ai.usage.input_tokens/output_tokens`, `ActivityStatusCode.Ok` on success, `Error` + `error.type` on failure. Displays as `chat <model>` once the model is known.
- **ADR 0002** (`docs/adr/0002-otel-genai-conventions.md`) — pins the naming decision: GenAI conventions verbatim, `Vais2.Agents` source/meter name, `vais2.*` for our extensions (not a replacement for GenAI tags).
- **PublicAPI baselines**: two new `PublicAPI.Unshipped.txt` (27 OTel + 26 Langfuse entries). `AgenticDiagnostics`, `AgenticTags`, `AgenticMetrics` added to Core's baseline. Strict build still green.
- **New test project `tests/Vais2.Agents.Observability.Tests`** — 11/11 green. Covers:
  - Activity emission on success (GenAI tags + status Ok + `chat <model>` display name).
  - Activity emission on failure (status Error + `error.type`).
  - Context propagation (user/tenant/correlation/agent name tags).
  - `OpenTelemetryUsageSink` instrument shape (`long` tokens + `double` duration + dimensions) on success, on failure (carries `error.type`), and when provider reported no tokens (duration only).
  - Langfuse enricher: context → langfuse tags; anonymous-user fallback; static metadata; no-throw when no Activity is current.
- **Total tests: 33/33 green** (22 Core + 11 Observability).

**Surprises / decisions forced.**
- `ActivitySource` is process-global. First test run failed with a confusing `KeyNotFoundException`: a parallel test's activity leaked into the listener under test, and the assertion picked up the alien span. Fixed by disabling xUnit test parallelisation in the Observability test assembly (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`). Unit tests are fast — this was the right trade-off. Noted for future test projects that listen to global diagnostic sources.
- Records and the PublicAPI analyzer don't play nicely: `record` types auto-synthesise `<Clone>$()`, copy ctor, `Equals`, `GetHashCode`, `ToString`, operators. Every one of them counts as a public symbol and must be baselined. We went through this with the Abstractions records in M2a; needed to repeat it for `LangfuseEnrichmentOptions`. For sealed records the copy-constructor is omitted — caught that by trying to baseline it and getting RS0017 back.
- Chose `OpenTelemetry 1.13.1` (matching VAIS2's version) rather than the latest preview or the minimal `OpenTelemetry.Api`. The package gives us `TracerProviderBuilder` and `MeterProviderBuilder` in one import, and matching VAIS2's version means both halves of the eventual W9 migration resolve to the same graph.
- Decided that `Activity.DisplayName` gets set *after* the provider returns, rebranding the span from `"chat"` → `"chat <model>"`. This keeps span-start cheap (no wait for the model id) but makes the trace UI human-readable. Trade-off: exporters that sample at span-start only see `"chat"`. Good enough for now; we pay that cost once per turn.
- Deliberately chose **not** to emit per-message events on the span (one per `ChatTurn`). One span per `AskAsync` is the correct unit; per-message detail is consumer choice via their own `IAgentFilter`. Documented in ADR 0002.
- `LangfuseEnrichmentOptions` is `record` with `init`-only properties. Dropped a `configure` `Action<T>` overload on the DI extension because it couldn't mutate `init` properties; settled on `AddLangfuseEnrichment(services, options?)` — more explicit, no lambda sugar.

**What's deferred (explicitly not done).**
- Tool-calling contracts (`IToolRegistry` / `ITool`) and `IKernelConfigurator` / `IAgentConfigurator` — M2c.
- Formal parity-test project — M2c.
- Orleans host, persistence providers, VectorData — M3.
- VAIS2 migration (W1 + W9) — still out of scope.
- Bridging VAIS2's current `ActivityListener` bootstrap into an `AddAgenticUsageSink()`-style helper — W9 (consumer-side migration).

**What's next.** See §7. First M2c task: design `IToolRegistry` + `ITool` contracts in Abstractions so both SK's `KernelPlugin` and MAF's `ChatClientAgent.Tools` can bind identically.

---

### 2026-04-18 — Phase 1 Milestone 2c complete

**Goal.** Ship stack-neutral tool-calling. Define an `ITool` contract that means the same thing on SK and MAF, wire it through `StatefulAiAgent`, prove both adapters converge on the same bridge, and stand up a formal parity-test project that catches drift.

**What landed (`oss/agentic/`, tagged `v0.0.4-alpha`).**
- **New contracts in `Vais2.Agents.Abstractions`:**
  - `ITool` — `Name`, `Description`, `JsonElement ParametersSchema`, `Task<string> InvokeAsync(JsonElement arguments, CancellationToken)`. JSON Schema is the wire-format, returned strings feed back to the model.
  - `IToolRegistry` — `Tools` + `GetByName`. Read-only; composition is the consumer's job.
  - `CompletionRequest.Tools` — optional `IReadOnlyList<ITool>?` record property, serialised turn-by-turn.
- **Core plumbing (`Vais2.Agents.Core`):** `StatefulAgentOptions.ToolRegistry` + `StatefulAiAgent` attaches the registry's tool list to every `CompletionRequest`. Default (null registry) behaves exactly as before.
- **SK adapter — `SkToolBinder` (internal):** builds a `KernelPlugin` from `IReadOnlyList<ITool>` by wrapping each tool in a small `AIFunction` subclass (`ToolAsAiFunction`) and calling MEAI's `AIFunction.AsKernelFunction()`. `SkCompletionProvider.CompleteAsync` now clones the kernel per-turn when tools are present and sets `FunctionChoiceBehavior.Auto()` so SK auto-invokes.
- **MAF adapter — `MafToolBinder` (internal):** same `AIFunction` bridge, published directly as `ChatOptions.Tools`. `MafCompletionProvider` now wraps the injected `IChatClient` with `UseFunctionInvocation()` at construction so auto-invocation works regardless of whether tools arrive on any given turn.
- **ADR 0002 addendum:** the bridge path through `AIFunction` is the shared substrate. Both binders are ~40 lines of adapter code each, not duplicated MEAI reinventions.
- **New test project `tests/Vais2.Agents.ParityTests`** — 5 tests, all green:
  - MAF end-to-end: `StatefulAiAgent` + `MafCompletionProvider` + scripted `IChatClient` that returns a tool-call then a final text message → tool invoked once with correct JSON args, final reply surfaced.
  - SK binding inspection: `SkToolBinder.BuildPlugin` produces a `KernelPlugin` with the right function name, description, and `KernelFunction` count.
  - SK round-trip: invoking the produced `KernelFunction` via `kernel.InvokeAsync(...)` calls through to `ITool.InvokeAsync` with JSON-marshalled arguments.
  - MAF round-trip: invoking the produced `AIFunction.InvokeAsync` calls through to `ITool.InvokeAsync` with matching arguments.
  - Identity parity: the same `ITool` produces matching `Name` / `Description` on both binders.
- **Internal-visibility**: `InternalsVisibleTo("Vais2.Agents.ParityTests")` added to both adapter projects so the parity tests can inspect the internal binders without promoting them to public API. Binders stay internal — they're implementation detail, not consumer surface.
- **PublicAPI baselines**: Abstractions gained `ITool`, `IToolRegistry`, and the expanded `CompletionRequest` ctor / `Deconstruct` / `Tools` property. Core gained `StatefulAgentOptions.ToolRegistry`. Strict build clean.
- **Total tests: 38/38 green** (22 Core + 11 Observability + 5 ParityTests).

**Surprises / decisions forced.**
- **Biggest finding: auto-invocation lives at different layers in SK vs MAF.** MAF's `FunctionInvokingChatClient` is a generic pipeline layer wrapping any `IChatClient`, so a scripted fake `IChatClient` auto-invokes out of the box. SK's auto-invocation, on the other hand, is a responsibility of the concrete chat-completion connector (e.g. `OpenAIChatCompletionService`). A raw `IChatCompletionService` test double does *not* auto-invoke — which is why the SK end-to-end scenario originally failed when we wrote it. Captured in the `ToolCallingParityTests` docstring as a real parity finding. Practical consequence for consumers: if you bring a custom `IChatCompletionService` to SK, you own tool-call handling; MAF's path is stack-agnostic.
- We decided early to collapse both adapters onto one bridge through MEAI's `AIFunction` rather than writing two separate `ITool` → stack translations. MEAI is already a transitive dep of both adapters; going through `AIFunction` means SK picks up any schema-marshalling improvements shipped by MEAI for free.
- `SkToolBinder` clones the kernel per-turn before attaching the plugin. The alternative — mutating `_kernel.Plugins` directly — breaks concurrent `CompleteAsync` callers. Clone is ~50ns; we prefer correctness.
- Wrapped `IChatClient` with `UseFunctionInvocation()` in `MafCompletionProvider`'s constructor unconditionally, even when no tools are attached. `FunctionInvokingChatClient` is a passthrough when `ChatOptions.Tools` is empty, so the cost is one allocation at construction — not per-turn.
- We dropped the planned `IKernelConfigurator` / `IAgentConfigurator` contracts from M2c scope. The `ITool` + `IToolRegistry` surface does the job; per-adapter kernel configurators would let consumers inject arbitrary SK filters or MAF middleware, which isn't a Phase-1 requirement. Deferred to M3 with a note in W3.
- The parity project uses `InternalsVisibleTo` instead of making the binders public. Binders are translation plumbing — not a surface we want to support as API. External consumers who need a custom binding can subclass `AIFunction` themselves, same as we do.

**What's deferred (explicitly not done).**
- `IKernelConfigurator` / `IAgentConfigurator` — pushed from M2c to M3 (see above).
- Multi-agent orchestrator, streaming responses, `IAgentEventBus`, `IAgentStateStore` — M3.
- Orleans host, persistence providers, VectorData RAG — M3.
- A built-in tool library (`Vais2.Agents.Tools`: DuckDuckGo, HTTP, etc.) — post-M3, when a consumer actually asks for it.
- Streaming parity. MAF supports streaming via `IAsyncEnumerable<ChatResponseUpdate>`; SK via `GetStreamingChatMessageContentsAsync`. Our `ICompletionProvider` is non-streaming today. Streaming gets its own slice.
- VAIS2 migration (W1 + W9) — still out of scope.

**What's next.** See §7. Phase 1 is feature-complete; the API-freeze sweep (shift `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt`, cut local `0.1.0-preview` packages, dog-food from `HelloAgent`) is the bridge into M3. First M3 task: sketch the `Vais2.Agents.Hosting.Orleans` grain base so we can validate the neutral `StatefulAiAgent` reuses cleanly inside a grain.

---

### 2026-04-18 — API-freeze sweep, step 1 (`PublicAPI.Shipped` promotion)

**Goal.** Freeze the Phase-1 public surface. Move every baselined entry from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` across the seven Phase-1 packages so that future additions land as explicit unshipped deltas rather than silently joining a floating baseline. No code change, no behaviour change — this is a governance step before cutting `0.1.0-preview`.

**What landed (`oss/agentic/`).**
- All seven Phase-1 packages had their full public surface promoted to `PublicAPI.Shipped.txt`, and `PublicAPI.Unshipped.txt` reset to just `#nullable enable`. Counts per package (entries, excluding the `#nullable enable` header):
  - `Vais2.Agents.Abstractions` — 139 entries (neutral contracts + records: `ICompletionProvider`, `IAiAgent`, `IAgentRuntime`, `IAgentContextAccessor`, `IAgentFilter`, `IUsageSink`, `ITool`, `IToolRegistry`, records `AgentContext`/`ChatTurn`/`CompletionRequest`/`CompletionResponse`/`UsageRecord` and their synthesised members).
  - `Vais2.Agents.Core` — 51 entries (`StatefulAiAgent`, `StatefulAgentOptions`, `AsyncLocalAgentContextAccessor`, `NullUsageSink`, `AgenticDiagnostics` / `AgenticTags` / `AgenticMetrics` constants).
  - `Vais2.Agents.Ai.SemanticKernel` — 4 entries (`SkCompletionProvider` ctor/method/property surface).
  - `Vais2.Agents.Ai.MicrosoftAgentFramework` — 5 entries (`MafCompletionProvider` ctor/method/property surface).
  - `Vais2.Agents.Hosting.InMemory` — 8 entries (`InMemoryAgentRuntime` + `AgenticHostingInMemoryServiceCollectionExtensions`).
  - `Vais2.Agents.Observability.OpenTelemetry` — 9 entries (`OpenTelemetryUsageSink`, `AgenticOpenTelemetryExtensions`).
  - `Vais2.Agents.Observability.Langfuse` — 27 entries (`LangfuseEnrichmentFilter`, `LangfuseEnrichmentOptions` record, `LangfuseTags`, `AgenticLangfuseExtensions`).
- Total: ≈243 entries shipped. Tool-calling binders (`SkToolBinder`, `MafToolBinder`) deliberately remain `internal` — they're implementation plumbing reachable from `Vais2.Agents.ParityTests` via `InternalsVisibleTo` only, so no baseline entry.
- Strict build: clean (0 warnings, 0 errors). Tests: 38/38 green (22 Core + 11 Observability + 5 Parity). No public symbol drifted in the process.

**Surprises / decisions forced.**
- The move is a pure text shuffle — no build regressions, no code edits, no test changes. The only risk was missing `#nullable enable` on the Shipped file; each resulting file starts with it, which the analyzer requires. (Miss it and you get RS0041.)
- Record-synthesised members (`<Clone>$`, `Equals(T?)`, `Equals(object?)`, `GetHashCode`, `ToString`, `operator ==/!=`) make up a disproportionate chunk of the Abstractions count. That's fine — they're genuinely part of the public contract for anonymous-record-style consumption and must be frozen alongside the property set.
- Kept the `PublicAPI.Unshipped.txt` files as `#nullable enable`-only stubs rather than deleting them. The analyzer requires both files to exist; deleting `Unshipped` would fire `RS0024`. Empty-but-present is the correct steady state.

**What's deferred (explicitly not done in this step).**
- Cutting local `0.1.0-preview` NuGet packages — step 3 of the sweep.
- Dog-fooding the Shipped surface from `HelloAgent` (add a tool-calling sample variant + confirm no awkward ergonomics) — step 2 of the sweep.
- Publishing preview packages anywhere — we hold those locally until the design-partner round.

**What's next.** See §7. The API-freeze sweep now has two remaining steps before M3: (1) add a tool-calling variant to `HelloAgent` to dog-food `ITool` / `IToolRegistry` / `StatefulAgentOptions.ToolRegistry` end-to-end (flag any ergonomics before the surface is live), and (2) `dotnet pack` into a local feed and consume from a throwaway .NET 9 console app to prove the seven packages stand on their own.

---

### 2026-04-18 — API-freeze sweep, steps 2 + 3 (HelloAgent dog-food + local `0.1.0-preview` pack)

**Goal.** Finish the API-freeze sweep. Prove the Shipped Phase-1 surface is ergonomic enough to build an agent against by extending `HelloAgent` to exercise tool-calling end-to-end, then produce `0.1.0-preview` NuGet packages into a local feed and have a throwaway .NET 9 console app consume every package through normal `<PackageReference>` flows. No external publishing — we sit on preview locally until a design-partner round.

**What landed (`oss/agentic/`).**
- **`HelloAgent` tool-calling segment.** Added a third scenario after the existing SK / MAF chat walkthroughs that registers a trivial `RollDiceTool : ITool` and a 10-line `InMemoryToolRegistry : IToolRegistry`, then runs `StatefulAiAgent.AskAsync("Roll a die for me and tell me what you got.")` through both stacks with `StatefulAgentOptions.ToolRegistry` set. Same agent class, same options shape, two stacks — binder plumbing stays out of the sample entirely. Compile clean (`dotnet build samples/HelloAgent -clp:ErrorsOnly` — 0W/0E). The consumer writes ~30 lines of tool + registry code and nothing else to get end-to-end tool-calling on both SK and MAF.
- **Local `0.1.0-preview` packages.** `dotnet pack Vais2.Agents.sln -c Release -p:VersionPrefix=0.1.0 -p:VersionSuffix=preview -o artifacts/packages` produced seven `.nupkg` + seven matching `.snupkg` (symbol) files for the packable projects. Tests and `HelloAgent` are `IsPackable=false` so they didn't show up in the output. No pack warnings, no missing-XML-doc complaints.
- **Consumer smoke test at `oss/agentic/artifacts/smoketest/`** (gitignored). Contains `SmokeTest.csproj` referencing all seven Vais2 packages with explicit `Version="0.1.0-preview"`, a `NuGet.config` that uses package-source-mapping (`Vais2.Agents.*` → local feed `../packages`; everything else → `nuget.org`), and stub `Directory.Build.props` / `Directory.Packages.props` files that halt the upward MSBuild walk so the smoke test behaves like any third-party consumer rather than inheriting `TreatWarningsAsErrors=true` / central-package-management / experimental-flag suppression from the repo. `Program.cs` constructs a representative type from each of the seven packages — `ITool`, `IToolRegistry`, `AgentContext`, `CompletionRequest`, `StatefulAiAgent`, `AgenticDiagnostics` constants, `SkCompletionProvider`, `MafCompletionProvider`, `InMemoryAgentRuntime`, `OpenTelemetryUsageSink`, `LangfuseEnrichmentFilter` — and prints each provider's name + a proof line. `dotnet restore` + `dotnet build -c Release` + `dotnet run` succeeded end-to-end and printed the expected output. The seven packages stand on their own on a plain NuGet install.
- **38/38 tests still green** after both steps.

**Surprises / decisions forced.**
- **Dog-food finding #1: `Vais2.Agents.ChatRole` collides with `Microsoft.Extensions.AI.ChatRole`.** Any consumer file that imports both `Vais2.Agents` (for our records) and `Microsoft.Extensions.AI` (to build a custom `IChatClient` for MAF, which is a totally reasonable thing to do) sees `CS0104` at every `ChatRole.*` use. Ours is an enum, MEAI's is a struct. The smoke test works around it with fully-qualified names. **Decision: accept for 0.1.0-preview.** Consumers who hit it get a clear error and a 1-line fix (`using AgentChatRole = Vais2.Agents.ChatRole;`). A rename (e.g. `AgentChatRole`) would be the right long-term fix but is a breaking change — defer to 0.2 and gather design-partner feedback first. README will flag the collision in the SK/MAF setup section.
- **Dog-food finding #2: `SkCompletionProvider` ctor eagerly resolves `IChatCompletionService` from the `Kernel`.** A bare `new Kernel()` (no connector) throws `KernelException` at construction rather than deferring the error to the first `CompleteAsync`. Fail-fast is the right call — better than a half-constructed provider that dies on every request — but it means "build the kernel first, then wrap it." The smoke test registers a dummy OpenAI connector (no HTTP at construction, only on invocation) to work around it. **Decision: keep the fail-fast, document it.** Worth a line in the `SkCompletionProvider` XML doc and the README's SK-setup section.
- The smoke test needed `ImportDirectoryBuildProps="false"` equivalents — MSBuild doesn't have that property, so I stubbed both `Directory.Build.props` and `Directory.Packages.props` as empty (with `ManagePackageVersionsCentrally=false` in the latter so `PackageReference Version="..."` attributes don't fight central package management). Clean way to decouple an in-repo smoke test from repo-wide MSBuild inheritance.
- `OpenTelemetryUsageSink` is `IDisposable` — the smoke test uses `using var sink = new OpenTelemetryUsageSink();` and cleans up correctly. Worth keeping in mind for the DI extension when we re-audit it: the sink's `Meter` is disposed on `Dispose()`, so registering it as a singleton in a consumer's DI container is what they want.
- No central-source-mapping work was needed for `nuget.org`: only our packages get mapped to the local feed; `Microsoft.Extensions.AI`, `Microsoft.SemanticKernel`, `OpenAI`, etc. flow from `nuget.org` as usual, which is realistic consumer behaviour.

**Commit + tag.** Committed on OSS repo `main` as `a629213` ("API freeze: 0.1.0-preview — PublicAPI shipped + HelloAgent dog-food", 15 files: 14 PublicAPI files + `samples/HelloAgent/Program.cs`), then annotated tag `v0.1.0-preview` (preserves the "first preview" marker in `git tag --list`). Neither commit nor tag is pushed — the OSS repo still lives only as a local git repo inside `oss/agentic/`. Packages also live only in the untracked `artifacts/packages/` directory.

**What's deferred (explicitly not done in this step).**
- Renaming `Vais2.Agents.ChatRole` to avoid the MEAI collision — post-preview decision (0.2.0).
- Documenting the SK-ctor fail-fast and the MEAI collision in README / XML docs — bundled into the 0.1.0-preview docs pass; can still land on top of the tag as a `v0.1.0-preview-docs` rev or simply as follow-up commits before the design-partner hand-off.
- Pushing `0.1.0-preview` packages to a public NuGet feed — held locally until a design-partner round.
- Pushing the OSS repo to GitHub — still TBD on the eventual org/name (see §4, working names).

**What's next.** Phase 1 is DONE. Optional pre-M3 docs work: bundle the two dog-food findings into a README snippet. Then start **Milestone 3** — `Vais2.Agents.Hosting.Orleans` grain base first, reusing the neutral `StatefulAiAgent` inside a grain. After M3 lands, we decide whether to push `0.2.0-preview` publicly or iterate on design-partner feedback first.

---

### 2026-04-18 — Phase 1 Milestone 3a (Orleans host)

**Goal.** Port the VAIS2-internal `Vais2Agents.Orleans` grain base over to the OSS `Vais2.Agents.Hosting.Orleans` package, reusing the neutral `StatefulAiAgent` inside a grain and providing an `IAgentRuntime` implementation backed by virtual actors. Persistence layer (Redis / Postgres) is deliberately out of scope — M3a uses `IPersistentState` with whatever grain storage provider the host wires up. Real storage providers are M3b/c.

**What landed (`oss/agentic/`, tag `v0.2.0-alpha` pending).**
- **New package `Vais2.Agents.Hosting.Orleans`** (src + tests projects added to the solution):
  - `IAiAgentGrain : IGrainWithStringKey` — async surface with `AskAsync`, `GetHistoryAsync`, `Get/SetSystemPromptAsync`, `ResetAsync`, `DeleteAsync`.
  - Sealed `AiAgentGrain` — wraps `StatefulAiAgent` and persists `(History, SystemPrompt)` to `IPersistentState<AiAgentGrainState>` under storage name `"vais2.agents"`. Each activation re-seeds the neutral agent via `StatefulAgentOptions.InitialHistory`.
  - `OrleansAgentRuntime : IAgentRuntime` + internal `OrleansAiAgentProxy : IAiAgent` — client-side adapter that forwards to the grain and caches `History` / `SystemPrompt` client-side (refreshed after every `AskAsync`).
  - `OrleansAgentContextAccessor : IAgentContextAccessor` — reads Orleans `RequestContext` under the same `vais2.*` keys (`AgenticTags.UserId`, etc.) that OTel activities already use.
  - `ChatTurnSurrogate` + `AgentContextSurrogate` + `[RegisterConverter]` converters — keep `Vais2.Agents.Abstractions` strictly Orleans-free (no `Microsoft.Orleans.Serialization.Abstractions` dependency promoted to Abstractions) while still letting grain methods marshal records.
  - `AgenticHostingOrleansServiceCollectionExtensions` — `AddOrleansAgentRuntime()` for the client side, `ConfigureAgentGrains()` for the silo side.
- **Core surface extension (unshipped addition).** `StatefulAgentOptions.InitialHistory : IReadOnlyList<ChatTurn>?` lets hosts seed the neutral agent from external state (the grain's rehydration need motivated it). `StatefulAiAgent` ctor honours the seed by copying into its internal history list. Two new entries land in `PublicAPI.Unshipped.txt` for Core.
- **Test project `Vais2.Agents.Hosting.Orleans.Tests`** (12 tests, all green) using `Microsoft.Orleans.TestingHost`:
  - Single `OrleansClusterFixture` spins one `TestCluster` per collection with `AddMemoryGrainStorage("vais2.agents")` + the required silo DI; `HistorySizeProvider` is a test `ICompletionProvider` that replies `"history-size={request.History.Count}"` so provider observations double-check rehydration.
  - Grain tests: activation, round-trip history, **persistence across `IManagementGrain.ForceActivationCollection`** (the rehydration case — grain deactivates, next call reads state back from memory storage, provider sees the full pre-deactivation history), reset keeps system prompt, system-prompt persistence, delete clears state so the next activation starts fresh.
  - Runtime tests: `GetOrCreate` returns proxy and forwards to grain, same-id returns same-proxy, `Remove` evicts proxy and fires grain `DeleteAsync`, `TryGet` reports only cached proxies (never probes silo).
  - Context-accessor tests: empty `RequestContext` yields all-null fields, set values surface on `AgentContext`, non-string values are ignored (not thrown).
- **Solution + central packages updated.** Orleans 9.2.1 added to `Directory.Packages.props` (`Sdk`, `Core`, `Core.Abstractions`, `Runtime`, `Serialization.Abstractions`, `Server`, `Client`, `TestingHost`). Two new projects wired into `Vais2.Agents.sln`.
- **Total tests: 50/50 green** (22 Core + 11 Observability + 5 Parity + 12 Hosting.Orleans).

**Surprises / decisions forced.**
- **`IPersistentState` lives in `Microsoft.Orleans.Runtime`, not `Sdk`.** VAIS2's existing `Vais2Agents.Orleans` gets it transitively via `Microsoft.Orleans.Streaming`. Our hosting package doesn't need streaming for M3a, so I had to pull `Microsoft.Orleans.Runtime` explicitly. Client-only consumers pay a runtime-side dep they technically don't need — trade-off for single-package shipping; acceptable for M3a, revisitable if anyone screams.
- **Serialisation surrogates vs `[GenerateSerializer]` on Abstractions records.** The research doc §4 is explicit: "Abstractions depends on nothing — not SK, not MAF, not Orleans." So I took the VAIS2 pattern and built `ChatTurnSurrogate` / `AgentContextSurrogate` with `[RegisterConverter]`-registered converters inside the hosting package. Cost: ~40 lines of boilerplate. Benefit: Abstractions stays truly dependency-free; consumers who don't use Orleans never learn the types exist.
- **`InitialHistory` — a shipped-API extension was the right call.** Alternative was a dedicated `IChatHistoryStore` abstraction, but that needs a lot more design (partial writes, cursor semantics, multi-turn batching) and M3a doesn't need it. `InitialHistory` is a 5-line addition (one property + 3-line ctor seed) that solves exactly the grain-rehydration case. `IChatHistoryStore` still has a valid slot in M3c when Postgres persistence lands.
- **`IAiAgent` ↔ grain proxy impedance.** `IAiAgent.History` and `SystemPrompt` are synchronous properties; grain calls are always async. The proxy caches both client-side, refreshes on every `AskAsync`, and blocks synchronously on the `SystemPrompt` setter + `Reset` via `.GetAwaiter().GetResult()`. Documented caveat: only use the proxy from non-grain contexts (host services, background workers) — calling `SystemPrompt = ...` from inside *another* grain's turn would deadlock its single-threaded scheduler. Consumers inside grains should use `IAiAgentGrain` directly.
- **Sealed grain class.** Started with `public class AiAgentGrain` then switched to `sealed`. Subclassing a grain to tweak behaviour is an advanced Orleans pattern; M3a consumers who need different semantics can implement their own `IAiAgentGrain`. Seal reduced public-API surface by ~6 entries (the `virtual` member baselines).
- **PublicAPI analyzer + Orleans `OnActivateAsync` override.** The grain's `override` of `OnActivateAsync` counted as a new public member; had to add the `override` line to the baseline separately. One-time friction.
- **Grain key convention.** Each grain's string key IS the agent id. By default we set `StatefulAgentOptions.AgentName = grainKey` at activation, so telemetry (`gen_ai.*` tags + langfuse enrichment) automatically tags by grain key. Hosts can override via `ConfigureAgentGrains((sp, id) => ...)`.

**What's deferred (explicitly not done in this step).**
- **Stream-based agent events.** Old VAIS2's `Agent` base grain inherits stream publish/subscribe via `this.GetStreamProvider("StreamProvider")`. Implied in that is an `IAgentEventBus` abstraction we haven't designed yet. Punting to M3e (after persistence lands) so we can design the event surface around persistent Orleans streams.
- **Multi-agent orchestrator (`MultiAgent` port).** Old VAIS2's `MultiAgent` uses SK's `AgentGroupChat`. Neutral multi-agent requires a stack-agnostic orchestrator contract — complex, deferred to M3e or later.
- **Real persistence.** In M3a tests use `AddMemoryGrainStorage`. Redis + Postgres providers are M3b/c.
- **Cross-host parity harness.** The hypothetical "same scenario produces byte-identical outputs on InMemory and Orleans hosts" — deferred to M3e once Redis persistence is in and we can compare real-world scenarios, not just history-size smoke.
- **VectorData RAG (`AddKnowledge` equivalent).** Deferred to M3d.
- **Streaming parity** (`IAsyncEnumerable` from both adapters through the grain interface). Deferred.

**What's next.** M3b — `Vais2.Agents.Persistence.Redis`. Orleans clustering via Redis, grain storage via Redis, stream provider via Redis Streams. The M3a tests already run on `AddMemoryGrainStorage` — M3b tests would swap to Redis (via `Testcontainers.Redis` or a local `docker-compose`-started container) to verify the same grain behaviour survives an out-of-process store. Design question to flag at M3b kickoff: do we ship one `Vais2.Agents.Persistence.Redis` package that covers all three (cluster + storage + streams) or split? Leaning single package for simplicity — they share version pins and consumers rarely want one without the others.

---

### 2026-04-18 — Phase 1 Milestone 3b (Redis persistence — clustering + grain storage)

**Goal.** Get real out-of-process persistence behind `AiAgentGrain` so grain state survives across activation cycles against an actual data store, not just `AddMemoryGrainStorage`. Ship a thin `Vais2.Agents.Persistence.Redis` package wrapping Orleans' built-in Redis providers so consumers get the right storage name (`AiAgentGrain.StorageName = "vais2.agents"`) without re-typing it.

**Scope decisions taken at kickoff.**
- **Single package** covering clustering + grain storage. Split was considered; rejected because they share version pins (Orleans 9.2.1 Redis stack), consumers almost always want both together, and a third package later doesn't block a single-package start now.
- **Streams not in M3b.** Shipping a `UseAgenticRedisStreaming` before there are any neutral agent-event contracts would be build-yourself-a-brochure work. Deferred to M3e once `IAgentEventBus` / `AgentEvent` are designed.
- **Testcontainers for integration tests.** Requires Docker Desktop locally; trades CI complexity for genuine "Redis really round-trips state" coverage. Unit-test-only alternatives (e.g., asserting DI registrations) wouldn't prove anything about the integration and would give false confidence.

**What landed (`oss/agentic/`, tag `v0.3.0-alpha` pending).**
- **New package `Vais2.Agents.Persistence.Redis`.** Single public class `AgenticRedisPersistenceExtensions` with three static extensions:
  - `UseAgenticRedisClustering(ISiloBuilder, string connectionString)` — parses via `ConfigurationOptions.Parse`, delegates to Orleans' `UseRedisClustering` with pre-parsed options.
  - `UseAgenticRedisClustering(IClientBuilder, string connectionString)` — client-side twin.
  - `AddAgenticRedisGrainStorage(ISiloBuilder, string connectionString)` — registers the Redis grain-storage provider under the hardcoded `AiAgentGrain.StorageName`, so consumers don't need to know the storage-name convention.
- **Test project `Vais2.Agents.Persistence.Redis.Tests`** (5 tests, all green) with a `RedisClusterFixture : IAsyncLifetime` that:
  - Spins up `redis:7-alpine` via `Testcontainers.Redis` (ephemeral host port — coexists with the user's own Redis on :6379 without collision).
  - Builds a `TestCluster` whose silo configurator calls `AddAgenticRedisGrainStorage(containerConnectionString)` + wires `HistorySizeProvider` + `ConfigureAgentGrains`.
  - Static `CurrentConnectionString` is the simplest way to thread the Testcontainers-generated connection string into the `ISiloConfigurator.Configure` callback (configurators run without instance state from the fixture; TestClusterBuilder properties work too but require `IHostConfigurator` wiring — static handoff is ~1/3 the code).
- **Test coverage:**
  - `Ask_Writes_History_To_Redis_And_Reads_It_Back` — basic round-trip.
  - `History_Persists_Across_Activation_Collection_Backed_By_Redis` — writes two turns, forces `IManagementGrain.ForceActivationCollection(TimeSpan.Zero)`, asks a third turn, asserts the provider saw `history-size=5` (4 prior + new user) proving Redis rehydration worked.
  - `Grain_State_Materialises_As_A_Redis_Key` — connects with `StackExchange.Redis` directly, scans keys under `pattern: "*"`, asserts at least one key references the grain id. Proves the state is actually hitting Redis, not just in-memory.
  - `Delete_Clears_State_So_Reactivation_Starts_Fresh_From_Redis` — asks a turn, deletes, forces activation collection, reactivates, asserts history is empty + system-prompt is null.
  - `SystemPrompt_Persists_Via_Redis_Across_Activation_Collection` — M3a pattern ported to real Redis.
- **55/55 tests green across all five test projects** (22 Core + 11 Observability + 5 Parity + 12 Hosting.Orleans + 5 Persistence.Redis).

**Surprises / decisions forced.**
- **Orleans 9 Redis extensions live in `Microsoft.Extensions.Hosting`.** Both `UseRedisClustering` and `AddRedisGrainStorage` sit in that namespace, *not* in `Orleans.Hosting` despite operating on `ISiloBuilder` (which is in `Orleans.Hosting`). The global-usings shipped by `Microsoft.Orleans.Sdk` cover `Orleans.Hosting` + `Orleans` + `Orleans.Runtime` but *not* `Microsoft.Extensions.Hosting`. My extensions file therefore needs an explicit `using Microsoft.Extensions.Hosting;` on top. VAIS2's ApiService uses the Web SDK which imports `Microsoft.Extensions.Hosting` globally — that's why VAIS2's `siloBuilder.UseRedisClustering(...)` works with no extra using there but mine didn't. Noted for M3c when the Postgres helpers show up — they'll have the same issue.
- **Orleans' Redis `ClearStateAsync` leaves the key.** My initial `Delete_Removes_Grain_State_From_Redis` test asserted the Redis key was gone after `DeleteAsync` — it wasn't. Orleans 9's Redis grain-storage provider clears grain *content* but leaves the key structure (or writes an empty/tombstone value). Rewrote the test to assert *semantic* behaviour: `reactivation-after-delete sees empty history + null system-prompt`, which is what consumers actually care about. Implementation detail of whether `KeyDelete` or `HDEL` is issued stays provider-internal.
- **`RedisBuilder().GetConnectionString()` returns `localhost:<ephemeral-port>`** which `ConfigurationOptions.Parse` handles. No extra config plumbing needed. The `redis:7-alpine` image cold-starts in ~1.5s the first test run (pulls image), under 500ms on subsequent runs.
- **`Testcontainers.Redis` 4.0.0 API is clean.** `RedisContainer : IAsyncLifetime`-ish semantics, `StartAsync` / `DisposeAsync` handle Docker lifecycle. No surprises.

**What's deferred (explicitly not done in this step).**
- **Redis streams** (`UseAgenticRedisStreaming`) — deferred to M3e alongside `IAgentEventBus` + `AgentEvent`.
- **Multi-silo cluster tests** — TestingHost defaults to 1 silo + in-memory membership; wiring `UseRedisClustering` *inside* TestCluster is awkward and doesn't meaningfully exercise clustering since TestCluster has its own membership for test coordination. Clustering integration is effectively tested by consumers running the full silo stack (VAIS2's Aspire host does this today).
- **Tag not cut.** `v0.3.0-alpha` to be cut after this entry commits.
- **Postgres parallel** — M3c.

**What's next.** M3c — `Vais2.Agents.Persistence.Postgres`. Mirrors M3b's structure: `UseAgenticPostgresClustering` + `AddAgenticPostgresGrainStorage` wrappers over `Microsoft.Orleans.Clustering.AdoNet` + `Microsoft.Orleans.Persistence.AdoNet` (Orleans uses ADO.NET + Npgsql for Postgres), plus an optional first-class `IChatHistoryStore` abstraction for non-Orleans hosts that just want "save my chat history to Postgres" without pulling the Orleans runtime. Testcontainers.PostgreSql fixture. Design question to flag at M3c kickoff: does `IChatHistoryStore` belong in Abstractions (neutral) or Persistence.Postgres (provider-specific)? Leaning Abstractions — Postgres is just the first provider, and SQLite / CosmosDB / MongoDB future providers would all want the same interface.

---

### 2026-04-18 — Phase 1 Milestone 3c (Postgres persistence — clustering + grain storage)

**Goal.** Mirror M3b's Redis persistence for Postgres via Orleans' ADO.NET providers. Second production-ready persistence option so consumers can pick the data store they already run, not the one we prefer.

**Scope decisions taken at kickoff.**
- **`IChatHistoryStore` deferred.** My M3b milestone log leaned toward putting it in Abstractions; on reflection, deferring is better. Only one real implementation would exist right now (Postgres), and shaping a neutral interface around a single case tends to bake in the wrong abstractions. The `StatefulAgentOptions.InitialHistory` hook from M3a already solves the load-on-construct half of the need; a store-as-filter or store-as-decorator design can land later when (a) a second non-grain persistence target shows up or (b) a consumer actually asks for it. Explicit, recorded, non-blocking.
- **Grain storage + clustering, not streams.** Same principle as M3b: Orleans' Postgres streams would need a neutral `IAgentEventBus` to wrap and that design hasn't happened. Leaving a gap here is less costly than shipping a provider-specific stream helper.
- **Testcontainers.PostgreSql for integration tests.** Same trade-off as M3b — needs Docker, pays for genuine "real Postgres" coverage.

**What landed (`oss/agentic/`, tag `v0.4.0-alpha` pending).**
- **New package `Vais2.Agents.Persistence.Postgres`.** Single public static `AgenticPostgresPersistenceExtensions` with:
  - `const string NpgsqlInvariant = "Npgsql"` — Orleans' ADO.NET invariant name for Postgres. Exposed so consumers who need to build their own ADO.NET options have the canonical value without string-typing it.
  - `UseAgenticPostgresClustering(ISiloBuilder, string connectionString)` + client-side twin — wrap `UseAdoNetClustering` with `Invariant = NpgsqlInvariant` pre-set.
  - `AddAgenticPostgresGrainStorage(ISiloBuilder, string connectionString)` — wrap `AddAdoNetGrainStorage(AiAgentGrain.StorageName, ...)` with `Invariant = NpgsqlInvariant` pre-set.
- **Test project `Vais2.Agents.Persistence.Postgres.Tests`** (5 tests, all green) with a `PostgresClusterFixture : IAsyncLifetime` that:
  - Spins up `postgres:16-alpine` via `Testcontainers.PostgreSql` (ephemeral host port; coexists with the user's own Postgres on :5432).
  - Applies Orleans' Postgres schema via Npgsql before the cluster deploys — `PostgreSQL-Main.sql` + `PostgreSQL-Persistence.sql` sourced from the Orleans repo and embedded as resources in the test project (see "Surprises" below for why).
  - Builds a `TestCluster` wiring `AddAgenticPostgresGrainStorage(containerConnectionString)` via the static-connection-string handoff pattern that worked for M3b.
- **Test coverage:**
  - `Ask_Writes_History_To_Postgres_And_Reads_It_Back` — basic round-trip.
  - `History_Persists_Across_Activation_Collection_Backed_By_Postgres` — writes two turns, forces `IManagementGrain.ForceActivationCollection(TimeSpan.Zero)`, asks a third turn, asserts provider sees `history-size=5` proving Postgres-sourced rehydration.
  - `Grain_State_Appears_As_A_Row_In_OrleansStorage_Table` — connects with Npgsql directly, `SELECT COUNT(*) FROM OrleansStorage WHERE grainidextensionstring = @id`, confirms the row exists. Proves state hit the real database, not a transient in-process cache.
  - `Delete_Clears_State_So_Reactivation_Starts_Fresh_From_Postgres` + `SystemPrompt_Persists_Via_Postgres_Across_Activation_Collection` — semantic assertions, same shape as M3b's.
- **60/60 tests green across all six test projects** (22 Core + 11 Observability + 5 Parity + 12 Hosting.Orleans + 5 Persistence.Redis + 5 Persistence.Postgres).

**Surprises / decisions forced.**
- **Orleans' ADO.NET extensions live in `Orleans.Hosting`, not `Microsoft.Extensions.Hosting`.** This is the opposite of M3b's Redis finding (where the extensions lived in `Microsoft.Extensions.Hosting`). My first build had `using Microsoft.Extensions.Hosting;` copied from the Redis package — analyzer flagged it as unused (`IDE0005`) and the build told the story. Removed the using; `Orleans.Hosting` is already in the SDK's global usings so `UseAdoNetClustering` / `AddAdoNetGrainStorage` resolve without any extra import. Consistency across provider packages is a lie; always trust the compiler error.
- **Orleans' NuGet packages do not ship the Postgres SQL schema scripts** (`PostgreSQL-Main.sql`, `PostgreSQL-Persistence.sql`). They live only in the Orleans source tree on GitHub. For tests I pulled `v9.2.1` copies via `curl` and embedded them as `EmbeddedResource` in the test csproj; licence-compatible (Orleans is MIT, we're Apache 2.0, MIT redistribution is fine). Runtime consumers of `Vais2.Agents.Persistence.Postgres` still need to run Orleans' migration scripts against their database themselves — documented in the extension class's XML doc. A "ship a migration helper" package could come later if consumers ask for it; the Orleans community doesn't seem to find this objectionable today.
- **Resource names preserve dashes.** MSBuild's default name-mangling for `Sql/PostgreSQL-Main.sql` is `<RootNamespace>.Sql.PostgreSQL-Main.sql` — dash *not* replaced with underscore. I guessed wrong first time and got "embedded resource not found". Reflection `GetManifestResourceNames()` is the source of truth.
- **`OrleansStorage` table's `grainidextensionstring` column** is where the grain's string key lands (for `IGrainWithStringKey` grains). Made the key-exists assertion in `Grain_State_Appears_As_A_Row` straightforward — no parsing needed.

**What's deferred (explicitly not done in this step).**
- **`IChatHistoryStore`** — neutral abstraction for "save chat history to X" without the Orleans runtime. Revisit when a second provider case shows up.
- **SQL schema auto-provisioning helper.** A `InitializeAgenticPostgresSchemaAsync(connectionString)` convenience would spare consumers running migrations themselves; keeping it out of M3c because (a) consumers running Orleans in production already do migrations, (b) Orleans itself doesn't ship this, and (c) it's a small additive feature any consumer can add themselves with the 222 lines of SQL already in the Orleans repo.
- **Multi-silo Postgres clustering test.** Same rationale as M3b — `TestCluster` manages its own membership for coordination, so driving `UseAgenticPostgresClustering` inside it is awkward and doesn't test much. Clustering integration is proven by real deployments (VAIS2's Aspire silo host uses the same Orleans machinery).
- **`Microsoft.Orleans.Reminders.AdoNet`** — a third ADO.NET package exists for reminder persistence. Not wrapped here because reminders aren't used by `AiAgentGrain`; can ship later if consumers need them.
- **Tag not cut.** `v0.4.0-alpha` to be cut after this entry commits.

**What's next.** M3d — `Vais2.Agents.Persistence.VectorData`. `Microsoft.Extensions.VectorData`-backed `IKnowledgeRetriever` for RAG (§4 decision 7). Works for both SK and MAF adapters (MEAI is the shared substrate). Design question to flag at M3d kickoff: does `IKnowledgeRetriever` go in Abstractions or in a dedicated `Vais2.Agents.Rag` package that also handles the `StatefulAiAgent` integration (probably as an `IAgentFilter`)? Leaning "Abstractions for the contract, Persistence.VectorData for the `Microsoft.Extensions.VectorData`-backed implementation, and a filter-based integration in Core" — three-layer, mirrors how tool-calling landed.

---

### 2026-04-18 — Phase 1 Milestone 3d (VectorData-backed RAG)

**Goal.** Port VAIS2's `AddKnowledge` RAG pattern to the neutral library against `Microsoft.Extensions.VectorData`, so consumers can wire any supported vector store (in-memory, Qdrant, pgvector, Azure AI Search, …) and have it augment agent turns without touching `StatefulAiAgent`, without adding any Core knowledge-of-RAG, and without breaking stack neutrality.

**Scope decisions taken at kickoff (all three greenlit upfront).**
- **Three layers.** Contract in `Vais2.Agents.Abstractions` (`IKnowledgeRetriever` + `KnowledgeChunk`); MEAI-backed retriever + a filter-based integration in `Vais2.Agents.Persistence.VectorData`. Mirrors the M2c tool-calling split (neutral `ITool` in Abstractions, adapter binders in SK/MAF packages).
- **Filter-based integration, not a new Core API.** `KnowledgeRetrievalFilter : IAgentFilter` plugs into the M2a filter pipeline. Zero changes to `StatefulAiAgent`, zero new `StatefulAgentOptions` properties. Consumers insert the filter wherever in the chain they want retrieval to happen (typically outermost after Langfuse enrichment).
- **SystemPrompt augmentation, not synthetic history turns.** Retrieved context joins the request's `SystemPrompt`; `IAiAgent.History` stays pristine (tracks real conversation only). Consumers who want the context surfaced differently wrap the filter or roll their own.

**What landed (`oss/agentic/`, tag `v0.5.0-alpha` pending).**
- **Abstractions (unshipped additions).** `IKnowledgeRetriever.RetrieveAsync(string query, int topK = 5, CT) -> Task<IReadOnlyList<KnowledgeChunk>>` and `record KnowledgeChunk(string Text, string? Id = null, float? Score = null)`. Minimal, no embedding-generator reference in the contract — providers own their embedding choice.
- **New package `Vais2.Agents.Persistence.VectorData`:**
  - `VectorStoreKnowledgeRetriever<TKey, TRecord>` — takes `VectorStoreCollection<TKey, TRecord>` + `IEmbeddingGenerator<string, Embedding<float>>` + a `Func<TRecord, KnowledgeChunk>` projection. Embeds the query, calls `collection.SearchAsync(embedding.Vector, top: topK, options: null, CT)`, runs the projection over each result, and overrides each chunk's `Score` with the search-result's score via record `with`.
  - `KnowledgeRetrievalFilter` — pulls the most recent `ChatRole.User` turn from `request.History`, calls the retriever, formats chunks via a `KnowledgeRetrievalOptions.Template` (default: `"Relevant context:\n{chunks}"` with `{chunks}` expanding to the chunks joined by `\n---\n`), creates an augmented request via `request with { SystemPrompt = ... }`, and forwards to `next`. Stateless per-call — no cross-turn caching.
  - `KnowledgeRetrievalOptions` — `TopK` (default 5), `Template`, `ChunkSeparator`.
- **Test project `Vais2.Agents.Persistence.VectorData.Tests`** (10 tests, all green):
  - 4 retriever tests over `Microsoft.SemanticKernel.Connectors.InMemory 1.63.0-preview` + deterministic `HashEmbeddingGenerator` — exact-match retrieval, projection round-trip + score annotation, topK bound, zero/negative topK returns empty.
  - 6 filter tests (pure unit tests, no vector store) — context injection, no-user-turn passthrough, zero-chunks passthrough, null-SystemPrompt replacement, custom template/separator, "latest user turn wins" when history has assistant in between.
- **70/70 tests green across all seven test projects** (22 Core + 11 Observability + 5 Parity + 12 Hosting.Orleans + 5 Persistence.Redis + 5 Persistence.Postgres + 10 Persistence.VectorData).

**Surprises / decisions forced.**
- **MEAI 9.10 has no matching VectorData.** The VectorData.Abstractions package versions on NuGet: `9.0.0-preview.*` → `9.5.0` → `9.6.0` → `9.7.0` → `10.0.0+`. There is *no* `9.10.0`. The 10.x line requires `Microsoft.Extensions.AI.Abstractions >= 10.2.0`, which conflicts with our 9.10 MEAI pin (tied to the SK 1.62 ↔ OpenAI 2.5 balancing act we've been nursing since M1). Pinned VectorData at `9.7.0` — the latest stable that works with MEAI 9.10. Bumping the whole MEAI stack to 10.x is a bigger migration, deferred until it has a reason beyond "align with latest VectorData."
- **SK InMemory connector is preview-only.** `Microsoft.SemanticKernel.Connectors.InMemory` has no stable release; nearest release to our SK 1.62.0 stable is `1.63.0-preview`. Used for tests only, so the preview dependency doesn't leak into the shipped package.
- **`VectorStoreCollection`, not `IVectorStoreRecordCollection`.** VectorData 9.7's concrete type is `VectorStoreCollection<TKey, TRecord>` (abstract class), not the older interface name. The class provides `SearchAsync(vector, top, options, CT)` returning `IAsyncEnumerable<VectorSearchResult<TRecord>>`.
- **VectorData record attributes renamed.** Old SK attributes were `[VectorStoreRecordKey/Data/Vector]`; current MEAI attributes are `[VectorStoreKey/Data/Vector]` (no "Record" prefix). The 9.7 package shipped the new names only.
- **`VectorSearchResult.Score` is `double?`, not `float?`.** `KnowledgeChunk.Score` is `float?` to keep the Abstractions surface small, so the retriever casts explicitly: `(float?)result.Score`.
- **PublicAPI generic-parameter nullability.** For `VectorStoreKnowledgeRetriever<TKey, TRecord> where TRecord : class`, the analyzer wants `TRecord!` in baselined parameter types (the constraint makes it a reference type, so `!`-annotated). First baseline try used `TRecord` without the `!` and got RS0016/RS0017. Same pattern we saw with Orleans surrogates in M3a — record-constrained generics always non-null in baselines.
- **Fake embedder SHA256 has no semantic-similarity property.** Hashing makes vectors *deterministic* but *uncorrelated* between similar inputs — "Tell me about X" and "X is …" produce vectors that are essentially random relative to each other. First version of the "retriever returns the relevant doc" test failed because cosine similarity between unrelated SHA256 vectors put the wrong doc on top. Rewrote the test to query with exact-match text (so the vectors are identical and cosine similarity = 1). The retriever's *contract* we need to verify is "ask the store, project results" — not "semantic search works" (that's the real embedder's job, out of our scope).

**What's deferred (explicitly not done in this step).**
- **Convenience DI extension methods.** No `AddAgenticVectorStoreRetrieval(IServiceCollection, ...)` because consumers bringing their own `VectorStoreCollection<TKey, TRecord>` + embedding generator is bespoke enough that a one-liner helper would hide more than it reveals. Wiring is three lines by hand; helpers can come when patterns settle.
- **Caching decorator** for `IKnowledgeRetriever`. Consumers who want it can wrap the retriever; filter stays stateless by design.
- **Auto-ingestion.** Loading documents *into* the vector store is a consumer concern — MEAI's `IEmbeddingGenerator` + `VectorStoreCollection.UpsertAsync` is the idiomatic path. We don't ship ingestion helpers.
- **MEAI 10.x migration.** When it makes sense (e.g., a consumer wants the newer VectorStore APIs), bump the whole MEAI/SK/OpenAI stack together.
- **Tag not cut.** `v0.5.0-alpha` after this entry commits.

**What's next.** M3e — extended parity. This closes out the original M3 plan. Concretely: (1) a cross-host harness that runs the same scenario on `InMemoryAgentRuntime` and on `OrleansAgentRuntime + Redis|Postgres grain storage` and asserts equivalent outputs (with the non-determinism of the LLM abstracted away via a deterministic provider like `HistorySizeProvider`); (2) streaming parity once we add an `IStreamingCompletionProvider` surface — both SK and MAF expose `IAsyncEnumerable<ChatResponseUpdate>` but our current `ICompletionProvider` is non-streaming; (3) multi-agent orchestrator neutral contract to replace VAIS2's `MultiAgent : AgentGroupChat`; (4) `IAgentEventBus` + `AgentEvent` to finally land Redis/Orleans streams (design first, then the DI extension on top of `Microsoft.Orleans.Streaming.Redis`). M3e is larger than M3a/b/c/d combined — may split further (M3e-1, M3e-2, …) during kickoff.

### 2026-04-18 — Phase 1 Milestone 3e-1 (cross-host parity harness)

**Goal.** Prove that the three hosts — `InMemoryAgentRuntime`, Orleans+Redis, Orleans+Postgres — produce byte-for-byte identical observable behaviour for the same deterministic scenario, *and* that Orleans grains correctly rehydrate their history after `ForceActivationCollection`. First deliverable under the M3e (extended parity) umbrella and the smallest — no new public API surface, pure test-project work. Also acts as a smoke test for everything we built across M3a/b/c: if history, filter invocations, or usage records drift between hosts, something in the grain ↔ persistence ↔ silo DI wiring is off.

**Scope decisions taken at kickoff.**
- **One collection fixture, not three.** `CrossHostFixture` spins up both Testcontainers (Redis + Postgres) *and* both Orleans `TestCluster`s so a single test method can drive all three hosts and compare results in-line. Alternative (three separate fixtures + shared scenario module) would have required running the scenario three times, collecting snapshots to disk, and diffing across runs — more machinery for the same answer.
- **Per-host recorder pairs, not per-scenario.** Each host owns a dedicated `RecordingUsageSink` / `RecordingFilter` pair kept alive for the fixture's lifetime. Tests call `ClearRecordings()` at entry to scrub between runs. Per-test recorders would've required re-deploying silos (DI containers are frozen post-deploy), which is far too expensive.
- **Snapshot excludes non-deterministic fields.** `ParitySnapshot.SummariseUsage` deliberately drops `Duration` and `StartedAt` (wall-clock noise). History comparison uses `{Role}|{Text}`, filter log uses `history={N},prompt={...}` — everything that a consumer's grain replay would reasonably see as stable.
- **Fresh runtime + proxy for rehydration.** The second rehydration test explicitly constructs a NEW `OrleansAgentRuntime` after `ForceActivationCollection` so the client-side history cache on the proxy can't accidentally mask a silo-side rehydration bug.

**What landed (`oss/agentic/`, tag `v0.6.0-alpha` pending).**
- **New test project `tests/Vais2.Agents.CrossHostTests`** (2 tests, both green):
  - `RecordingUsageSink` (`ConcurrentQueue<UsageRecord>`) + `RecordingFilter` (`ConcurrentQueue<string>` of `history={N},prompt={...}`) — dead-simple thread-safe recorders. Each exposes `Clear()` for between-scenario scrubbing.
  - `ParitySnapshot` static helpers — `SummariseHistory(IReadOnlyList<ChatTurn>) → IReadOnlyList<string>` and `SummariseUsage(UsageRecord) → string` collapse host-specific state into host-agnostic strings for comparison.
  - `HistorySizeProvider` (a third copy, duplicated rather than shared with Hosting.Orleans.Tests / Persistence.Redis.Tests / Persistence.Postgres.Tests — each project stays self-contained per the existing pattern).
  - `CrossHostFixture : IAsyncLifetime` — one Redis container + one Postgres container (Testcontainers 4.0), Postgres Orleans schema applied via `<Link>`'d embedded resources from the Postgres test project (no duplication of SQL), one `InMemoryAgentRuntime` + two `TestCluster`s. Silo configurators are parameterless classes; they read per-host recorders from fixture-owned statics set right before each `DeployAsync`. Same static-handoff technique the Redis/Postgres test fixtures use for connection strings.
  - `ParityScenarioTests`:
    - `Three_Turn_Scenario_Produces_Identical_Snapshots_On_All_Three_Hosts` — sets `SystemPrompt = "be-concise"`, asks three turns, asserts all hosts saw `history-size=1,3,5` replies and produced identical history / filter log / usage summary sequences.
    - `Scenario_Interrupted_By_Grain_Deactivation_Still_Matches_InMemory_Baseline_On_Next_Turn` — drives InMemory through 4 turns as the 8-turn-history baseline, then for each Orleans host drives 2 turns, forces activation collection, constructs a FRESH runtime+proxy and drives 2 more turns, asserts final history matches the InMemory baseline byte-for-byte.
- **72/72 tests green across all eight test projects** (22 Core + 11 Observability + 5 Parity + 12 Hosting.Orleans + 5 Persistence.Redis + 5 Persistence.Postgres + 10 Persistence.VectorData + 2 CrossHost).

**Surprises / decisions forced.**
- **`xUnit1030` forbids `ConfigureAwait(false)` in test methods.** All three existing Orleans-integration test projects already follow this rule; I slipped `.ConfigureAwait(false)` into the parity test methods out of habit (it's idiomatic in the `src/` code) and got 12 build errors. One `sed -i` later, clean build. Worth remembering for future test work.
- **Static handoff is hard to avoid with `ISiloConfigurator`.** Orleans constructs configurators via `Activator.CreateInstance`; there's no factory overload to let you inject fixture-scoped state. The existing Redis/Postgres fixtures use a single static for the connection string; we extended the pattern to a pair of statics per host (`CurrentSink`, `CurrentFilter`), set immediately before each `DeployAsync`. The silo's `Configure` captures them into locals on entry, so flipping the statics before the next cluster is deployed doesn't race — but it's a subtle ordering invariant and documented in the fixture's remarks.
- **`OrleansAgentRuntime.Remove` fires grain `DeleteAsync` fire-and-forget.** That's a hard constraint for the parity test structure: once you Remove a runtime's agent, you've also asked the silo to clear its persistent state. So the rehydration test uses TWO runtimes per Orleans host (`before` drives the initial turns, `after` drives the post-eviction turns) and only Removes the *first* at cleanup — otherwise the final `Remove` in the second iteration (Postgres, after Redis) would hit grain state that the test still needed.
- **Shared SQL via `<Link>` works but resource name follows the current project's root namespace.** The Postgres fixture's SQL files live at `tests/Vais2.Agents.Persistence.Postgres.Tests/Sql/*.sql`. We `<Link>` them into CrossHostTests under `Sql\\`, and their manifest resource names become `Vais2.Agents.CrossHostTests.Sql.PostgreSQL-*.sql` — not the Postgres project's namespace. The linking mechanism is path-based, not project-based. Good to know for future reuse across test projects.

**What's deferred (explicitly not done in this step).**
- **Tool-calling parity.** The M3e-1 scenario doesn't exercise tools because it would require registering a same-shape `IToolRegistry` on all three hosts and we haven't written the test plumbing for that yet. M3e-1 covers the history/filter/usage triad; tool parity can ride on M3e-2 or a dedicated M3e-1b if needed.
- **`AskAsync` cancellation parity.** Not exercised — the three hosts should all propagate `OperationCanceledException` without reporting usage, but testing this reliably against an in-proc Orleans silo needs harder synchronisation than we wanted for a smoke harness.
- **Tag not cut.** `v0.6.0-alpha` after this entry commits.

**What's next.** M3e-2 — streaming parity. New Abstractions surface `IStreamingCompletionProvider.StreamAsync(CompletionRequest, CT) → IAsyncEnumerable<CompletionUpdate>`. SK adapter bridges `IChatCompletionService.GetStreamingChatMessageContentsAsync`; MAF adapter bridges `IChatClient.GetStreamingResponseAsync` (MEAI types). `StatefulAiAgent.StreamAsync` added as a peer to `AskAsync`. Filter pipeline stays non-streaming for v1 (filters see a buffered request+response pair; streaming filter semantics can come later). M3e-2 will require a new PublicAPI baseline for Abstractions + Core + both adapter packages, plus likely a new `Vais2.Agents.ParityTests` case and one more CrossHostTests case proving the streamed outputs match `AskAsync` when drained.

### 2026-04-18 — Phase 1 Milestone 3e-2 (streaming parity)

**Goal.** Unify streaming across SK and MAF under a neutral contract so consumers can call `agent.StreamAsync(userMessage)` on any adapter and get the same token-by-token experience. Earlier milestones established non-streaming parity through `ICompletionProvider`; this one mirrors that surface for streams while deliberately keeping the v1 footprint minimal.

**Scope decisions taken at kickoff.**
- **Separate interface, not a `StreamAsync` method on `ICompletionProvider`.** A second interface (`IStreamingCompletionProvider`) lets non-streaming providers stay valid without throwing stubs, and lets the core detect capability via `_provider as IStreamingCompletionProvider`. Both adapter classes implement both interfaces — consumers register one concrete type and get either code path.
- **Text-delta stream, not `IAsyncEnumerable<CompletionUpdate>` on the agent.** `StatefulAiAgent.StreamAsync` yields `IAsyncEnumerable<string>` — parallels `AskAsync`'s `Task<string>` return. Consumers who want model id / token counts read them from the emitted `UsageRecord` after the stream drains (same surface as non-streaming turns). The richer `CompletionUpdate` stays at the provider boundary, not the agent boundary, so the ergonomic surface for the common case is "await foreach var token".
- **Filters + resilience bypassed for v1.** Wrapping a stream with `Task<CompletionResponse>`-shaped filters either buffers (defeating the point) or requires a streaming-filter API we haven't designed. Documented as a known gap: consumers needing RAG + streaming stay on `AskAsync`, or run retrieval manually and set `SystemPrompt` before calling `StreamAsync`. Usage sink + per-turn Activity ARE emitted after drain.
- **`StatefulAiAgent` only, not `IAiAgent`.** Adding `StreamAsync` to the neutral `IAiAgent` interface forces Orleans-proxy streaming (Orleans 9 supports `IAsyncEnumerable<T>` grain methods but it's non-trivial — persistence-state writes during mid-stream turns, failure/cancellation semantics across the silo boundary). Explicitly deferred; v1 streaming only works when consumers construct `StatefulAiAgent` directly (InMemory host + samples). Orleans streaming ships as a later slice.

**What landed (`oss/agentic/`, tag `v0.7.0-alpha` pending).**
- **Abstractions (unshipped).** `record CompletionUpdate(string TextDelta, string? ModelId, int? PromptTokens, int? CompletionTokens)` — per-chunk payload; interior updates typically carry only `TextDelta`, final update carries `ModelId` + token counts. `interface IStreamingCompletionProvider { string ProviderName; IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest, CT); }`.
- **Core (unshipped addition).** `StatefulAiAgent.StreamAsync(string, CT) → IAsyncEnumerable<string>`. Implementation: appends user turn → casts provider to `IStreamingCompletionProvider` or throws `InvalidOperationException` → starts per-turn Activity → walks `MoveNextAsync` in a `while` loop so provider exceptions get captured into a local `failure` without aborting the outer `finally` → yields each non-empty `TextDelta` → appends assistant turn from accumulated `StringBuilder` after drain (iff no failure) → reports usage once with the final update's metadata. `StatefulAgentOptions.Filters` + `ResiliencePipeline` NOT applied — documented in the method's XML.
- **SK adapter.** `SkCompletionProvider : ICompletionProvider, IStreamingCompletionProvider`. `StreamAsync` uses the same `BuildChatHistory` + `OpenAIPromptExecutionSettings` + kernel-clone-on-tools path as `CompleteAsync`, then drives `_chatService.GetStreamingChatMessageContentsAsync(...)` and maps each `StreamingChatMessageContent` to `CompletionUpdate` with `ModelId` piped through every chunk (cheap; makes the final-chunk-only path robust to consumers that stop iterating early).
- **MAF adapter.** `MafCompletionProvider : ICompletionProvider, IStreamingCompletionProvider`. `StreamAsync` builds a fresh `AIAgent` per call (same as non-streaming), then drives `agent.RunStreamingAsync(messages, thread: null, options, CT)` and maps each `AgentRunResponseUpdate` to `CompletionUpdate`, reading `UsageContent.Details.{Input,Output}TokenCount` off the update's `Contents` collection when present.
- **Tests.** Core.Tests: new `FakeStreamingCompletionProvider` implementing both interfaces + 6 `StatefulAiAgentStreamingTests` (rejects empty message, throws on non-streaming provider, yields deltas in order + appends history, reports usage with final-update metadata, propagates cancellation, suppresses assistant turn on failure). ParityTests: new `ScriptedStreamingChatCompletionService` + `ScriptedStreamingChatClient` (both throw on the non-streaming path so test intent is visible) + 3 `StreamingParityTests` (SK adapter streams deltas end-to-end; MAF adapter streams deltas end-to-end; both adapters produce equivalent streams for the same input script).
- **81/81 tests green across all eight test projects** (28 Core + 11 Observability + 8 Parity + 12 Hosting.Orleans + 5 Persistence.Redis + 5 Persistence.Postgres + 10 Persistence.VectorData + 2 CrossHost).

**Surprises / decisions forced.**
- **`ChatRole` ambiguity inside `Vais2.Agents.ParityTests`.** File-scoped namespace `Vais2.Agents.ParityTests` pulls `Vais2.Agents.*` types into scope implicitly, so unqualified `ChatRole` resolves to `Vais2.Agents.ChatRole` even when `using Microsoft.Extensions.AI;` is present. Same root-namespace shadow we already flagged as a dog-food finding after the API-freeze sweep; here we fixed it with `using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;` aliases in the scripted streaming clients. Reinforces the case for eventually renaming `Vais2.Agents.ChatRole` → `AgentChatRole`, but only when we're ready to ship a breaking change with a deprecation shim.
- **Kernel DI registration in ParityTests.** `Kernel.CreateBuilder()` returns an `IKernelBuilder` whose `.Services` is an `IServiceCollection` — standard `AddSingleton` requires `using Microsoft.Extensions.DependencyInjection;` (the non-DI package exposes the type but not the extension method). Minor papercut, worth remembering when adding tests that compose kernels in-line.
- **`yield return` + `try/finally`.** The stream accumulator lives inside `try { while ... { yield return ... } } finally { DisposeAsync() }`. C# allows `yield return` inside a `try` that has only `finally` (not `catch`). The inner `catch` for provider exceptions lives in a nested `try/catch` *around* the `await MoveNextAsync` call only, not around the `yield`, which keeps the compiler happy and gives us the exception-to-usage-sink reporting path. Took one read of the language spec to get the nesting right.
- **MAF streaming is via `AIAgent.RunStreamingAsync`, not `IChatClient.GetStreamingResponseAsync`.** Both work, but picking `AIAgent` keeps the non-streaming and streaming paths structurally parallel (both create the agent, both `Run*Async` on it) and ensures the MAF-specific `AgentRunResponseUpdate` shape is exercised. The wrapping `FunctionInvokingChatClient` is in place either way, so tool auto-invocation on streamed turns works when tools are attached.
- **Core test for final-update metadata.** The deterministic test expects `PromptTokens=7, CompletionTokens=4, ModelId="m1"` from an update script where only the last update carries those values. Since the agent stores whatever the last non-null update provided, this test also pins the "last-value-wins" aggregation rule — a subtle but load-bearing detail if a future provider streams metadata inconsistently across chunks.

**What's deferred (explicitly not done in this step).**
- **Filter pipeline over streams.** Requires a new streaming-filter surface (`IStreamingAgentFilter`?); punt until we have a concrete consumer that needs it.
- **Resilience pipeline over streams.** Polly has `ResiliencePipeline.ExecuteOutcomeAsync` but wrapping `IAsyncEnumerable` retries is subtle (partial output on retryable failure → do you emit the partial stream? start over?). Skip until there's demand.
- **`IAiAgent.StreamAsync`.** Adding it forces Orleans proxy streaming. Orleans 9 supports `IAsyncEnumerable<T>` grain returns but integrating mid-stream persistent-state writes + cancellation handling is its own slice. Do it when M3e-3 (event bus) or the Orleans host proxy gets a more serious streaming story.
- **Tool-calling streaming parity test.** The parity suite proves text chunks align; it doesn't drive a streaming tool-call flow. Given SK's connector-level auto-invoke limitation with fake `IChatCompletionService` (same reason the non-streaming parity for tools focused on MAF-only end-to-end), a good streaming tool-call test needs real adapters. Deferred until the dog-food / sample-level smoke work.
- **Tag not cut.** `v0.7.0-alpha` after this entry commits.

**What's next.** M3e-3 — neutral agent event bus + Redis streams. Design Abstractions surface: `IAgentEventBus` (publish-side) + `AgentEvent` discriminated union (TurnStarted / TurnCompleted / ToolInvoked / Error at minimum — design-first) + optional `IAgentEventSubscriber`. `Vais2.Agents.Hosting.InMemory` gets an `InMemoryAgentEventBus` (trivial `Channel<T>`-backed impl). `Vais2.Agents.Persistence.Redis` gets `UseAgenticRedisStreaming(ISiloBuilder, string)` wrapping `Microsoft.Orleans.Streaming.Redis` + an `OrleansAgentEventBus` that publishes via Orleans streams. Testcontainers test proves event round-trips through real Redis and is consumed on a subscriber grain. Depending on sizing, M3e-3 itself may split further (e.g. "event bus contract + InMemory" as M3e-3a, "Redis streams" as M3e-3b).

### 2026-04-18 — Phase 1 Milestone 3e-3a (neutral agent-event bus + InMemory impl)

**Goal.** Land the Abstractions-level contract for semantic agent events (`TurnStarted` / `TurnCompleted` / `TurnFailed`), plus an in-process bus suitable for samples and single-process hosts. Wire publishing into `StatefulAiAgent` so any consumer that supplies an `IAgentEventBus` in `StatefulAgentOptions` starts getting events without further integration work. Redis-streams-backed bus is deliberately split off into M3e-3b to keep this slice small — event surface alone is already a substantial public-API addition.

**Scope decisions taken at kickoff.**
- **Closed hierarchy for `AgentEvent`.** `abstract record AgentEvent(DateTimeOffset At, AgentContext Context)` with exactly three sealed subclasses. Consumers pattern-match; adding new event types is an *unshipped* addition to Abstractions, not a subclass in consumer code. Keeps wire serialisation (Orleans / Redis in M3e-3b) deterministic.
- **Separate from `IUsageSink`.** Usage sink stays for numeric telemetry (tokens / duration aggregation); the event bus carries semantic payload (the actual user message, the assistant text, the error). Both are wired in `StatefulAiAgent`; neither replaces the other.
- **Publish-subscribe with `IDisposable` unsubscribe.** `ValueTask Publish` + `IDisposable Subscribe(Func<AgentEvent, CT, ValueTask>)`. Matches filter/usage sink ergonomics. Channel-based pull is a subscriber pattern (wrap the handler around a `Channel<T>` if you want it), not the bus surface.
- **Best-effort publishing.** Bus failures are swallowed + logged at the Core level — same discipline as `IUsageSink` reporting. A failing observer must not break the agent's main flow.
- **Tool-invocation events deferred.** Surfacing them requires adapter-side hooks inside SK auto-invoke and MAF `FunctionInvokingChatClient`. Doable, but a separate slice (likely M3e-3c after streams, or fold into an M3e-4 orchestrator work).

**What landed (`oss/agentic/`, tag `v0.8.0-alpha` pending).**
- **Abstractions (unshipped).** `abstract record AgentEvent(DateTimeOffset At, AgentContext Context)` + sealed `TurnStarted(..., string UserMessage)`, `TurnCompleted(..., string AssistantText, string? ModelId, int? PromptTokens, int? CompletionTokens, TimeSpan Duration)`, `TurnFailed(..., string ErrorType, string ErrorMessage, TimeSpan Duration)`. `IAgentEventBus { ValueTask PublishAsync(AgentEvent, CT); IDisposable Subscribe(Func<AgentEvent, CT, ValueTask>); }`.
- **Core (unshipped).** `NullAgentEventBus.Instance` (companion to `NullUsageSink`, used as the default when options don't supply a bus). `StatefulAgentOptions.EventBus`. `StatefulAiAgent.AskAsync` publishes `TurnStarted` immediately after user-turn append + before the provider call; publishes `TurnCompleted` after the assistant turn is appended + usage sink reported; publishes `TurnFailed` (with error type + message) before rethrowing. `StreamAsync` follows the same pattern around the stream-drain loop, aggregating metadata from the final `CompletionUpdate`.
- **Hosting.InMemory (unshipped).** `InMemoryAgentEventBus` — `ImmutableArray<T>` of handlers under a short `lock`; publish snapshots the array (no lock held while handlers run), so subscribe/unsubscribe from inside a handler is safe and affects only future events. Throwing subscribers get logged + swallowed so fan-out continues. `Subscribe` returns an `IDisposable` that uses `Interlocked.Exchange` on the captured handler slot so double-dispose is safe.
- **Tests.** Core.Tests: new `InMemoryAgentEventBusTests` (5 tests — fan-out, dispose unsubscribes, throwing subscriber doesn't break fan-out, publish-with-no-subscribers, double-dispose safe) + new `StatefulAiAgentEventPublishingTests` (5 tests — AskAsync happy path emits Started+Completed, AskAsync failure emits Started+Failed, StreamAsync emits Started+Completed with accumulated text, no-bus-configured is a no-op, ambient AgentName wins over options AgentName in event context).
- **91/91 tests green across all eight test projects** (38 Core + 11 Observability + 8 Parity + 12 Hosting.Orleans + 5 Persistence.Redis + 5 Persistence.Postgres + 10 Persistence.VectorData + 2 CrossHost).

**Surprises / decisions forced.**
- **Abstract-record PublicAPI baseline is noisier than sealed records.** The analyzer wants the full closed hierarchy baselined: `abstract <Clone>$()` on the base, `override <Clone>$()` and `override sealed Equals(AgentEvent?)` on each derived type, PLUS a strongly-typed `Equals(TurnStarted?)` on each derived (which records auto-synthesise even though it duplicates the sealed-override). Plus the `virtual EqualityContract.get` and `virtual PrintMembers(StringBuilder!)` members on the base. The primary ctor and copy ctor on the abstract record ARE part of the public API baseline even though they're effectively protected — derived records outside the assembly call through them. First pass of the baseline had ~20 RS0016 errors; iterated by reading the analyzer output verbatim into baseline entries.
- **`@event` parameter name escapes to `event` in baselines.** When the interface declares `PublishAsync(AgentEvent @event, ...)`, the analyzer's expected baseline uses `event` (the unescaped identifier). Baselining `@event` fires RS0017 "not found". Minor but non-obvious; `@` is a C#-source-only escape, gone by the time the analyzer sees the symbol.
- **`NullAgentEventBus` goes in Core, not Abstractions.** Early draft put it in Abstractions (matches where `IAgentEventBus` lives); moved to Core after noticing `NullUsageSink` already lives in Core and that's the established pattern for "defaulting null sinks." Abstractions stays interface-only (plus records); Core owns the defaults.
- **Event publishing added no new `StatefulAiAgent` state machine.** Originally considered treating events as a strict "envelope" wrapping usage reports. Rejected because usage sinks are a documented surface with consumers already; overloading events through them would be a semantic mismatch. Instead, publishing is a separate awaited call pair (Started before provider, Completed/Failed after usage report). The agent's control flow stayed clear and linear.
- **`AsyncLocalAgentContextAccessor.Current` is read-only.** First draft of the "ambient context wins" test tried to assign `accessor.Current = ...`; the accessor is immutable-per-flow and uses `Push(...)` + `IDisposable` restorer. Minor papercut, makes the test clearer (explicit scope for the ambient override).

**What's deferred (explicitly not done in this step).**
- **Redis-streams-backed bus.** `UseAgenticRedisStreaming` + `OrleansAgentEventBus` live in M3e-3b. Once the Abstractions surface is frozen here, the Orleans streams integration is a straightforward "publish to an `IStreamProvider`, subscribe via an implicit-stream grain" adapter.
- **Orleans serialisation surrogates.** Not needed yet — InMemory bus passes object references directly. M3e-3b will add `AgentEventSurrogate` (or variants) under `Vais2.Agents.Hosting.Orleans` so Abstractions stays Orleans-free.
- **Tool-invocation events.** No hook into SK's auto-invoke / MAF's `FunctionInvokingChatClient`. Surface a future `ToolInvoked(At, Context, ToolName, Arguments, Result, Duration)` event when we add the hook. Could be its own M3e-3c.
- **Subscriber back-pressure / bounded queues.** Current bus is synchronous fan-out — a slow subscriber blocks the publishing turn. A `BoundedInMemoryAgentEventBus` with a `Channel<T>` pull model is a trivial future addition; skip until we have a consumer with actual back-pressure needs.
- **Filter-pipeline integration.** Filters DON'T see events (they see `CompletionRequest` → `CompletionResponse`). A filter that wants to observe events can subscribe to the bus independently. No changes needed to the filter surface.
- **Tag not cut.** `v0.8.0-alpha` after this entry commits.

**What's next.** M3e-3b — Redis-streams-backed event bus. New silo-side extension `UseAgenticRedisStreaming(ISiloBuilder, string)` in `Vais2.Agents.Persistence.Redis`, wrapping `Microsoft.Orleans.Streaming.Redis` with a conventional stream provider name (probably `vais2.agents.events`). New `OrleansAgentEventBus : IAgentEventBus` in `Vais2.Agents.Hosting.Orleans` that publishes to the stream provider + exposes implicit-stream subscription. Orleans serialisation surrogates for the three event subclasses so Abstractions stays Orleans-free. Testcontainers Redis round-trip test: publish from silo A → subscriber grain observes → assert event text round-trips.

### 2026-04-18 — Phase 1 Milestone 3e-3b (Orleans-streams-backed event bus — reframed to provider-neutral)

**Goal at kickoff.** Ship a Redis-streams-backed `IAgentEventBus` so multi-silo deployments can fan events across the cluster. Planned: `UseAgenticRedisStreaming(ISiloBuilder, string)` extension in `Vais2.Agents.Persistence.Redis` wrapping `Microsoft.Orleans.Streaming.Redis` + `OrleansAgentEventBus` + Testcontainers round-trip test.

**Reframed mid-flight.** NuGet check for `Microsoft.Orleans.Streaming.Redis` returned exactly one published version: `10.1.0-alpha.1`. That package requires Orleans 10.x core packages, which is incompatible with our whole stack pinned at 9.2.1 (and bumping to 10.x is a Phase-2-sized migration — cascades to every persistence + host + test package, has breaking changes in grain-ID APIs, and conflicts with VAIS2's own Orleans pins that M3 explicitly declined to touch). Also checked `Microsoft.Orleans.Streaming.AdoNet`: only alphas (`9.2.1-alpha.1`) at 9.x, quality unknown. `Microsoft.Orleans.Streaming.EventHubs` has stable 9.x but is Azure-only, wrong fit for a "just use Redis you already have" slice.

**Pivot.** Reframe M3e-3b from "Redis streams" to "provider-neutral `OrleansAgentEventBus`". The bus takes an `IClusterClient` + a stream-provider name; it doesn't know or care about the underlying transport. Consumers wire `AddMemoryStreams(name)` for single-process dev/tests, `AddEventHubStreams(name, ...)` for Azure, or a future `AddRedisStreams` once `Microsoft.Orleans.Streaming.Redis` ships stable at 9.x. The `UseAgenticRedisStreaming` convenience extension is deferred until then. This keeps the slice shippable against our Orleans-9.2.1 reality without taking on the Orleans 10.x bump.

**Scope decisions after the pivot.**
- **One global stream per cluster, `StreamId.Create("vais2.agents.events", Guid.Empty)`.** Consumers who want per-agent or per-tenant fan-out can publish to their own streams outside this bus; v1 keeps one stream so subscribers don't need agent ids up front.
- **`Subscribe` is synchronous block-on-task.** The `IAgentEventBus` contract requires `IDisposable Subscribe`; Orleans' `SubscribeAsync` returns a task. Subscribing blocks with `.GetAwaiter().GetResult()` — not a hot path (consumers subscribe at startup, not per turn), so the blocking is acceptable.
- **Polymorphic surrogate with per-subclass converters.** Orleans' `IConverter<TValue, TSurrogate>` resolves by exact runtime type. When a `TurnStarted` gets boxed into memory-streams' internal `List<object>`, Orleans looks up a codec for `TurnStarted` — not for `AgentEvent`. So the base-type converter isn't enough; each of the three subclasses also gets its own converter entry (`TurnStartedSurrogateConverter`, `TurnCompletedSurrogateConverter`, `TurnFailedSurrogateConverter`) that shares logic via an internal `AgentEventSurrogateHelpers` static. All four converters round-trip through the same flat `AgentEventSurrogate` struct with a discriminator `Kind`.

**What landed (`oss/agentic/`, tag `v0.9.0-alpha` pending).**
- **Directory.Packages.props + Hosting.Orleans.csproj.** Added `Microsoft.Orleans.Streaming 9.2.1` as a pin + package reference (the base streaming abstractions — needed for `IAsyncStream<T>` / `IAsyncObserver<T>` / `StreamSubscriptionHandle<T>` types, NOT a provider).
- **Hosting.Orleans (unshipped).**
  - `enum AgentEventKind { Started = 0, Completed = 1, Failed = 2 }` — discriminator for the surrogate.
  - `struct AgentEventSurrogate` with `[GenerateSerializer]`, `[Id(n)]` on every field: discriminator + all union fields (nullable where not applicable to a given kind).
  - `AgentEventSurrogateConverter : IConverter<AgentEvent, AgentEventSurrogate>` — handles polymorphic sites.
  - `TurnStartedSurrogateConverter`, `TurnCompletedSurrogateConverter`, `TurnFailedSurrogateConverter` — needed because Orleans resolves by exact runtime type; all share `AgentEventSurrogateHelpers`.
  - `public sealed class OrleansAgentEventBus : IAgentEventBus` — ctor `(IClusterClient, string streamProviderName)`. `PublishAsync` → `stream.OnNextAsync`. `Subscribe` wraps the handler in a private `ObserverAdapter : IAsyncObserver<AgentEvent>`, calls `SubscribeAsync(observer).GetAwaiter().GetResult()`, returns a `Subscription : IDisposable` that `Interlocked.Exchange`s the handle and fires a best-effort `UnsubscribeAsync`.
  - `const string OrleansAgentEventBus.StreamNamespace = "vais2.agents.events"` — convention both the bus and consumer-side `AddMemoryStreams(name)` use.
  - `static readonly Guid OrleansAgentEventBus.StreamKey = Guid.Empty` — single-stream convention.
  - `AgenticHostingOrleansServiceCollectionExtensions.AddOrleansAgentEventBus(IServiceCollection, string streamProviderName = StreamNamespace)` — DI helper.
- **Hosting.Orleans.Tests.** New `OrleansAgentEventBusTests` + `OrleansStreamsFixture` (TestCluster with `AddMemoryStreams(StreamNamespace)` + `AddMemoryGrainStorage("PubSubStore")`, configured on both silo and client sides). 4 tests: publish/subscribe round-trip for TurnStarted, TurnCompleted (all fields), TurnFailed (all fields), and `Dispose()` unsubscribes (no second-event delivery after dispose).
- **95/95 tests green across all eight test projects** (38 Core + 11 Observability + 8 Parity + 16 Hosting.Orleans + 5 Persistence.Redis + 5 Persistence.Postgres + 10 Persistence.VectorData + 2 CrossHost).

**Surprises / decisions forced.**
- **No stable `Microsoft.Orleans.Streaming.Redis` at Orleans 9.x.** The whole premise of M3e-3b as originally scoped. `curl https://api.nuget.org/v3-flatcontainer/microsoft.orleans.streaming.redis/index.json` returned exactly `["10.1.0-alpha.1"]`. Orleans' streaming.adonet is also alpha-only at 9.x. Only streaming.eventhubs has stable 9.x. Decision: provider-neutral bus + tests-on-memory-streams, skip the Redis-specific extension until upstream ships something we can pin without bumping the world.
- **Polymorphic converters need per-subclass registration.** First implementation had `IConverter<AgentEvent, AgentEventSurrogate>` only. Tests failed with `CodecProvider.ThrowCodecNotFound(Type fieldType = Vais2.Agents.AgentEvent)` when publishing — the `ObjectCodec.WriteField` tried to find a codec for the expected type AgentEvent but no concrete converter was registered for `TurnStarted` etc. Added three more `[RegisterConverter]` classes (one per subclass) that share logic via `AgentEventSurrogateHelpers`. Worth remembering: Orleans surrogate dispatch is exact-type, not polymorphic-by-base.
- **Memory streams need `PubSubStore`.** Orleans streams require a grain storage provider named exactly `"PubSubStore"`. The fixture calls `siloBuilder.AddMemoryGrainStorage("PubSubStore")` alongside `AddMemoryStreams(...)`. Without it, subscribe fails at runtime when Orleans tries to persist the PubSub grain state.
- **Client-side streams need their own configuration.** The client builder needs `AddMemoryStreams(name)` too — otherwise `_clusterClient.GetStreamProvider(name)` returns null and the bus throws. Added an `IClientBuilderConfigurator` to the test fixture.

**What's deferred (explicitly not done in this step).**
- **`UseAgenticRedisStreaming` extension.** Waiting for `Microsoft.Orleans.Streaming.Redis` to ship a stable 9.x. When it does, this is a ~10-LOC addition to `Vais2.Agents.Persistence.Redis` wrapping `siloBuilder.AddRedisStreams(name, configureRedis)`.
- **Per-agent / per-tenant stream partitioning.** Single global stream for v1. Partitioning is an additive `OrleansAgentEventBus` overload when a consumer actually needs it.
- **`OrleansAgentEventBus` participation in usage-sink-style buffering.** Publish is synchronous w.r.t. the stream `OnNextAsync`. Fire-and-forget publish is a trivial `Task.Run(() => bus.PublishAsync(...))` at the caller; not a bus concern.
- **Durable subscriber replay via stream sequence tokens.** Orleans streams support `SubscribeAsync(observer, token)` for replay; v1 ignores the token. Replay becomes relevant when we add a persistence-tier event-sourced consumer.
- **Tag not cut.** `v0.9.0-alpha` after this entry commits.

**What's next.** M3e-4 — multi-agent orchestrator neutral contract. This replaces VAIS2's `MultiAgent : AgentGroupChat` (SK-specific). Design: an `IAgentOrchestrator` or similar that takes a set of agents + a termination / next-speaker strategy, runs the conversation, and emits `AgentEvent`s along the way (now that we have an event bus). SK adapter maps to `AgentGroupChat`; MAF adapter maps to MAF's group-chat / sequential-agent primitives. Expect this to be the biggest single M3e sub-slice — orchestration is a genuinely open design question, not a straight port.

### 2026-04-18 — Phase 1 Milestone 3e-4 (multi-agent orchestrator — closes M3 + Phase 1)

**Goal.** Ship a stack-neutral multi-agent orchestration surface so consumers can compose pipelines or group chats across SK and MAF agents without picking a framework first. Feature parity with VAIS2's `MultiAgent : AgentGroupChat` (SK-specific). This is the final M3e sub-slice; landing it closes out Phase 1 Milestone 3 entirely.

**Scope decisions taken at kickoff.**
- **Below `IAiAgent`, not alongside it.** `IAiAgent` owns per-agent history. A multi-agent conversation has a shared view that every participant reads before its turn. Mixing the two would either fragment the shared view or pollute each agent's private history with turns it didn't author. Decision: orchestrators drive `ICompletionProvider` directly through an `AgentParticipant(Name, Provider, SystemPrompt?)` record. Consumers who want `StatefulAiAgent`-style memory either use `StatefulAiAgent.AskAsync` for single-agent or wrap an orchestrator output in their own history policy.
- **Two built-ins, not an extension point.** `SequentialOrchestrator` (pipeline: each stage gets the previous stage's text verbatim as its user input) and `RoundRobinOrchestrator` (shared history + cycles for N rounds + optional termination predicate) cover the most common cases. More patterns (handoff, concurrent, LLM-driven next-speaker) can be added as concrete types later without breaking this interface.
- **Shared history encodes speaker names in the text.** `RoundRobinOrchestrator` formats prior steps as assistant turns of the form `"[AgentName] text"`. `ChatTurn` has no per-turn author field; we could have split into `System`-labeled turns or multiple assistant messages, but provider semantics for "multi-assistant conversation" vary wildly. Name-prefix in text is explicit, portable, and consumer-visible.
- **`TerminationPredicate` is a delegate, not an interface.** Single-method callback: `IReadOnlyList<OrchestrationStep> → bool`. Interface-ifying it for a single hook is over-design — consumers pass a lambda or a named delegate method and move on. Runs synchronously between turns; LLM-driven termination is the consumer's problem (wrap the orchestrator).

**What landed (`oss/agentic/`, tag `v0.10.0-alpha` pending).**
- **Abstractions (unshipped).**
  - `interface IAgentOrchestrator { IAsyncEnumerable<OrchestrationStep> RunAsync(string task, CT); }` — the single entry point.
  - `sealed record OrchestrationStep(string AgentName, string Text, ChatRole Role = ChatRole.Assistant)` — one participant turn emitted as the orchestration progresses.
  - `sealed record AgentParticipant(string Name, ICompletionProvider Provider, string? SystemPrompt = null)` — the composable unit.
- **Core (unshipped).**
  - `public delegate bool TerminationPredicate(IReadOnlyList<OrchestrationStep> steps)` — optional stop-early callback.
  - `sealed class SequentialOrchestrator(IReadOnlyList<AgentParticipant>)` — runs each participant once; each subsequent participant receives the previous participant's assistant text as its user message.
  - `sealed class RoundRobinOrchestrator(participants, maxRounds, TerminationPredicate?)` — rotates through participants for up to `maxRounds` cycles, each seeing the full shared conversation (user task + all prior steps, prefixed with agent names) as `CompletionRequest.History`. Evaluates the predicate after every yielded step and breaks mid-round if it returns true.
- **Tests.** 8 new `OrchestratorTests` (both classes) in Core.Tests: sequential pipelining + system-prompt forwarding + empty-participants / empty-task rejection + cancellation; round-robin rotation order + shared-history shape (first participant sees only user, second sees user + `"[A] hello"`) + termination-predicate short-circuit + invalid-construction rejections.
- **103/103 tests green** across all eight test projects (46 Core + 11 Observability + 8 Parity + 16 Hosting.Orleans + 5 Persistence.Redis + 5 Persistence.Postgres + 10 Persistence.VectorData + 2 CrossHost).

**Surprises / decisions forced.**
- **`TerminationPredicate` delegate needs its `Invoke` in the PublicAPI baseline.** `[public delegate bool Foo(...)]` generates an `Invoke` method with the delegate's signature. The PublicAPI analyzer considers it part of the public surface and demanded a `virtual Vais2.Agents.Core.TerminationPredicate.Invoke(...) -> bool` entry alongside the type line. Minor papercut but worth filing — records' quirks around operator/`Equals` have a twin in delegates' `Invoke`.
- **System-prompt-per-participant shape.** `AgentParticipant` owns the prompt rather than threading it through `CompletionRequest` at call-time in the orchestrator. Simpler: participants are self-contained. Consumers who want the same provider with different prompts make multiple `AgentParticipant` records pointing at the same `ICompletionProvider` — fine because `ICompletionProvider` is stateless per call.
- **No event-bus emission.** `IAgentEventBus` (M3e-3a) isn't wired into orchestrators in v1. Deferred because (a) we don't have concrete event types for "orchestration step" yet — would be its own design slice; (b) consumers who want it can subscribe to their own `AgentEvent` bus in a filter around the participant's provider. Keeping the two surfaces independent until a consumer asks for the integration.

**What's deferred (explicitly not done in this step).**
- **Framework-native orchestrator wrappers.** No `SkGroupChatOrchestrator` wrapping `AgentGroupChat`, no `MafOrchestrator` wrapping MAF's primitives. Core impls work over any provider, so both stacks compose out of the box. Framework-native features (SK's plugin-aware selection strategies, MAF's handoff semantics) come if and when a consumer needs them.
- **LLM-driven next-speaker selection.** The built-in orchestrators cycle in registration order. A `SelectorOrchestrator` that delegates next-speaker choice to an LLM is an obvious future add; punt until demand.
- **Orchestration-level `AgentEvent`s.** No `StepStarted`/`StepCompleted` events on the bus. Add when a consumer integration needs them — at that point we also decide whether orchestrators take an `IAgentEventBus` in their constructor or wrap via a decorator.
- **`StatefulAiAgent` participation in orchestration.** Consumers who want orchestration + per-agent history currently have to build it themselves. A `StatefulAgentParticipant` wrapping an `IAiAgent` is plausible but has semantic questions (does the agent's history absorb the shared conversation? only the participant's own turns? none?) that are better answered with a consumer in the room.
- **Tag not cut.** `v0.10.0-alpha` after this entry commits.

**What's next.** **Phase 1 is complete.** Five options for the next session, listed roughly by impact: (1) **Push `0.2.0-preview` publicly** — bump version across packages, `dotnet pack`, push to nuget.org, write release notes, announce on a small channel. (2) **Design-partner round on the current surface** before pushing publicly — revisit the dog-food findings (ChatRole collision, SK ctor fail-fast) with actual users in hand. (3) **Phase 3 (cloud runtime MVP)** — A2A-native runtime, BYO-key multi-tenancy, Helm chart. Separate 6-8 week slice per the roadmap. (4) **VAIS2 migration** — gate on `1.0` per the plan. (5) **Opportunistic M3e-4 follow-ups** — framework-native orchestrators, orchestrator-level events, LLM selector. All deferred per-consumer-demand; doable at any time.

### 2026-04-18 — API freeze sweep + `v0.2.0-preview` local cut (closes Phase 1)

**Goal.** Match the process we ran at `v0.1.0-preview`: promote every unshipped PublicAPI entry across all packages, dog-food the surface by packing and consuming from a throwaway .NET 9 console app, annotated-tag the commit, and *don't* push anywhere. This is the post-M3 preview cut — 11 packages at `0.2.0-preview` vs 7 at `0.1.0-preview`.

**What landed (`oss/agentic/`, commit `9ef1962`, tag `v0.2.0-preview`).**
- **API freeze.** Nine packages had unshipped entries (the two observability packages' unshipped files were already empty). Merged the Unshipped → Shipped lists, sorted, deduped; reset each Unshipped file to `#nullable enable` only. Counts after sweep: Abstractions 283 shipped lines (up from 139 at 0.1.0-preview), Core 68 (up from 51), Hosting.Orleans 93 (all-new since 0.1.0-preview), Hosting.InMemory 12 (up from 8), Persistence.Redis 5 (new), Persistence.Postgres 6 (new), Persistence.VectorData 22 (new), SK 6 (up from 4, adds `StreamAsync`), MAF 6 (up from 5, adds `StreamAsync`). Observability.OpenTelemetry and Observability.Langfuse unchanged.
- **Pack.** Cleared `artifacts/packages/` of the stale `0.1.0-preview` outputs, ran `dotnet pack Vais2.Agents.sln -c Release -p:VersionPrefix=0.2.0 -p:VersionSuffix=preview -o artifacts/packages`. Produced 11 `.nupkg` + 11 `.snupkg` files (22 total) with deterministic, source-linked symbols.
- **Smoketest.** Updated `artifacts/smoketest/` to reference all 11 packages at `0.2.0-preview`. Extended `Program.cs` to construct a representative type from each of the four new packages (Hosting.Orleans, Persistence.Redis, Persistence.Postgres, Persistence.VectorData) plus exercise every major 0.2 public-API addition: `CompletionUpdate` + `IStreamingCompletionProvider` (cast both SK and MAF providers to the streaming interface); `AgentEvent` + three subclasses; `NullAgentEventBus` + `InMemoryAgentEventBus` + `Subscribe`; `SequentialOrchestrator` + `RoundRobinOrchestrator` with a `TerminationPredicate` delegate cast; `StatefulAgentOptions.EventBus` + `StatefulAgentOptions.InitialHistory`; `OrleansAgentEventBus.StreamNamespace` const + `AgentEventKind` enum; `KnowledgeRetrievalOptions` + `KnowledgeRetrievalFilter` with a `IKnowledgeRetriever` stub. Restored + built + ran clean, all 11 lines printed; zero warnings.
- **Commit + tag.** `git add src/` → `git commit` (18 files, +281/-409) → `git tag -a v0.2.0-preview`. No push. Packages live only in the untracked `artifacts/packages/` directory.

**What's deferred (explicitly not done in this step).**
- **Public push.** The tag and packages are local only, per the current "design-partner-first" plan. Push-to-nuget.org is the next explicit decision.
- **Release notes / changelog.** CHANGELOG.md is not yet maintained; PR-side commit messages are the authoritative record. Consider adding one when we push publicly.
- **Per-package version divergence.** All 11 packages share a single version string. At some point we'll want independent versioning (e.g. a surface-stable Abstractions at 1.0 while Persistence.VectorData lags at 0.x), but single-version is the right call while everything churns together.

**What's next.** Decision: push `0.2.0-preview` to nuget.org OR run a design-partner round against the local packages first. No more M3 work remains.

### 2026-04-18 — Dependency-upgrade review (companion doc)

**Scope.** Re-examined Phase 1's logged findings/surprises through the lens of a coordinated bump to the latest stable .NET/Orleans/MEAI/SK/MAF. Full review saved as a separate document per the review-global rule: **[`actor-agents-oss-dependency-upgrade-review.md`](./actor-agents-oss-dependency-upgrade-review.md)** (same folder).

**Short summary.** A coherent "everything on 10.x, still `net9.0`" path exists with zero NU1608 traps — **Orleans 10.1.0 + MEAI 10.5.0 + SK 1.74.0 + MAF 1.1.0 + OpenAI 2.10.0**. Unlocks three deferred items: (1) `UseAgenticRedisStreaming` becomes shippable via `Microsoft.Orleans.Streaming.Redis 10.1.0-alpha.1`; (2) VectorData pins move from 9.7 to 10.5, matching MEAI; (3) the NU1608/NU1107/NU1605 suppressions from SK 1.62's `OpenAI [2.2.0]` exact-pin can be removed. Recommended bundle: Phase A (coordinated 10.x bump, `net9.0`) + Phase B (ship Redis streams extension) + Phase C (opportunistic simplifications — polymorphic-codec audit, `ChatRole`→`AgentChatRole` rename while no public consumers exist) → new tag `v0.3.0-alpha`. Phases D (`net10.0` multi-target), E (FluentAssertions licence — v7+ went commercial), and F (xUnit v3) are independent workstreams, deferred. Full rationale, upgrade path, per-finding impact, risks, and task list in the companion doc.

### 2026-04-18 — Dependency-upgrade Phases A/B/C landed + `v0.3.0-preview` local cut

Outcome of the companion review doc's recommended bundle. All three phases + the preview cut landed on OSS repo `main` in a single session. **Full commit-level details + predicted-vs-actual deviations are documented in the companion doc's new §STATUS section; only the headline is duplicated here.**

- **Phase A — coordinated 10.x bump** (commit `af9821a`): Orleans 9.2.1 → 10.1.0, MEAI 9.10 → 10.5.0, SK 1.62 → 1.74.0, MAF 1.0.0-preview → 1.1.0, OpenAI 2.5 → 2.10.0. Source breaks absorbed beyond the review's predictions: MAF `CreateAIAgent` extension was *removed* (not just renamed) — switched to direct `new ChatClientAgent(...)`; Orleans 10's new `ORLEANS0014` analyzer forbids `ConfigureAwait(false)` in grain code; `[VectorStoreVector(Dimensions: 8)]` → positional `[VectorStoreVector(8)]`; Testcontainers 4.11 deprecated parameterless builders.
- **Phase B — `UseAgenticRedisStreaming`** (commit `368510c`): silo + client extensions against `Microsoft.Orleans.Streaming.Redis 10.1.0-alpha.1` + 2 Testcontainers round-trip tests. Closes the M3e-3b deferred item.
- **Phase C — rename + audits** (commit `52276bf`): `Vais2.Agents.ChatRole` → `Vais2.Agents.AgentChatRole` clean break (no type-forward shim since 0.2.0-preview wasn't public). Both simplification audits came back no-op — Orleans 10.1 still dispatches surrogate converters by exact runtime type (M3e-3b finding permanent); MAF 1.1.0 `ChatClientAgent.Instructions` is still construction-only (M3e-2 per-call pattern correct).
- **`v0.3.0-preview` local cut** (commit `91f08a1`, annotated tag `v0.3.0-preview`): 11 `.nupkg` + 11 `.snupkg` in `oss/agentic/artifacts/packages/`. Smoketest at `0.3.0-preview` clean, with a reflection probe confirming the new streaming extension resolves. **105/105 tests green. Not pushed to any remote.**

**Corrections logged in the review doc's §STATUS** (findings that contradicted the original research-agent report): FluentAssertions 6.12.3 doesn't exist on NuGet (pinned at 6.12.2); VectorData held at 10.1 not 10.5 (SK 1.74 InMemory preview references removed-in-10.5 `VectorSearchFilter`); added a repo-local `NuGet.config` clearing global sources (dev-machine Syncfusion contamination).

Phases D (`net10.0` multi-target), E (FluentAssertions licence decision — stuck on 6.12.2 either way), and F (xUnit v3) remain deferred as independent low-urgency workstreams per the review doc's recommendation.

### 2026-04-18 — Architectural review (companion doc)

**Scope.** With dependencies current and `v0.3.0-preview` cut, re-examined the public surface through the lens of what's needed before implementing agent-harness components (execution loop, tools, memory, context mgmt, prompt construction, guardrails) and the cloud control plane ("Kubernetes for agents"). Full review saved as a separate document per the review-global rule: **[`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md)** (same folder).

**Short summary.** Current surface is a solid execution-primitive foundation — `ICompletionProvider`, `IStreamingCompletionProvider`, `ITool`, `IAgentFilter`, `IAgentEventBus`, `IAgentRuntime`, `IKnowledgeRetriever` — but misses the **conversation-keying** abstraction (Thread / Session / Run) that every surveyed framework converged on. Prior-art surveys (seven agent frameworks; ten agent runtimes; seven interop protocols) identify ~20 abstraction additions grouped across six harness pillars plus the cloud control plane, with ~90% additive and a small linear breaking-change budget earmarked for `v0.4.0-preview`. Key convergences adopted: MAF's three-layer guardrails (Agent/Function/Chat), `IAgentSession` as the canonical memory boundary, `RunBudget` for step/turn/cost limits, `IContextProvider` chain, handoff + graph orchestration as additive primitives, and an `AgentManifest` + `IAgentLifecycleManager` pair for the control plane. Interop converges on **MCP + A2A** (both GA with official .NET SDKs) as the two adapter packages worth building; everything else (NLIP, AGNTCY ACP, Agent Protocol, IBM ACP) is speculative, merged into A2A, or abandoned. Polyglot (Python/TS agents) is an emergent property of A2A alignment, not a separate workstream. Durable execution (Temporal/Restate/Inngest-style journalling) is flagged as the *next* major design surface but explicitly out of scope for this review — the proposed `IToolCallDispatcher` + `AgentInterrupt` + `IAgentSession` shapes are designed to *accept* a journal layer later. Recommended delivery: Option A — pillar-by-pillar PR series into a single `v0.4.0-preview` cut (~10-12 PRs, ~2 weeks), then design-partner feedback before any component implementation work begins. Full per-pillar interface sketches, gap-vs-change matrix, and foundation-only task list in the companion doc.

### 2026-04-18 — v0.4 session pillar landed (§9.1 of the architectural review)

First of the six harness pillars. Three PRs on OSS repo `main` (not pushed) — per-PR commit cadence, matching the Phase A/B/C pattern from the dep-upgrade work. Focused plan doc at [`actor-agents-oss-v0.4-session-pillar.md`](./actor-agents-oss-v0.4-session-pillar.md).

- **PR 1 — `IAgentSession` primitive end-to-end** (commit `d4709d8`): Abstractions gains `IAgentSession` + `IAiAgent.Session`. Core's `InMemoryAgentSession` default + `StatefulAgentOptions.Session` + `StatefulAiAgent` routing all Append/Reset/History through the session (now a live shim over `Session.History`). Hosting.Orleans proxy exposes a `GrainBackedAgentSession` view backed by the existing `IAiAgentGrain` (history via cache, `ResetAsync` forwards to grain, `AppendAsync` throws with PR 3 guidance). +15 tests.
- **PR 2 — `IMemoryStore` + `IHistoryReducer`** (commit `227727d`): Abstractions gains six new types (`IMemoryStore` + `MemoryScope` + `MemoryItem` + `MemorySearchResult` + `MemoryDurability` + `IHistoryReducer`). Core adds `NullMemoryStore.Instance`, `InMemoryMemoryStore` (scope-partitioned, concurrent, substring search), `NoopHistoryReducer.Instance`, and two new options slots. `StatefulAiAgent` applies the reducer to the session snapshot before building each turn's `CompletionRequest` in both `AskAsync` and `StreamAsync` (no-op by default → zero behaviour change). MemoryStore is exposed but not consumed by the execution loop yet; it'll be wired in the context-provider pillar. +13 tests.
- **PR 3 — Orleans per-session grain + agent-config grain** (commit `a56bf19`): option (b) design landed — grain per `(agentId, sessionId)` pair. `IAgentSessionGrain` + `AgentSessionGrain` (pure state container, no LLM turn-loop on the silo — keeps the review's "session = state, execution is separate" split from AgentCore/Assistants), `OrleansSessionGrainKey` (`/` encoding helper with validation), `IAgentConfigGrain` + `AgentConfigGrain` (per-agent shared config keyed by `agentId`), `OrleansAgentSession` client proxy (lazy history hydration, refresh after Append/Reset), `OrleansAgentRuntime.GetSession` + `.GetAgentConfig`. +22 tests (including a write-through-durability test: drive a turn with `StatefulAiAgent` composed with `GetSession`, drop the runtime, create a fresh runtime, confirm history survived on the silo).
- **Cumulative**: 131/131 non-container tests green (+50 vs 81-baseline). 0 warnings. 35 files added, 4 modified. No packs cut — `v0.3.0-preview` remains the stable snapshot during pillar work.
- **Deferred decisions flagged for future pillars**: (a) `OrleansAgentRuntime.GetOrCreate(agentId, sessionId?)` overload — waits on control-plane pillar (§9.8) because it entangles with where the LLM turn-loop runs (client vs. silo); (b) `[Obsolete]` on `AiAgentGrain` — still serves the valid silo-local single-session execution use case, revisit once control plane lands.

### 2026-04-18 — v0.4 context pillar landed (§9.2 of the architectural review)

Second harness pillar. Two PRs on OSS repo `main` (not pushed). Focused plan doc at [`actor-agents-oss-v0.4-context-pillar.md`](./actor-agents-oss-v0.4-context-pillar.md) with design decisions log (ContextInvocationContext shape, injected-history placement, failure semantics).

- **PR 4 — Context provider chain + window packer + pipeline wiring** (commit `a6067ae`): Abstractions gains `IContextProvider` + `ContextContribution` (record with optional `SystemPromptAddendum`, `InjectedHistory`, `AdditionalTools`; static `Empty`) + `ContextInvocationContext` (record: Candidate + AmbientContext + Session) + `IContextWindowPacker`. Core adds `NoopContextWindowPacker.Instance` identity default + `StatefulAgentOptions.ContextProviders` + `StatefulAgentOptions.ContextWindowPacker`. `StatefulAiAgent` inserts a provider+merge+packer stage between the history reducer and the filter chain, in both `AskAsync` and `StreamAsync`. Merge rules: system-prompt addendum concatenated in provider order with `\n\n`; injected history appended *after* session history (canonical "here's retrieved context, now the conversation" layering); tools concatenated. Provider exceptions propagate and fail the turn (context is load-bearing; swallow semantics require a consumer-side wrapper). +11 tests covering every merge path + packer-runs-after-providers + session propagation via `ContextInvocationContext`.
- **PR 5 — `KnowledgeRetrievalFilter` → `KnowledgeRetrievalContextProvider`** (commit `5efdacd`): VectorData gains the context-provider equivalent (same retrieval + template semantics), returning `ContextContribution.Empty` for the zero-chunks / no-user-turn cases. Legacy `KnowledgeRetrievalFilter` flagged `[Obsolete(..., DiagnosticId="VAIS2_0001")]` with migration guidance; still fully functional for one release window (removal planned for v0.5). `KnowledgeRetrievalOptions` kept non-obsolete (shared). +6 new provider tests mirror the legacy filter tests 1-for-1; legacy filter tests stay live under `#pragma warning disable VAIS2_0001` so the obsolete path's behaviour is still verified.
- **Cumulative across both pillars**: 158/158 non-container tests green (+27 vs the 131 end-of-session-pillar mark). 0 warnings. Session + context pillars collectively add 14 new types (7 session-related + 6 memory/reducer + 4 context-related; see plan docs for the full list).
- **Two pillars down, four to go**. Next up per the review: prompt pillar (§9.3).

### 2026-04-18 — v0.4 prompt pillar landed (§9.3 of the architectural review)

Third harness pillar. Single PR on OSS repo `main` (not pushed). Focused plan doc at [`actor-agents-oss-v0.4-prompt-pillar.md`](./actor-agents-oss-v0.4-prompt-pillar.md) with three design decisions logged.

- **PR 6 — Prompt-template + system-prompt composer + contributors** (commit `98733fe`): Abstractions gains `IPromptTemplate` (render-with-variables service, exposed for consumer composition but NOT on `StatefulAgentOptions` — intentional deviation from the review; `StatefulAiAgent` doesn't consume it), `ISystemPromptComposer` (async, returns `string?`), `ISystemPromptContributor` (priority + `ContributeAsync`). Core adds `FormatStringPromptTemplate.Instance` (simple `{key}` substitution, unknown keys pass through as literal, unmatched braces emitted verbatim, null values → empty, non-strings `.ToString()`'d), `AggregatingSystemPromptComposer(IEnumerable<ISystemPromptContributor>)` (orders by `Priority` ascending, joins non-null/non-empty with `\n\n`, returns null when nothing contributes), and `StatefulAgentOptions.SystemPromptComposer`. `StatefulAiAgent` calls the composer before building the candidate in both `AskAsync` and `StreamAsync`; composer result **replaces** the plain `SystemPrompt` string (decision option (a) from the conversation — avoids merge-order ambiguity). Context-provider addenda still concatenate on top, so the canonical shape is composed-base + `\n\n` + retrieved-context. +15 tests.
- **Cumulative across three pillars**: 173/173 non-container tests green. 0 warnings. v0.4 surface now ~40 new types across Abstractions + Core + Hosting.Orleans + Persistence.VectorData.
- **Three pillars down, three harness pillars + control plane + interop + polishing to go**. Next up per the review: guardrails (§9.4).

### 2026-04-18 — v0.4 guardrails pillar landed (§9.4 of the architectural review)

Fourth harness pillar. Single PR on OSS repo `main` (not pushed). Focused plan doc at [`actor-agents-oss-v0.4-guardrails-pillar.md`](./actor-agents-oss-v0.4-guardrails-pillar.md) with five design decisions logged.

- **PR 7 — Three-layer guardrails** (commit `b6b948a`): Ships MAF's three-layer split as typed interfaces. Abstractions: `GuardrailDecision { Pass, Deny }` enum (Interrupt deferred to §9.5), `GuardrailLayer { Input, Output, Tool }`, `GuardrailOutcome` record with static `Pass` singleton + `Deny(reason?)` factory (dropped `Replacement` from the review sketch — additive later), three guardrail interfaces, `AgentGuardrailDeniedException` carrying layer + reason. Core: three `StatefulAgentOptions` slots (`InputGuardrails`, `OutputGuardrails`, `ToolGuardrails`). `StatefulAiAgent`: input guardrails after the packer/before filters (both AskAsync and StreamAsync), output guardrails between provider response and session append (in StreamAsync: post-facto after accumulator drains, documented). Denial throws the typed exception; usage sink sees `Succeeded = false`, event bus sees `TurnFailed`. `IToolGuardrail` ships but is not wired yet — execution-loop pillar (§9.5) lands the per-tool-call seam. `GuardrailTriggered` event deferred too (waits on the next Orleans surrogate regeneration so it batches with `ToolCallStarted`/`Completed`/`InterruptRaised`). +12 tests covering every deny path, short-circuit, streaming semantics, and TurnFailed emission.
- **Cumulative across four pillars**: 185/185 non-container tests green. 0 warnings.
- **Four pillars down, two harness pillars + control plane + interop + polishing to go**. Next: execution-loop (§9.5) — the pillar that introduces `IToolCallDispatcher`, `RunBudget`, `AgentInterrupt`, and the streaming-filter hook. Once it lands, `IToolGuardrail` wiring follows immediately.

### 2026-04-18 — v0.4 execution-loop pillar landed (§9.5 of the architectural review)

Fifth and biggest harness pillar — the one that flipped tool-call ownership to live inside `StatefulAiAgent` instead of the adapter SDKs. Split into five PRs after the design spike identified tradeoffs at three boundaries (budget + streaming, dispatcher + adapter rewire, events + Orleans regen). Focused plan doc: [`actor-agents-oss-v0.4-execution-loop-pillar.md`](./actor-agents-oss-v0.4-execution-loop-pillar.md).

- **PR 8 — `RunBudget` + `IStreamingAgentFilter`** (commit `404fcb6`): Additive types. Budget carried on options but not enforced (that lands in 9a). Streaming-filter chain wired in `StreamAsync` with per-delta transform + post-drain `OnStreamCompleteAsync`. +9 tests.
- **PR 9a — Tool-call dispatcher + outer loop** (commit `a69a66e`): Abstractions for `ToolCallRequest` / `ToolCallOutcome` / `IToolCallDispatcher` / `AgentBudgetExceededException`; additive params on `CompletionResponse.ToolCalls` and `ChatTurn.ToolCalls`/`ToolCallId`; `AgentChatRole.Tool`. Core: `DefaultToolCallDispatcher` with `IToolGuardrail` wiring, `StatefulAiAgent.AskAsync` rewritten as a working-history loop with full `RunBudget` enforcement (MaxTurns/MaxToolCalls/MaxPromptTokens/MaxCompletionTokens/MaxDuration). Session stays clean (user + final assistant only); intermediate assistant-with-ToolCalls and Tool-role turns live in working history for one run. +12 tests.
- **PR 9b — SK + MAF adapter rewire** (commit `01becee`): SK non-streaming flips `.Auto()` → `.None()` + maps `FunctionCallContent` to `ToolCallRequest`; MAF drops `.UseFunctionInvocation()`, uses `ChatClientAgentOptions.UseProvidedChatClientAsIs = true`, moves system prompt to a prepended System-role message (options ctor doesn't carry `Instructions`). Both adapters translate `ChatTurn.Tool` + `ChatTurn.Assistant(ToolCalls=...)` to native SDK shapes on input. Zero parity-test changes — the MAF end-to-end tool-call test now exercises the new loop instead of `.UseFunctionInvocation()` middleware. SK streaming stays on auto-invoke legacy-path; tool-using streaming through `StatefulAiAgent.StreamAsync` deferred. 0 new tests.
- **PR 9c — Events + Orleans surrogate regen** (commit `0b58365`): Three new `AgentEvent` subclasses (`ToolCallStarted`/`Completed`/`GuardrailTriggered`). `DefaultToolCallDispatcher` gets `IAgentEventBus` ctor arg + emits tool-call pair events + tool-layer `GuardrailTriggered`. `StatefulAiAgent` emits input/output-layer `GuardrailTriggered` before throwing. Orleans surrogate + enum + 3 new per-subclass converters per M3e-3b pattern. +8 tests. **Closed the two §9.4 deferrals**: `IToolGuardrail` wired, `GuardrailTriggered` event shipped.
- **PR 10 — `AgentInterrupt` + resume** (commit `3f884c1`): HITL primitive. `AgentInterrupt` record, `ResumeInput` record, `InterruptRaised` event (7th subclass), `AgentInterruptedException`, `GuardrailDecision.Interrupt = 2` (closes PR 7 deferral), `GuardrailOutcome.Interrupt(...)` factory + `InterruptPayload` field. Guardrail runners across all three layers emit `InterruptRaised` then throw. `StatefulAiAgent.ResumeAsync(ResumeInput)` v0.4 shim forwards payload as next user turn; true durable resume deferred to post-v0.4 durable-execution pillar (option (b) design call from the conversation). Orleans surrogate adds the 4th new converter. +11 tests. **Execution-loop pillar closed.**
- **Cumulative across five pillars**: 225/225 non-container tests green (+94 vs start of v0.4). 0 warnings. v0.4 abstraction surface now ~60 new types; `AgentEvent` closed hierarchy grew from 3 → 7 subclasses; every guardrail layer + tool dispatch now observable on the event bus with Orleans serialization.
- **Five pillars down. Remaining**: §9.6 tools (additive helpers — `IToolSource`, `IToolApprovalPolicy`, typed `Tool.FromFunc`), §9.7 orchestration (composable termination, handoff, graph executor), §9.8 control plane (`AgentManifest`, lifecycle, identity), §9.9 interop (MCP + A2A adapters), §9.10 polishing + the `v0.4.0-preview` cut.

### 2026-04-18 — v0.4 tools pillar landed (§9.6 of the architectural review)

Sixth pillar. Single additive PR at [`actor-agents-oss-v0.4-tools-pillar.md`](./actor-agents-oss-v0.4-tools-pillar.md) on OSS repo `main` (not pushed).

- **PR 11 — Tools helpers** (commit `1988859`): Abstractions gains `IToolSource` (catalogue-style dynamic tool providers). Core gains `AggregatingToolRegistry.BuildAsync(staticTools, sources)` — combines direct + discovered tools, caches at build time, keeps `IToolRegistry.Tools` sync. Plus `Tool.FromFunc<TInput, TOutput>` and no-arg `Tool.FromFunc<TOutput>` — typed handler → schema via `System.Text.Json.Schema.JsonSchemaExporter` + STJ-driven arg deserialization + string/null/object output handling. Zero existing-code churn; `IToolRegistry` surface unchanged. +13 tests. **Skipped from the review's §9.6 list**: `IToolApprovalPolicy` — overlaps with `IToolGuardrail.BeforeInvokeAsync` which already returns Pass/Deny/Interrupt; shipping both duplicates the surface. Finding: STJ's `JsonSchemaExporter` emits nullability-aware union types (`"type": ["string", "null"]`); adapter-side post-processing for strict dialects is a consumer concern.
- **Cumulative across six pillars**: 238/238 non-container tests green (+107 vs start of v0.4).
- **Six pillars down. Remaining**: §9.7 orchestration, §9.8 control plane, §9.9 interop (MCP + A2A adapter packages), §9.10 polishing + `v0.4.0-preview` cut.

### 2026-04-18 — v0.4 orchestration pillar landed (§9.7 of the architectural review)

Seventh pillar. Single PR on OSS repo `main` (not pushed). Focused plan doc at [`actor-agents-oss-v0.4-orchestration-pillar.md`](./actor-agents-oss-v0.4-orchestration-pillar.md).

- **PR 12 — Orchestration extensions** (commit `82ec9e6`): Abstractions gains `ITerminationCondition` (async + composable; preferred over the legacy `TerminationPredicate` delegate), `Handoff(FromAgent, ToAgent, Message?, HistoryToCarry?)` record, and `HandoffRequested` event subclass (8th in the closed `AgentEvent` hierarchy). Core gets `TerminationConditions.FromPredicate` bridge (lives in Core next to the delegate — Abstractions doesn't reference Core) and a new `RoundRobinOrchestrator(participants, maxRounds, ITerminationCondition?)` ctor that the existing delegate ctor chains through. Orleans: `AgentEventSurrogate` extended (Ids 19-21), `AgentEventKind.HandoffRequested = 7`, `HandoffRequestedSurrogateConverter`. `Handoff.HistoryToCarry` deliberately excluded from surrogate serialization. +9 tests. **Skipped from review's §9.7**: `IHandoff` interface (the record is the data contract; a parallel interface duplicates the surface) and `IAgentGraphExecutor`/`IAgentGraphBuilder` (too design-speculative without implementation — shipping empty interfaces pins design choices the eventual `GraphOrchestrator` will want to revisit).
- **Cumulative across seven pillars**: 247/247 non-container tests green.
- **Seven pillars down. Remaining**: §9.8 control plane, §9.9 interop (MCP + A2A adapter packages), §9.10 polishing + `v0.4.0-preview` cut.

### 2026-04-18 — v0.4 control-plane pillar landed (§9.8 of the architectural review)

Eighth pillar — the "Kubernetes for agents" surface. Single PR on OSS repo `main` (not pushed). Focused plan doc at [`actor-agents-oss-v0.4-control-plane-pillar.md`](./actor-agents-oss-v0.4-control-plane-pillar.md).

- **PR 13 — Control-plane contracts** (commit `93d314e`): Ships the contract surface, no engine. Abstractions gains 14 data records (`AgentManifest` + `AgentHandlerRef` / `ProtocolBinding` / `ToolRef` / `MemoryRef` / `IdentityRef` / `AutoscalingSpec` sub-records; `AgentHandle`; `AgentInvocationRequest` / `Result`; `AgentSignal`; `AgentPrincipal`; `OutboundCredential`) + `AgentStatus` enum (5 states) + 3 interfaces (`IAgentLifecycleManager` with the 7 universal verbs Create/Invoke/Signal/Query/Cancel/Update/Evict, `IAgentRegistry`, `IAgentIdentityProvider`). Core gains `InMemoryAgentRegistry` — concurrent-dict impl with `Register`/`Remove` helpers, label-prefix filter on `ListAsync`, null-version → latest-lexicographically on `GetAsync`. **Explicitly deferred** per review §5.3: HTTP API, CRDs, YAML, policy engine, multi-region, `IAgentLifecycleManager` impl (cloud-runtime Phase 3), `IAgentIdentityProvider` impl (security-engine). `IAgentLifecycleManager` ships contract-only — justified because the 7 verbs are a surveyed universal primitive (AgentCore, Temporal, Restate, Dapr, OpenAI all converge on this verb set). +12 tests.
- **Cumulative across eight pillars**: 259/259 non-container tests green.
- **Eight pillars down. Remaining**: §9.9 interop (MCP + A2A adapter packages), §9.10 polishing + `v0.4.0-preview` cut.

### 2026-04-18 — v0.4 interop pillar landed (§9.9 of the architectural review)

Ninth pillar — outbound protocol adapters. Split into two PRs per user approval (MCP + A2A deliver independent packages; no semantic coupling). Focused plan docs at [`actor-agents-oss-v0.4-mcp-interop-pillar.md`](./actor-agents-oss-v0.4-mcp-interop-pillar.md) and [`actor-agents-oss-v0.4-a2a-interop-pillar.md`](./actor-agents-oss-v0.4-a2a-interop-pillar.md).

- **PR 14 — MCP outbound adapter** (commit `e492bfe`): new `Vais2.Agents.Protocols.Mcp` package. `McpToolSource : IToolSource` wraps a pre-connected `IMcpClient` (caller owns the connection lifecycle — stdio / streamable-HTTP transports need to connect before the source's first discovery call); internal `McpBackedTool : ITool` concatenates text-type content blocks with `\n` separators and ignores image/audio/resource blocks in v0.4 (documented limitation — mixed-modal tool responses lose their non-text parts); `CallToolResponse.IsError == true` surfaces as `McpToolInvocationException` which the dispatcher maps to `ToolCallOutcome.Error`. Pinned against `ModelContextProtocol 0.1.0-preview.10` — the local NuGet mirror at `E:/nugets` is the only source allowed by the repo-local `NuGet.config` (Syncfusion contamination fix from the dep upgrade), and the 10.x MCP SDK isn't mirrored. Inbound `McpAgentServer` deferred — "agent as MCP server" maps poorly to MCP's tool/prompt/resource primitives and the shape is still unresolved. 2 shape-level tests; end-to-end coverage deferred to the v0.4 smoketest's MCP segment or a future live-server harness.
- **PR 15 — A2A outbound adapter** (commit `0d719f9`): new `Vais2.Agents.Protocols.A2A` package. `A2ARemoteAgentTool : ITool` wraps `IA2AClient` + `AgentCard` so a local agent can delegate sub-tasks to a peer agent over Agent-to-Agent. Static factory `A2ARemoteAgentTool.CreateAsync(Uri agentUrl, HttpClient? http = null, CancellationToken)` resolves the remote card via `A2ACardResolver` + builds the client — keeps the ctor synchronous for DI while discovery stays at factory time. Fixed input schema `{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}` — A2A's `AgentCard` describes skills but not a single "input shape", a tool-call model just needs one string to send. Tool-name sanitisation maps any non-`[A-Za-z0-9_-]` char in `AgentCard.Name` to `_`, collapses runs, and throws if the result is empty. Response extraction switches on `A2AResponse`: `AgentMessage` → concatenate all `TextPart.Text`; `AgentTask` → concatenate text parts across all `Artifacts`; anything else throws `A2AAgentInvocationException`. Pinned against `A2A 0.3.1-preview` — same local-mirror rationale as MCP. Inbound `A2AAgentEndpoint` (needs `A2A.AspNetCore`) and `A2ARemoteAgentProvider` (bridge as `ICompletionProvider`) both deferred to follow-up PRs; so is the `OrleansTaskStore : ITaskStore` bridge — only useful once we host A2A server-side. 12 shape-level tests (exception + ctor/factory null-rejection + parameter schema shape + `SanitiseToolName` via `InternalsVisibleTo`); end-to-end integration deferred to the smoketest's A2A segment. **Findings**: `IA2AClient.SendMessageStreamingAsync` returns `IAsyncEnumerable<SseItem<A2AEvent>>` (not plain `IAsyncEnumerable<A2AEvent>`) — needed to satisfy the interface in the test stub; `IA2AClient.CancelTaskAsync` takes `TaskIdParams`, not a string id; `AgentCard.Name` is free-form UTF-8 so sanitisation is load-bearing.
- **Cumulative across nine pillars**: 287/287 non-container tests green (+28 vs. end of control-plane: +12 A2A + +2 MCP tests, all shape-level).
- **Nine pillars down. Remaining**: §9.10 polishing + `v0.4.0-preview` cut.

### 2026-04-18 — v0.4.0-preview cut landed (§9.10 — closes all ten pillars)

Tenth and final pillar — API freeze, smoketest rewrite, pack, tag. Single commit (`9c73a4b`) + annotated tag on OSS repo `main`. Focused plan doc at [`actor-agents-oss-v0.4-polishing-pillar.md`](./actor-agents-oss-v0.4-polishing-pillar.md). **NOT pushed to any public feed** — design-partner feedback round is the next explicit decision point, same pattern as the 0.1/0.2/0.3 cuts.

- **API freeze**: one-shot `PublicAPI.Unshipped.txt → PublicAPI.Shipped.txt` across all 13 packages. Promotions: Abstractions 594 lines, Core 76, Hosting.Orleans 76, Persistence.VectorData 3, Protocols.Mcp 6, Protocols.A2A 11. Four `*REMOVED*` markers (`ChatTurn` + `CompletionResponse` ctor/Deconstruct) resolved by deleting the matching original Shipped lines — the pre-v0.4 entries no longer compile against the post-execution-loop shape (ChatTurn carries tool-call fields now, CompletionResponse carries `ToolCalls`). **Finding**: PublicAPI `*REMOVED*` markers in Unshipped must be processed at freeze time by deleting the matching original from Shipped, not by copying the marker line into Shipped — or RS0024 fires on "shipped API file has removed members".
- **Pack**: `dotnet pack -c Release -p:VersionPrefix=0.4.0 -p:VersionSuffix=preview -o artifacts/packages` → 13 `.nupkg` + 13 `.snupkg`. Two new packages vs. 0.3 cut: `Vais2.Agents.Protocols.Mcp` + `Vais2.Agents.Protocols.A2A`.
- **Smoketest rewritten** against the packaged feed at `artifacts/packages/`. Exercises every v0.4 pillar surface at runtime: `IAgentSession` + `InMemoryMemoryStore` write/read round-trip; `IContextProvider` + `IContextWindowPacker` + `KnowledgeRetrievalContextProvider`; `FormatStringPromptTemplate.RenderAsync` + `AggregatingSystemPromptComposer.ComposeAsync`; `GuardrailOutcome.Pass`/`Deny` + `AgentGuardrailDeniedException`; `RunBudget` + `DefaultToolCallDispatcher` + the four new events (`ToolCallStarted` / `ToolCallCompleted` / `GuardrailTriggered` / `InterruptRaised`) + `AgentInterrupt` / `ResumeInput` / `AgentInterruptedException`; `Tool.FromFunc<TIn, TOut>` + `AggregatingToolRegistry.BuildAsync`; `TerminationConditions.FromPredicate` + `Handoff` + `HandoffRequested`; full `AgentManifest` (every optional field incl. `Autoscaling`/`Labels`/`Memory`/`Identity`) + `InMemoryAgentRegistry.Register → GetAsync` round-trip; `AgentStatus` enum + `AgentPrincipal` + `OutboundCredential`; MCP + A2A type-presence probes (`McpToolSource`, `A2ARemoteAgentTool`, both exceptions). Plus a `StatefulAiAgent` constructed with every new option wired simultaneously (`Session`, `MemoryStore`, `HistoryReducer`, `ContextProviders`, `ContextWindowPacker`, `SystemPromptComposer`, all three guardrail lists, `Budget`, `ToolCallDispatcher`). Clean restore + build + run against the 0.4.0-preview feed.
- **Tag**: annotated `v0.4.0-preview` on OSS repo `main`. **Not pushed.** Packages live in the untracked `artifacts/packages/` directory.
- **Findings**: (a) `ToolCallCompleted` ctor requires positional `Succeeded` (bool) + `Duration` (TimeSpan) alongside the `CallId`/`ToolName`/`Error` fields — full signature `(At, Context, CallId, ToolName, Succeeded, Error, Duration)`. `ToolCallStarted` orders `(At, Context, CallId, ToolName)` — CallId before ToolName. (b) `Vais2.Agents.IPromptTemplate` collides with `Microsoft.SemanticKernel.IPromptTemplate` in any consumer that imports both namespaces (realistic when building a custom chat client for SK); fully qualify or alias. Add this to the dog-food list for potential rename before a wider release. (c) `KnowledgeRetrievalFilter` carries `[Obsolete(DiagnosticId="VAIS2_0001")]` so consumers upgrading from 0.3 can still compile; the smoketest exercises both old filter + new provider under `#pragma warning disable VAIS2_0001`. Removal is scheduled for v0.5. (d) `#nullable enable`-only Unshipped stubs are required after freeze — deleting the file fires RS0024.

- **All ten architectural-review pillars closed**. v0.4.0-preview stands locally at 17 commits past 0.3 (15 pillar PRs + 1 freeze commit), 287/287 non-container tests green, 13 packages on disk. Next decision point: design-partner feedback round vs. public push.

### 2026-04-19 — MCP SDK bumped to stable `ModelContextProtocol.Core 1.2.0` (follow-up to §9.9)

Post-freeze SDK upgrade. Single commit `cf6c883` on OSS repo `main`. Packages repacked at `0.4.0-preview`; smoketest re-run clean. 287/287 non-container tests still green.

- The design decision in the MCP pillar plan to pin `0.1.0-preview.10` was framed as a local-mirror constraint, but the true blocker was just caution — nuget.org has `1.2.0` and our repo-local `NuGet.config` already allows it via the post-`<clear/>` whitelist. Bumped.
- Adapter rewrite against the reshaped SDK (mechanical, ~30 LOC delta): `IMcpClient` interface → concrete `McpClient`; `EnumerateToolsAsync` → `ListToolsAsync` (eager `IList`, SDK auto-paginates); `CallToolResponse` → `CallToolResult` with `IList<ContentBlock>` / `TextContentBlock` hierarchy; `serializerOptions` + `progress` folded into a new `RequestOptions` bag (centralised via internal `McpToolSource.BuildRequestOptions`). PublicAPI `Shipped` entry for the `McpToolSource` ctor swapped from `IMcpClient` to `McpClient` — acceptable under 0.4-preview, not yet public.
- Switched `PackageReference` from the `ModelContextProtocol` metapackage (includes hosting/DI extensions) to `ModelContextProtocol.Core` (client types only). Smaller transitive surface.
- **Finding**: editing `Directory.Packages.props`, don't put `<clear/>` (or any other XML) inside an `ItemGroup Label="..."` attribute — MSBuild sees it as nested XML and corrupts central-package resolution with broad NU1604/NU1103/NU1701 errors that look nothing like "broken label". Lost ~15 min diagnosing.
- **Tag handling**: `v0.4.0-preview` annotated tag still points at the API-freeze commit `9c73a4b`. This commit (`cf6c883`) is post-tag but the repacked packages in `artifacts/packages/` reflect the new MCP version. Tag motion deliberately deferred — future decision whether to move the tag, cut a `v0.4.1-preview`, or land the bump under a broader `v0.4.1` covering multiple follow-ups.

### 2026-04-19 — A2A SDK bumped to `A2A 1.0.0-preview2` (follow-up to §9.9)

Second post-freeze SDK upgrade, sibling to the MCP bump above. Local working tree on OSS repo `main`. Single A2A package repacked at `0.4.0-preview`; smoketest re-run clean; 273/273 non-container tests still green (12 A2A among them).

- **Framing correction, same as MCP.** The A2A pillar plan's design decision #2 said "preview-pinned because our local mirror only has `0.3.1-preview`". That was imprecise — `E:/nugets` is just the machine's default `globalPackagesFolder` cache, not a curated mirror, and the repo-local `NuGet.config` does `<clear/>` + whitelist `nuget.org`, so `1.0.0-preview2` (published 2026-04-09) was always reachable. The pin was cautious, not constrained. Design decision #2 is superseded.
- **Adapter rewrite is substantially bigger than MCP's.** The A2A SDK 1.0 reshaped the wire types wholesale — types look protobuf-generated now. Mechanical breaks absorbed:
  - `AgentMessage` → `Message` (type rename; property shape preserved).
  - `MessageRole` → `Role` enum (`User` / `Agent` / `Unspecified`).
  - **`Part` polymorphism gone.** Was a hierarchy (`Part` base + `TextPart` / `DataPart` / `FilePart` subclasses, pattern-matched via `part is TextPart tp`). Now a single `Part` record with a `PartContentCase` discriminator (`Text` / `Data` / `Raw` / `Url` / `None`) and nullable per-case properties. Creation via factory methods: `Part.FromText(text)`, `Part.FromData(json)`, `Part.FromRaw(bytes, mediaType, filename)`, `Part.FromUrl(url, mediaType, filename)`. Text filter changes from `part is TextPart tp && !string.IsNullOrEmpty(tp.Text)` to `part.ContentCase == PartContentCase.Text` + null-check on `part.Text`.
  - `MessageSendParams` → `SendMessageRequest`. Every `IA2AClient` method takes its own `*Request` record — even the ones that used to take primitives (`GetTaskAsync(string)` → `GetTaskAsync(GetTaskRequest)`; `CancelTaskAsync(TaskIdParams)` → `CancelTaskAsync(CancelTaskRequest)`).
  - `A2AResponse` polymorphic base → `SendMessageResponse` discriminated union, with `SendMessageResponseCase` enum (`Message` / `Task` / `None`). Response-extraction switch changes from `case AgentMessage m: / case AgentTask t:` over `A2AResponse` to `switch (response.PayloadCase)` with `response.Message` / `response.Task` getters.
  - `IA2AClient` surface grew 7 → 12 methods. Renames: `SendMessageStreamingAsync` → `SendStreamingMessageAsync`. New: `ListTasksAsync`, `GetExtendedAgentCardAsync`, push-notification operations split into Create/Get/List/Delete. Our `StubA2AClient` test stub re-implements the full 12-method surface.
  - **Streaming return type**: `IAsyncEnumerable<SseItem<A2AEvent>>` → `IAsyncEnumerable<StreamResponse>`. SSE parsing is now absorbed inside the SDK — callers get a plain `StreamResponse` stream. Dropped `using System.Net.ServerSentEvents;` from the test stub (sibling finding to the MCP bump's transitive-dep trim via `.Core`).
- **Package-reference unchanged.** Unlike MCP's `ModelContextProtocol` → `.Core` split, A2A ships as a single package — no hosting/DI sub-package to pick between.
- **TFM fallback.** A2A 1.0 targets `net8.0` + `net10.0` only (no `net9.0`). Our `oss/agentic` solution is on `net9.0` — consumes the `net8.0` library via forward-compat with no warnings, restore clean.
- **PublicAPI.Shipped unchanged.** The ctor entry `A2ARemoteAgentTool(A2A.IA2AClient! client, A2A.AgentCard! card)` still holds — external type names (`IA2AClient`, `AgentCard`) survived the reshape intact; only the method signatures behind `IA2AClient` moved. No public-surface drift on our side.
- **Tag handling**: same as MCP — `v0.4.0-preview` stays on `9c73a4b`. Both post-freeze bumps live on top of the tagged commit; whether to move the tag, cut `v0.4.1-preview`, or bundle them under a broader `v0.4.1` is the pending decision. Both bumps land together in whichever direction that choice goes.

### 2026-04-19 — Tool-using streaming closed (closes §9.5 deferral)

Third post-freeze follow-up. Local working tree on OSS repo `main`. `StatefulAiAgent.StreamAsync` now wraps an outer tool-call loop parallel to `AskAsync`; the deferred PR-9c streaming-tool work from the execution-loop pillar is done. 280/280 non-container tests green (+7 new: 5 Core + 2 Parity). Four packages repacked at `0.4.0-preview`; smoketest re-runs clean.

**Scope decision taken at kickoff.** Approach A from the two-option survey: additive `IReadOnlyList<ToolCallRequest>? ToolCalls` on `CompletionUpdate`, outer tool-call loop inside `StreamAsync`, no change to the consumer-facing surface (stays `IAsyncEnumerable<string>`). Tool-call observability continues to flow through the existing `IAgentEventBus` (`ToolCallStarted` / `ToolCallCompleted` / `GuardrailTriggered` via `DefaultToolCallDispatcher`, `TurnStarted` / `TurnCompleted` once per run enveloping the whole streamed-turns loop). Approach B (richer `AgentStreamUpdate` discriminated union) deferred — can still land later on top of A without breaking A's consumers.

**What landed.**
- **Abstractions (unshipped).** `CompletionUpdate` gained a trailing `ToolCalls : IReadOnlyList<ToolCallRequest>?` parameter (default null). Record ctor + `Deconstruct` signatures change; `PublicAPI.Unshipped.txt` carries `*REMOVED*` markers for the old shape + new entries for the new shape, to be merged into `Shipped` at the next freeze. Acceptable pre-public (v0.4.0-preview not pushed).
- **Core.** `StatefulAiAgent.StreamAsync` rewritten as outer while-loop mirroring `AskAsync`'s shape: working-history / session-history split kept (session only receives user + final assistant, intermediate assistant-with-tool-calls + tool-role turns live in working history for one run). Budget enforced turn-by-turn (`MaxTurns` / `MaxDuration` at top, `MaxPromptTokens` / `MaxCompletionTokens` after each turn's drain, `MaxToolCalls` per-dispatch). Input guardrails fire every turn (same as `AskAsync`); output guardrails + `IStreamingAgentFilter.OnStreamCompleteAsync` fire once at the end of the final non-tool-call turn (post-facto deltas, same semantics as v0.4 streaming). Single `TurnStarted` at call entry + single `TurnCompleted` / `TurnFailed` at call exit envelope the whole run — matches `AskAsync`.
- **SK adapter.** `SkCompletionProvider.StreamAsync` flipped `FunctionChoiceBehavior.Auto()` → `.None()`, matching the non-streaming path. Fragment accumulation via SK's built-in `FunctionCallContentBuilder` (`builder.Append(chunk)` during stream drain, `builder.Build()` after). Terminal `CompletionUpdate(TextDelta: "", ModelId, ToolCalls: rebuilt-list)` emitted post-drain.
- **MAF adapter.** `MafCompletionProvider.StreamAsync` walks every `AgentRunResponseUpdate.Contents` for `FunctionCallContent` items and accumulates into a `Dictionary<string, FunctionCallContent>` keyed by `CallId`. Last-seen-wins per id (MEAI streaming surfaces whole FCCs, not arg-string fragments — documented). Anonymous empty call-ids get synthetic keys so providers that omit them still round-trip. Terminal `CompletionUpdate` emitted post-drain with the accumulated list.
- **Tests.** 5 new `StatefulAiAgentStreamingToolCallTests` in Core: dispatch + continuation (streams preamble text → tool call → streams next turn's answer), working-history carries tool results into the second streamed request, `MaxToolCalls` budget aborts mid-run, `MaxTurns` budget trips at iteration 2 before the second stream opens, event-bus sees the full `TurnStarted` / `ToolCallStarted` / `ToolCallCompleted` / `TurnCompleted` envelope. Plus `ScriptedMultiTurnStreamingProvider` test double for queue-of-scripts per-call playback. 2 new `StreamingToolCallingParityTests` in ParityTests: MAF end-to-end (scripted `FunctionCallContent` → tool invoked → second turn's streamed text surfaces to the consumer; session stays clean), SK adapter terminal-tool-call emission (scripted `StreamingFunctionCallUpdateContent` → terminal `CompletionUpdate.ToolCalls` present after text deltas). `ScriptedStreamingChatClient` + `ScriptedStreamingChatCompletionService` test doubles extended with per-call / function-call-update ctor overloads (existing single-turn string ctors retained for the original `StreamingParityTests`).
- **Smoketest refresh.** Repacked Abstractions + Core + SK + MAF at `0.4.0-preview` (four of the thirteen packages touched this slice). Added a `CompletionUpdate(TextDelta: "", ToolCalls: [...])` probe to `Program.cs`. Ran clean against the refreshed feed.

**Surprises / decisions forced.**
- **Cached-package invalidation gotcha.** `dotnet nuget locals global-packages --list` points at `E:/nugets` for this machine, not `~/.nuget/packages`. First smoketest rerun picked up the *old* `CompletionUpdate` from the E: cache and failed with `CS1739: no parameter named 'ToolCalls'`. Deleting the `E:/nugets/vais2.agents.abstractions/0.4.0-preview` directory (and equivalents for Core + both adapters) forced a fresh install from `artifacts/packages/`. Documented in memory alongside the MCP-bump one — the "local mirror" framing is misleading for `E:/nugets`; it's the machine's default cache.
- **Streamed turn shape: text + trailing tool-call update is one turn, not two.** First draft of the Core test scripted three separate provider calls (text, tool-call-only, final text) and asserted three streamed turns; failed because the outer loop saw script #1 (text-only, no `ToolCalls`) as terminal. Corrected to match the realistic wire shape — a streamed turn that ends in tool calls emits its text deltas *and* a terminal tool-call update in the same provider call. Two provider calls per tool-using streamed run (the tool round + the final answer), not three.
- **`AgentBudgetExceededException` field is `BudgetField`, not `LimitName`.** Guessed the latter in a test assertion (carried over from a different budget-enforcement API I misremembered); tests caught it. Noted.
- **Filters + resilience still bypassed on the streaming path.** Scope decision — same rationale as v0.4 (no streaming-filter surface designed yet; Polly `ResiliencePipeline.ExecuteAsync` wrapping `IAsyncEnumerable` retries has subtle partial-output semantics). Consumers needing filter-mediated behaviour stay on `AskAsync`. Streaming-filter surface gets its own pillar when a concrete consumer asks for it.

**Tag handling.** `v0.4.0-preview` still on `9c73a4b`. This is the third post-freeze follow-up on top of the tag (after MCP + A2A bumps). Decision to move the tag, cut `v0.4.1-preview`, or bundle everything under `v0.4.1` is still pending — all three follow-ups move together.

### 2026-04-19 — VAIS brand rename (closes the pre-public scrub)

Fourth post-freeze follow-up. Brand decision landed — the OSS library publishes under **VAIS**. Package namespace / assembly / id prefix `Vais2.Agents.*` → `Vais.Agents.*`; runtime identifier prefix `vais2.*` → `vais.*` (OTel tags) / `vais.agents.*` (Orleans stream + storage); copyright attribution "VAIS2 Platform contributors" → "VAIS contributors". Scope per [`plans/oss-vais2-reference-scrub-plan.md`](./oss-vais2-reference-scrub-plan.md). Zero `vais2` residue post-sweep; 280/280 non-container tests green; 13 packages repacked under new ids; smoketest runs clean against the refreshed feed.

**What landed.** Single coordinated sweep on local OSS-repo working tree (not pushed).

- **Package-prefix rename** — text sweep `Vais2.Agents` → `Vais.Agents` across 232 files / 1715 occurrences via `find … | xargs sed`. 13 src/ + 10 tests/ project folders renamed; 23 csproj files renamed; `Vais2.Agents.sln` → `Vais.Agents.sln`. PublicAPI.Shipped.txt + Unshipped.txt files swept in the same pass (no `*REMOVED*` markers — pre-public rename, not an API change). No cross-plan dependencies (the `Vais.Agents.*` prefix is self-consistent; external type references to SK/MAF/Orleans are unaffected).
- **Content scrub** — copyright sweep `VAIS2 Platform contributors` → `VAIS contributors` across ~80 `.cs` file headers + LICENSE + NOTICE + Directory.Build.props `<Authors>`/`<Company>`/`<Copyright>`. NOTICE line 1 `Vais2.Agents` → `Vais.Agents`. `PackageProjectUrl` / `RepositoryUrl` placeholders updated to `/vais-agents`. README lines 8 + 56 rewrote the parent-project narrative + the "full roadmap lives in the parent repo" pointer — replaced with stand-alone framing and a pointer to `docs/`. ADR 0001 + 0002 editorial sweep to drop all VAIS2-parent references.
- **Runtime identifier rename** — `AgenticTags.AgentName` / `UserId` / `TenantId` / `CorrelationId` → `vais.agent.name` / `.user.id` / `.tenant.id` / `.correlation.id`. `OrleansAgentEventBus.StreamNamespace` → `vais.agents.events`. `AiAgentGrain.StorageName` → `vais.agents`. Low-friction defaults: MAF `agentName` default `"vais-agent"`; `LangfuseEnrichmentOptions.DefaultTags` seeds `"vais-agents"`; internal anon prefix `__vais_anon_`. Diagnostic code `VAIS2_0001` → `VAIS0001` (affects the `KnowledgeRetrievalFilter` `[Obsolete(DiagnosticId=…)]` + one test's `#pragma warning disable`). Last-pass prose sweep of lingering "Vais2-specific" / "VAIS2 ships" / "VAIS2's" XML-doc phrases → stack-neutral equivalents.
- **Internal class prefix `Agentic*` kept.** Per D3 of the scrub plan — `Agentic*` is a descriptor ("agent-library-related helpers"), not a brand. `AgenticTags`, `AgenticDiagnostics`, `AgenticMetrics`, `AgenticOpenTelemetryExtensions`, `AgenticLangfuseExtensions`, `AgenticRedisPersistenceExtensions`, `AgenticPostgresPersistenceExtensions`, `AgenticHostingOrleansServiceCollectionExtensions`, `AgenticHostingInMemoryServiceCollectionExtensions` all stay. Revisit if reader feedback shows confusion.

**Verification.**
- Build: `dotnet build Vais.Agents.sln -c Release` clean, 0 warnings, 0 errors.
- Tests: 280/280 non-container green (186 Core + 43 Hosting.Orleans + 16 VectorData + 12 A2A + 11 Observability + 10 Parity + 2 Mcp).
- Packages: 13 `.nupkg` + 13 `.snupkg` at `0.4.0-preview` in `artifacts/packages/`; old `Vais2.Agents.*` builds purged from both `artifacts/packages/` and the machine's global cache at `E:/nugets/`.
- Smoketest: refreshed feed, added `NuGet.config` source rename (`vais2-local` → `vais-local`), restored + built + ran clean. Every pillar probe line emits the expected `Vais.Agents.*` type names and the renamed runtime identifiers (`diagnostics=Vais.Agents`, `stream-namespace=vais.agents.events`).
- Grep audit: `rg -i vais2 oss/agentic/` (excluding `bin/`, `obj/`, `artifacts/packages/`, `.git/`) returns zero matches.

**Surprises / findings.**
- **Text-sweep ordering.** Did text-sweep before folder/file renames. Reverse order (rename dirs first, then text-sweep) would have broken cross-project references for files transiting through the rename. Text-first keeps every reference valid throughout.
- **PublicAPI const-value change is silent.** `AgenticTags` const values are `public const string X = "..."` — changing the literal (`"vais2.agent.name"` → `"vais.agent.name"`) updates the shipped API entry (`const X = "value" -> string!`) but since both `PublicAPI.Shipped.txt` and the source changed in lockstep, the analyzer sees no drift. No `*REMOVED*` markers needed. Interesting quirk vs. record-ctor param additions where a `*REMOVED*` marker IS required.
- **`LICENSE` + `NOTICE` have no extension** and were missed by the first text-sweep (`*.cs`, `*.csproj`, `*.md`, `*.txt` etc.). Second-pass `sed` via explicit file list picked them up.
- **Two `Vais2.*` runtime strings were not directly captured by the `Vais2\.Agents` sweep** — `"vais2-agent"` (MAF default) and `"vais2-agents"` (Langfuse default-tags); dash not dot. Needed their own sed rules.
- **`NuGet.config` source name `vais2-local`** was found only at the end. Just a local-feed identifier, no functional impact, renamed for consistency.
- **Const-value updates don't require rebuilding the smoketest cache from scratch**, but package-version-preview changes DO (the `E:/nugets/vais.agents.*/0.4.0-preview/` directory must not exist for a fresh pull; the `Vais2.Agents.*` copies were from the pre-rename pack runs and had to be removed explicitly). Same cache-purge pattern as the MCP + A2A + streaming bumps.

**Tag handling.** `v0.4.0-preview` still on commit `9c73a4b` (pre-rename). The rename now sits on top alongside the three post-freeze follow-ups (MCP bump, A2A bump, tool-using streaming). All four follow-ups move together at the next tag decision — `v0.4.1-preview` bundling everything, or a fresh `v0.5.0-preview` reflecting the brand cut, or moving the existing tag to a post-rename commit.

### 2026-04-19 — v0.7 MCP inbound pillar landed (`v0.7.0-preview`)

One new package (`Vais.Agents.Protocols.Mcp.Server` — paired with the shipped outbound `Vais.Agents.Protocols.Mcp`); 20 total packages at `0.7.0-preview`. Agents hosted by this library can now surface themselves as MCP servers over stdio (for local / Claude-Desktop spawn) and streamableHttp (for web + ContextForge-style gateway composition). Pillar plan: [`plans/actor-agents-oss-v0.7-mcp-inbound-pillar.md`](./actor-agents-oss-v0.7-mcp-inbound-pillar.md). Shipped across four PRs on OSS `main` (not pushed): `badd6c9` (PR 1 core + stdio), `bdfb33d` (PR 2 streamableHttp + JWT dual-header), `8e94a7c` (PR 3 manifest resources), plus this cut.

**What landed (semantic).** One MCP tool per registered agent; input schema `{ text: required, sessionId?: string, resume?: { interruptId, runId?, payload } }`. `(agentId, sessionId)` scoping so two agents in the same virtual server never collide on caller-supplied session ids. Interrupts surface as `isError: true` with a structured `{interruptId, reason, runId, agentId, continuation}` payload; resume via a follow-up `call_tool` with `resume.interruptId` set. Manifests are published as MCP resources (`agent://{id}/{version}/manifest`, one per (id,version)), read back as the v0.6 control-plane envelope JSON — same wire shape as the HTTP surface, so one source of truth for the contract across protocols. Tool descriptions are multi-line structured text (id + version + budget block + handoffs block + input example) so they're self-explanatory in ContextForge-style virtual-server UIs without the caller having to read the manifest resource.

**What landed (wire).** stdio transport via `StdioAgentServerHost : BackgroundService` + `AddMcpAgentServerStdio` DI helper — Claude Desktop drops the binary into its `claude_desktop_config.json` and spawns it. streamableHttp transport via `MapMcpAgentServer("/mcp")` delegating to the SDK's `MapMcp` — transport plumbing stays in the SDK, the value-add is the builder + auth wiring. JWT bearer via `AddMcpAgentServerJwtAuth` with dual-header support: `X-Upstream-Authorization` wins over `Authorization` when both present, matching the ContextForge / gateway-forwarding convention (gateway rides its own credential on `Authorization`, forwards the user credential upstream).

**Design decisions locked during implementation.**
- **Option 1 semantic (one MCP tool per agent id).** Options 2/3/4 rejected — 2 ("sub-call virtual agents as tools") is not about agents, 3 ("shared conversation session") is premature bloat, 4 ("streaming-only") is orthogonal.
- **`sessionId` caller-owned, no server-side TTL.** Caller-owned lifecycle keeps the MCP protocol thin; TTL is the registry's concern, not the MCP layer's.
- **Interrupts as structured errors, not `elicitation/create`.** `elicitation/create` is the MCP-native mechanism for "agent needs input from user" but deferred to a future pillar — structured errors work with every MCP client today, elicitation doesn't.
- **Manifest as JSON resource, not YAML.** Plan considered YAML for readability but flipped to JSON — single source of truth > rendering convenience; MCP clients pretty-print JSON fine.
- **`Vais.Agents.Protocols.Mcp.Server` doesn't depend on `Vais.Agents.Control.Http.Client`.** Server packages shouldn't transitively drag client packages. `ManifestEnvelopeSerializer` is duplicated (~80 lines) in each; drift caught by round-trip tests on both sides. `AgentManifest` is the pinned shape — any new field compile-breaks via missing ctor/property access, not via serializer divergence.
- **`McpAgentServerOptions` relaxed `init` → `set`.** The `AddMcpAgentServerHttp(configure: o => { o.Name = …; })` DI lambda needs settable properties; `init` locks it to object-initializer-only and breaks the extension-method ergonomics.
- **No sampling/create.** Out of scope for v0.7 — orthogonal to "agent as server". The agent already does completion internally; exposing the MCP `sampling/create` method would just route back through the agent's own completion path, creating a recursion footgun.

**Verification.**
- Build: `dotnet build` clean, 0 warnings, 0 errors.
- Tests: 445 / 445 non-container green (+23 from pillar: 13 builder + 6 HTTP + 4 resource). Prior baseline 422 (pre-v0.7 branch start).
- Packages: 20 `.nupkg` + 20 `.snupkg` at `0.7.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.7.0-preview, added MCP-server probe line verifying `McpAgentServerBuilder.Build(...)` produces configured `McpServerOptions` with all four handlers registered (list-tools, call-tool, list-resources, read-resource) and both capabilities (tools + resources) declared. Ran clean.

**Surprises / findings.**
- **IDE0005 false-positive on ASP.NET Core namespaces.** `using Microsoft.AspNetCore.Builder;` / `using Microsoft.AspNetCore.Authorization;` surface extension methods (`MapMcp`, `AddAuthentication`) that the analyzer doesn't see as "used" — flagged as IDE0005 warnings. Same pattern already worked around in `Vais.Agents.Control.Http.Server`; solution reused: `<NoWarn>$(NoWarn);IDE0005</NoWarn>` in the csproj.
- **`McpAgentServerOptions` needed `set` not `init`.** Shipped v0.7 PR 1 as `init`-only because the struct-like pattern felt idiomatic. PR 2's HTTP DI lambda broke against it — `configure` action is `Action<T>`, not an object-initializer. Flipped to `set`; PublicAPI.Unshipped re-issued with `.set -> void` instead of `.init -> void` before PR 2 shipped, so no binary-level drift on consumers.
- **Multi-version manifest URI shape.** Plan's original `agent://<id>/manifest` doesn't disambiguate versions when the registry holds `support/1.0` + `support/1.1`. Locked to `agent://<id>/<version>/manifest` — versioned path segment, not query param. `read_resource` parser accepts the versionless shape too (routes to latest) but `list_resources` only emits versioned URIs so discovery is structural, not convention-based.
- **MCP SDK handler delegates take `RequestContext<T>` — not directly constructible from smoketest.** The handlers are `(RequestContext<X>, CancellationToken) -> ValueTask<Y>`; constructing a `RequestContext` from outside the SDK requires a hosted `McpServer` instance. The test suite calls the `internal static` helper methods via `InternalsVisibleTo` (`HandleListToolsAsync`, `HandleCallToolAsync`, etc.), which is why the smoketest's probe stops at "`Build(...)` produces configured `McpServerOptions`" rather than a full round-trip invocation. The transport-level round-trip is the SDK's concern and is covered indirectly via the TestHost HTTP tests.

**Tag handling.** Annotated `v0.7.0-preview` created on OSS `main`, not pushed. Previous tags (`v0.6.0-preview`, `v0.5.0-preview`, `v0.4.x-preview`) remain on their respective commits. All 20 packages at `0.7.0-preview` in the local feed.

---

### 2026-04-19 — v0.8 A2A inbound pillar landed (`v0.8.0-preview`)

One new package (`Vais.Agents.Protocols.A2A.Server` — paired with the shipped outbound `Vais.Agents.Protocols.A2A`); `Vais.Agents.Hosting.Orleans` extended with an A2A `ITaskStore` so `input-required` tasks survive silo restart. 21 total packages at `0.8.0-preview`. Agents hosted by this library can now surface themselves as A2A endpoints — each registered agent gets `/agents/{id}` + a `.well-known/agent-card.json`, reachable by any A2A-spec-compliant client (A2A Inspector, `a2a-cli`, peer agents). Pillar plan: [`plans/actor-agents-oss-v0.8-a2a-inbound-pillar.md`](./actor-agents-oss-v0.8-a2a-inbound-pillar.md). Shipped on OSS `main` (not pushed) as two commits: `073073a` (PRs 1-3 feat) + `850f078` (PR 4 API freeze).

**What landed (semantic).** One A2A route per registered agent (`/agents/{id}` + well-known card); `Manifest.Id` → `AgentCard.Name`, `Version` → `Version`, auto one default `invoke` skill, provider org configurable, hook-based overrides (`CustomizeCard` post-process / `BuildCard` replacement / `PerAgentOverrides` per-id dictionary) — same precedence shape as the v0.7 MCP server's customizer chain. Unary `message/send` returns `Message` for fast reply, `AgentTask` in state `input-required`/`completed`/`failed` for multi-turn runs; no SSE `message/stream` (deferred to post-v0.8). `AgentInterruptedException` → `Task(input-required)` with `{interruptId, reason, runId, agentId, payload?}` embedded as an A2A data-part tagged `Part.Metadata["vais.interrupt"] = true`; resume = follow-up `message/send(taskId)` — the handler walks `task.Status.Message` + `task.History` for the tagged envelope, threads `interruptId` + `runId` into `AgentInvocationRequest.Metadata`'s `resume.*` keys, and runs the agent through `InvokeAsync` with full continuation semantics. Policy-denied + budget-exceeded exceptions surface as `Task(failed)` with structured `{code, operation?/field?, reason}` data-parts — same envelope shape as the MCP server's error payload for cross-protocol consistency.

**What landed (wire).** `MapA2AAgentServer(IEndpointRouteBuilder, baseUrl?)` walks the registry at startup, builds one `A2AServer` per agent, and mounts routes via `A2A.AspNetCore.MapA2A` + `MapWellKnownAgentCard`. `AddA2AAgentServer(services, configure?)` DI extension registers options + defaults an `InMemoryTaskStore`. `AddOrleansA2ATaskStore(services)` in `Hosting.Orleans` swaps the default in-memory store for one backed by `IA2ATaskGrain` (per-task grain keyed by `taskId`, state persisted via `IPersistentState<A2ATaskGrainState>` under `AiAgentGrain.StorageName`). `AddA2AAgentServerJwtAuth(services, configure)` wires JWT bearer under a dedicated scheme name (`A2AJwt`) with the same `X-Upstream-Authorization` / `Authorization` dual-header precedence the MCP server uses; auto-populates `AgentCard.SecuritySchemes["bearer"]` with `{type: http, scheme: bearer, bearerFormat: JWT}` so discovery clients see the scheme in the well-known card.

**Design decisions locked during implementation.**
- **Endpoint-per-agent.** Skill-catalog-style multi-agent dispatch on one endpoint rejected; mirrors the v0.7 "one MCP tool per agent" projection, matches A2A SDK's native `AgentCard`-per-endpoint idiom, composes cleanly with gateway routers.
- **Unary-only in v0.8, SSE `message/stream` deferred.** A2A SDK just churned 0.3 → 1.0.0-preview → 1.0.0-preview2 in four weeks; `TaskUpdater`'s streaming event-queue surface is under-documented. Per spec, `message/send` can return a `Task` in any state (including `input-required`), so interrupts + resume work without streaming. Re-evaluate for v0.9.
- **One default `"invoke"` skill per agent.** A2A's `AgentSkill` shape has no clean map from Vais `Tools` / `Handoffs` / `Budget`, and inventing multiple skills per agent would be lossy. Consumers fill real taxonomies via `CustomizeCard` or `BuildCard`.
- **`A2ATaskSurrogate` stores `AgentTask` as a JSON string, not a field-mirrored surrogate.** Uses `A2A.A2AJsonUtilities.DefaultOptions` to round-trip. Schema drift couples to SDK version bumps instead of hand-synced Orleans surrogate edits — a much nicer coupling given A2A's preview cadence. `ContextId` denormalised so a future context-index grain can add list-by-context without migration.
- **`ListTasksAsync` stub in v0.8.** `OrleansTaskStore.ListTasksAsync` returns an empty response. Full listing needs a separate index grain keyed by `ContextId` — deferred post-v0.8; the surrogate already carries `ContextId` so the wiring lands without storage migration.
- **JWT scheme name `A2AJwt` (distinct from MCP's default).** Lets consumers running both MCP + A2A side-by-side keep independent audit trails while sharing the same JWT validator config.
- **No SSE, no push-notifications, no mid-task re-auth.** All three explicitly out of scope for v0.8 — documented as known gaps in the pillar plan, revisit when asked.

**Verification.**
- Build: `dotnet build` clean, 0 warnings, 0 errors.
- Tests: 457 / 457 non-container green (+12 from pillar: 8 A2A.Server builder/HTTP + 6 interrupt/resume + 5 JWT/card + 3 OrleansTaskStore — the JWT+card tests count in A2A.Server, store tests count in Orleans). Prior baseline 445 at v0.7.
- Packages: 21 `.nupkg` + 21 `.snupkg` at `0.8.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.8.0-preview, added A2A-server probe line verifying `A2AAgentServerBuilder.BuildAsync(...)` produces an `A2AAgentServerEntry` with the expected route (`/agents/smoke-a2a-agent`), derived `AgentCard` shape (name, version, one `invoke` skill, one JSON-RPC `AgentInterface` with `iface-url=http://localhost:5080/agents/smoke-a2a-agent`, `streaming=false`), and 7 A2A.Server types + 3 OrleansTaskStore types + the `A2AJwt` scheme name all present. Ran clean.

**Surprises / findings.**
- **SDK-semantics gotcha #1: `TaskUpdater` requires `Submit → StartWork → {Complete|RequireInput|Fail}`.** Calling `RequireInputAsync` or `FailAsync` without prior `Submit` on a fresh task silently produces no events and the SDK returns `A2AException("did not produce any response events")`. Nothing in the xmldoc says this; only the actual MaterializeResponseAsync loop (seen after fetching `A2AServer.cs` from GitHub) makes it clear the event-queue iteration must see a Task event.
- **SDK-semantics gotcha #2: on resume, must re-enqueue the existing task first.** `MaterializeResponseAsync` only captures `Task` or `Message` events as the unary response; `TaskStatusUpdateEvent`s (which are what `TaskUpdater.CompleteAsync` / `FailAsync` / `RequireInputAsync` emit) update the store but don't satisfy the "any response event" check. Fix: `eventQueue.EnqueueTaskAsync(context.Task!)` at the top of the resume branch. If nothing else enqueues a Task, the handler looks like it did nothing to the SDK, and the client sees the same "did not produce" error. Worth documenting in the README + A2A.Server package notes.
- **`A2A.AspNetCore` is a separate NuGet package.** Plan assumed it was built into `A2A` itself; nuget.org shows it's a sibling package (same repo, separate assembly). Added `<PackageReference Include="A2A.AspNetCore" />` + corresponding CPM entry; all routing extensions (`MapA2A`, `MapWellKnownAgentCard`) live there.
- **Plan's "transitive A2A via outbound package" claim was wrong.** `Vais.Agents.Hosting.Orleans` had no transitive A2A reference; the outbound `.A2A` package isn't referenced by `Hosting.Orleans`. Added direct `<PackageReference Include="A2A" />` — trivial, matches the `A2A.AspNetCore` split rationale (A2A SDK is small; direct ref is cleaner than indirection).
- **Well-known discovery path is `/.well-known/agent-card.json`, not `/.well-known/agent.json`.** Plan used the older A2A spec name; SDK `1.0.0-preview2` uses `agent-card.json`. Followed the SDK, updated the pillar plan.
- **`AgentCard.DefaultInputModes` / `Skills` / `SupportedInterfaces` are `List<T>`, not `StringList`.** One early build error on a collection-initializer for `StringList` (which is a wrapper around `List<string>` used only for security-requirement scopes, confirmed via reflection probe). Switched to direct `List<string>`/`List<AgentSkill>`/`List<AgentInterface>` initializers — works.
- **`AddA2AAgentServerJwtAuth` handles both call orders.** If `AddA2AAgentServer` was called first, we mutate the registered options singleton directly (the card-customizer runs on enumeration at `MapA2AAgentServer` time); if not, we fall back to `IOptions<A2AAgentServerOptions>.PostConfigure`. Two-branch lookup — pattern worth keeping for other "ordering-sensitive" extension pairs.

**Tag handling.** Annotated `v0.8.0-preview` created on OSS `main` (commit `850f078`), not pushed. Previous tags (`v0.7.0-preview` on `aae82d7`, earlier) remain on their respective commits. All 21 packages at `0.8.0-preview` in the local feed.

---

### 2026-04-20 — v0.9 Graph orchestration pillar landed (`v0.9.0-preview`)

One new package (`Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` — thin adapter around `Microsoft.Agents.AI.Workflows 1.1.0` GA); extensions to `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Control.Abstractions`, `Vais.Agents.Control.Manifests.Json`, `Vais.Agents.Control.Manifests.Yaml`, `Vais.Agents.Hosting.Orleans`. 22 total packages at `0.9.0-preview`. Closes the v0.4 §9.7 deferral (`IAgentGraphExecutor` + `IAgentGraphBuilder` were deliberately skipped then as "too design-speculative without implementation"); the v0.9 spike (`plans/actor-agents-oss-v0.9-graph-orchestration-findings.md`) collapsed the design space. Shipped on OSS `main` (not pushed) as two commits: `2dac828` (PRs 1-4 feat) + `af2906e` (PR 5 API freeze).

**What landed (semantic).** `IAgentGraph<TState>` in Abstractions as the neutral contract (plus `IAgentGraph` non-generic specialisation over `IDictionary<string, JsonElement>` for declarative-first consumers). `AgentGraphManifest` with closed discriminated unions for `GraphEdgePredicate` (K8s-style `{property, operator, value}` matchers + `allOf`/`anyOf`/`not` combinators + `HandlerRef` escape hatch; 10 operators total) and `GraphEdgeEffect` (`Set` / `Increment` / `Append` + `HandlerRef`). Closed `AgentGraphEvent` taxonomy carrying `RunId` + `SuperStep` on every event. `IGraphCheckpointer` contract + `GraphCheckpoint` record + `GraphRecursionException` for max-step ceiling breaches. Four node kinds: `Agent`, `Code`, `Interrupt`, `End`. `IResumableAgentGraph<TState>` capability interface for durable resume; the in-process orchestrator implements it, the MAF adapter defers to v0.10.

**What landed (wire).** `InProcessGraphOrchestrator<TState>` in Core — Pregel/BSP runtime, zero external deps, drives an agent via `IAgentLifecycleManager` the same way the v0.6 control plane does, resolves `GraphAgentRef.Version = null` via `IAgentRegistry.GetAsync`. Fallback text `"(continue)"` when state has no messages + no `query` so agents with non-empty-text validation don't trip on empty-state graph steps. `MafGraphOrchestrator<TState>` + `MafGraphBuilder` + `GraphMessage` + `GraphNodeExecutor` in the new MAF adapter package — executors declare `[SendsMessage(typeof(GraphMessage))]` + `[YieldsOutput(typeof(GraphMessage))]` attributes (required by MAF), `WithOutputFrom(End + Interrupt nodes)` marks the workflow outputs. Routing stays inside the executor (MAF's conditional edges are sync, our predicate evaluator is async). `kind: AgentGraph` YAML/JSON manifest loader ships via new `JsonAgentGraphManifestLoader` + `YamlAgentGraphManifestLoader` classes (separate from v0.6's `IAgentManifestLoader` to preserve source-compat); `AgentGraphManifestValidator` does structural validation + DFS-based cycle detection; `AgentGraphManifestEnvelope` handles round-trip JSON. `OrleansCheckpointer` in `Hosting.Orleans` — same JSON-string surrogate pattern as v0.8's `A2ATaskSurrogate`, keyed by `runId`, persisted via `IPersistentState` under `AiAgentGrain.StorageName`.

**Design decisions locked during implementation.**
- **Hybrid state model**: `IAgentGraph<TState>` generic for code-first (matches MAF's `Executor<T>` idiom) + `IAgentGraph` specialisation over `IDictionary<string, JsonElement>` for YAML-authored graphs with JSON-schema-declarable state.
- **Ship MAF adapter AND in-house fallback** — MAF adapter for MAF-stack feature depth (native `CheckpointManager` + `RequestPort` HITL + fan-out/fan-in edges reachable post-v0.9); in-house for SK-only stacks + all tests (zero MAF dep).
- **Cycles + interrupt/resume day one** — the shared determinism-discipline contract LangGraph imposes on users is documentation, not runtime. The `IResumableAgentGraph<TState>` + `OrleansCheckpointer` pair lets interrupts survive silo restart.
- **Declarative YAML ships as ours**, not MAF's — `Microsoft.Agents.AI.Workflows.Declarative` is still `rc1`/preview and has two incompatible dialects (C# trigger-based vs Python name-based); bridging can land when it GAs.
- **Edge predicates = K8s-style matchers + boolean combinators + `HandlerRef` escape hatch.** No expression DSL. Kubernetes `matchExpressions` shape consumers already know.
- **Edge side-effects = tiny vocabulary** (`set` / `increment` / `append`) + `HandlerRef` escape hatch. Covers retry-counter patterns without a DSL.
- **One default reducer** (`LastWriteWins`) + one well-known key (`messages` with `AppendMessages` reducer). Custom reducers declarable in YAML deferred to post-v0.9.
- **`IAgentGraph<TState>` is a sibling of `IAgentOrchestrator`**, not a subtype — graph run-shape (state-threaded + multi-step + checkpointable) differs too much from v0.4 orchestrator (speaker-list + turn-loop).
- **Routing inside the MAF adapter's executor**, not via MAF's `AddEdge<T>(condition)` surface — MAF's conditional edges are sync; our predicate evaluator is async (for `HandlerRef` dispatch). Semantic parity with the in-process orchestrator is preserved via the same shared evaluator + effect applier + reducer helpers.

**Verification.**
- Build: `dotnet build` clean, 0 warnings, 0 errors.
- Tests: 490 / 490 non-container green (+33 from pillar: 12 in-process + 7 manifest loader + 9 MAF adapter + 5 Orleans checkpointer). Prior baseline 457 at v0.8.
- Packages: 22 `.nupkg` + 22 `.snupkg` at `0.9.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.9.0-preview, added graph-orchestration probe. Prints `entry=start nodes=2 run-events=6 completed=True envelope-roundtrip-id=smoke-graph envelope-roundtrip-entry=start maf-types-probed=3 orleans-checkpointer-types=3`. Final line: `"All twenty-two Vais.Agents.* 0.9.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean.

**Surprises / findings.**
- **MAF's `Executor<T>` attribute model.** MAF blocks `IWorkflowContext.SendMessageAsync` with `InvalidOperationException: Executor 'X' cannot send messages of type 'T'` unless the class carries `[SendsMessage(typeof(T))]`. Same for `[YieldsOutput(typeof(T))]` — `YieldOutputAsync` is a no-op without it. Neither attribute is surfaced in the "minimal example" docs; discovered via a diagnostic test that dumped MAF's `WorkflowEvent` stream.
- **`WithOutputFrom(executors)` is load-bearing.** Without it, `YieldOutputAsync` doesn't materialise as `WorkflowOutputEvent` and the caller loses the final state. Registered End + Interrupt nodes as outputs; non-terminal nodes stay silent.
- **`RequestHaltAsync` on End nodes suppresses `ExecutorCompletedEvent`.** Kept it off for End (let the yield close naturally, completion event fires) and on for Interrupt (halts pending route to the post-interrupt node). Different semantics per kind.
- **Async predicates force routing into the executor, not MAF's `AddEdge<T>(condition)` surface.** MAF's edge conditions are `Func<T, bool>` (sync); our `GraphEdgePredicate.HandlerRef` dispatcher calls `IGraphEdgePredicate.EvaluateAsync`. Semantic parity with the in-process orchestrator preserved by running the same shared evaluator on both paths.
- **Orleans package needed `IResumableAgentGraph<TState>` on the capability-interface pattern**, not on `IAgentGraph<TState>` itself — the MAF adapter legitimately doesn't support resume in v0.9 (MAF's own `CheckpointManager` has a different checkpoint format; bridging to our `GraphCheckpoint` shape is non-trivial and deferred to v0.10 when the MAF integration broadens).
- **`InProcessGraphOrchestrator` ctor took an extra `IAgentRegistry` arg beyond the original plan** — needed so `GraphAgentRef.Version = null` can resolve to the concrete version the lifecycle manager keyed on at `CreateAsync` time.

**Deferred to v0.10 (explicitly documented as scope-cuts).**
- `AddMafGraphOrchestrator` DI extension + MAF `CheckpointManager` integration + `RequestPort`-backed HITL — the adapter ships with ctor-only construction + simpler yield+halt interrupt pattern.
- `handlerRef` TypeName resolution as a structural validator check + `stateBindings` ↔ `OutputSchema` cross-check on the graph loader — runtime path already surfaces extraction mismatches; validator stays structural.
- HTTP control-plane graph CRUD (`GET /graphs/{id}` / `PUT /graphs/{id}`) — not required to ship the declarative-authoring story this pillar delivers.
- MAF adapter's resume path.

**Tag handling.** Annotated `v0.9.0-preview` created on OSS `main` (commit `af2906e`), not pushed. Previous tags (`v0.8.0-preview` on `850f078`, earlier) remain on their respective commits. All 22 packages at `0.9.0-preview` in the local feed.


### 2026-04-20 — v0.10 Streaming pipeline pillar landed (`v0.10.0-preview`)

No new packages — extensions only to `Vais.Agents.Abstractions` + `Vais.Agents.Core`. 22 total packages at `0.10.0-preview`. Closes the v0.4 / v0.4.1 documented gap (`StatefulAgentOptions.Filters` + `StatefulAgentOptions.ResiliencePipeline` bypassed on `StreamAsync`; consumers needing filters stayed on `AskAsync`). Grounded in the spike findings (`plans/actor-agents-oss-v0.10-streaming-pipeline-findings.md`) and the 3-PR pillar plan (`plans/actor-agents-oss-v0.10-streaming-pipeline-pillar.md`). Shipped on OSS `main` (not pushed) as two commits: `f2efddc` (PRs 1-2 feat) + `b28d80d` (PR 3 API freeze).

**What landed (semantic).** `IStreamingAgentFilter` widened with an additive DIM `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>` — around-provider hook that mirrors `IAgentFilter`'s role on the non-streaming path. Single type now carries three override points (around-provider / per-delta / end-of-stream); filter authors override whichever combination they need. Short-circuit caching (yielding synthetic deltas without invoking `next`) works cleanly because the agent iterates whatever the chain yields, regardless of origin. `IStreamingCompletionProvider.StreamAsync` gets a normative idempotence contract clause: exceptions before the first `CompletionUpdate` is yielded leave no observable side-effect on shared state; exceptions after the first delta are not retryable. Both shipped adapters (SK + MAF) satisfy this by construction already.

**What landed (wire).** `StatefulAgentOptions.StreamingResiliencePipeline` as a sibling knob to `ResiliencePipeline` — null ⇒ agent's internal streaming default (same 2-retry exponential-backoff cadence as the non-streaming default). `StatefulAiAgent.StreamAsync` per-turn loop refactored into two phases:
- **Phase 1 (retry boundary)**: Polly wraps `InvokeThroughStreamingFilters(...).GetAsyncEnumerator(...)` + first `MoveNextAsync()`. Transient pre-first-delta failures retry; the callback owns the prior-attempt enumerator's lifecycle (dispose + promote-on-success). `OperationCanceledException` + filter-domain exceptions excluded from `ShouldHandle`.
- **Phase 2 (drain)**: plain `try { ... yield return ... } finally { dispose }` with the existing per-filter `OnStreamDeltaAsync` chain + accumulator + tool-call aggregation unchanged. Mid-stream failures surface via the local `failure` variable without retry. `yield return`-inside-`try/catch` restriction respected — drain uses `try/finally` only.

New internal `IsFilterDomainException(Exception)` helper in Core centralises the "don't retry agent-domain exceptions" rule for both streaming and non-streaming pipelines (`AgentGuardrailDeniedException`, `AgentBudgetExceededException`, `AgentInterruptedException`, `OperationCanceledException`). Existing non-streaming default pipeline rewired to use it (was inline OCE-only check).

**Design decisions locked during implementation.**
- **Widen shipped `IStreamingAgentFilter`, not a new type.** Archetype exercise in the findings doc discriminated against the buffering alternative (can't stream cached chunks) and the new-type alternative (two registrations, cognitive overhead). Single type, DIM, additive PublicAPI entry.
- **Agent-driven per-delta iteration stays.** `InvokeAsync` wraps the provider call; `OnStreamDeltaAsync` + `OnStreamCompleteAsync` are fired by the agent between the filter's yield and the caller's yield. Preserves the "agent owns the iteration" invariant that makes event/budget/guardrail ordering predictable.
- **Pre-first-delta-only retry**; per-turn boundary inside the tool-call loop. Each streamed turn is an independent retry boundary — retries for turn N+1 never replay turn N's dispatches or `workingHistory` writes.
- **Zero adapter code changes.** Both SK + MAF adapters are retry-safe by construction today (SK clones the `Kernel` when tools attached; MAF constructs a fresh `ChatClientAgent` per call; neither shares connector state pre-first-delta). Tests pin the contract; no production rewrite.
- **Separate `StreamingResiliencePipeline` knob** rather than reusing `ResiliencePipeline`. Same default cadence, different scope (pre-first-delta only). Consumers who want identical retry budgets assign the same instance to both.

**Verification.**
- Build: `dotnet build Vais.Agents.sln -c Release` clean, 0 warnings, 0 errors.
- Tests: 523 across the whole solution. Core 318 (+12 new in `StreamingFilterPipelineTests.cs`); ParityTests 17 (+7 new in `StreamingIdempotenceParityTests.cs` — 3 SK + 3 MAF + 1 cross-stack parity). All other packages unchanged since v0.9.
- Packages: 22 `.nupkg` + 22 `.snupkg` at `0.10.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.10.0-preview, added streaming-pipeline probe. Prints `filter-around-invoked=True delta-hook-count=2 request-rewrite-max-tokens=77 deltas-yielded=[smoke ,reply] assistant-turn-appended=True`. Final line: `"All twenty-two Vais.Agents.* 0.10.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean.

**Surprises / findings.**
- **`yield return` in a `try { ... } catch` is a compile error** (known from v0.9, re-encountered). Solved same way: outer `try { ... } finally { dispose }` wrapping yields; inner `try { MoveNextAsync } catch { failure = ex; break; }` for local error capture. The `break` exits the drain loop cleanly because it sits inside the outer try — the finally still fires.
- **Unreachable-code warning (CS0162) on iterator-body `yield break` after an unconditional `throw`**. Fake streaming helpers in tests need `async IAsyncEnumerable<T>` with `yield break` to be recognised as iterators by the compiler; the `yield break` is dead after the throw. Suppressed via `#pragma warning disable CS0162` on the one line.
- **Polly `ResiliencePipeline.ExecuteAsync` callback runs multiple times on retry** — if the callback captures state via closure (the prior-attempt `enumerator` in this case), the callback body needs to dispose/reset on each entry. Patterned the callback to promote the new enumerator only on a successful first-`MoveNextAsync`; empty-stream attempts dispose the local enumerator immediately.
- **Adapter contract text is infrastructure**. Writing the "pre-first-delta is safe to retry" clause made it explicit that SK's kernel-clone behaviour + MAF's fresh-`ChatClientAgent`-per-call behaviour are load-bearing — not just conveniences. If a future adapter refactor tries to cache the connector across calls, the idempotence tests will catch it immediately.

**Deferred to v0.11+ (explicitly documented as scope-cuts in the pillar plan).**
- Per-attempt retry telemetry — SK/MAF emit internal `Activity` spans per attempt; our `chat` span stays per-call. Consumers who want per-attempt visibility wire their own inner `ActivitySource` or inspect HttpClient-level spans.
- Streaming journal replay (v0.5's `IAgentJournal` stays tool-call-granular).
- Orleans streaming passthrough to remote hosts (`OrleansAiAgentProxy` still doesn't proxy `StreamAsync`).
- Buffer-everything fallback for request→response-shaped filter semantics on streaming.

**Tag handling.** Annotated `v0.10.0-preview` created on OSS `main` (commit `b28d80d`), not pushed. Tag message summarises the pillar's three PRs. All 22 packages at `0.10.0-preview` in the local feed.

**Shape adjustment vs. original pillar plan.** The plan listed `Vais.Agents.Ai.SemanticKernel.Tests` + `Vais.Agents.Ai.MicrosoftAgentFramework.Tests` as the destinations for adapter idempotence tests; those projects don't exist in the OSS layout. Tests landed in `Vais.Agents.ParityTests/StreamingIdempotenceParityTests.cs` — the project references both adapters and already hosts the scripted-fake pattern for existing streaming-parity tests. Single file holds 6 per-adapter tests + 1 cross-stack parity test. Per-adapter intent preserved via `[Fact]` naming.


### 2026-04-20 — v0.11 OpenAPI + Idempotency-Key pillar landed (`v0.11.0-preview`)

No new packages — extensions only to `Vais.Agents.Control.Abstractions`, `Vais.Agents.Control.Http.Server`, `Vais.Agents.Control.Http.Client`, `Vais.Agents.Hosting.Orleans`. 22 total packages at `0.11.0-preview`. Closes two v0.6-era backlog items from the §7 research doc — OpenAPI auto-generation + `Idempotency-Key` dedupe store — at once. Grounded in the spike + findings docs (`plans/actor-agents-oss-v0.11-openapi-idempotency-*.md`). Shipped on OSS `main` (not pushed) as two commits: `83d5ff4` (PRs 1-4 feat) + `8b091c1` (API freeze).

**What landed (semantic).** `IIdempotencyStore` contract in `Control.Abstractions` — 3-phase lifecycle (`TryBeginAsync` / `CompleteAsync` / `ReleaseAsync`) + 4-tuple `IdempotencyKey(TenantId, Method, Path, Key)` scope + 4-status enum (`New` / `Replay` / `Mismatch` / `InFlight`) + `CachedResponse` record. Stripe-shape dedupe semantics: raw-body SHA-256 fingerprint, 24h default TTL, cache 2xx+4xx, release 5xx, `Idempotency-Replayed: true` header on replay, 422 on body-mismatch, 409+`Retry-After: 1` on in-flight. `/openapi/v1.json` spec endpoint via `Microsoft.AspNetCore.OpenApi 9.0.11` (built-in, zero transitive bloat); `VaisProblemDetailsOperationTransformer` attaches `x-vais-type-urns` extension to error responses so consumers doing client codegen can pattern-match on stable URNs.

**What landed (wire).**
- **Server** (`Control.Http.Server`): `InMemoryIdempotencyStore` (ConcurrentDictionary + `TimeProvider`-overridable background eviction + CAS-with-stale-replace in `TryBeginAsync`); `AgentControlPlaneIdempotencyMiddleware` (response-capture via `MemoryStream` swap; releases reservation on exception with body-stream restoration); `IdempotencyOptions` (Ttl/EvictionInterval/MaxKeyLength/PathExclusions/IncludeGetsInExclusion); `AddAgentControlPlaneIdempotency` + `UseAgentControlPlaneIdempotency` extensions. `AddAgentControlPlaneOpenApi` + `MapAgentControlPlaneOpenApi`; route annotations (`.WithSummary` / `.WithDescription` / `.WithTags` / `.Accepts<T>` / `.Produces<T>` / `.ProducesProblem`) on all 7 control-plane routes + health/ready. `ProblemDetailsMapping` gains 2 URN consts (`IdempotencyMismatchType` + `IdempotencyInFlightType`) + factory helpers with inner `RetryAfterResult` wrapper for the 409+header case.
- **Client** (`Control.Http.Client`): `AgentControlPlaneClientOptions` (AutoGenerateIdempotencyKey + IdempotencyKeyFactory). `IAgentControlPlaneClient` gains 6 new DIM overloads (one per write method) accepting explicit `idempotencyKey`; DIM default delegates to original + drops key so mocks don't break. `AgentControlPlaneClient` adds second ctor accepting options; overrides the 6 new overloads to thread via `TryAddWithoutValidation("Idempotency-Key", ...)`. `InvokeAsync`/`SignalAsync` migrated from `PostAsJsonAsync` to explicit `HttpRequestMessage` so header can ride along.
- **Orleans** (`Hosting.Orleans`): `OrleansIdempotencyStore` + `IIdempotencyKeyGrain` + `IdempotencyKeyGrain` + `IdempotencyKeyGrainState` + `IdempotencyKeySurrogate` + `IdempotencyGrainBeginResult` (Orleans-serialisable wire type translating to abstractions-level `IdempotencyBeginResult` — required because `Vais.Agents.Control.Abstractions` has no Orleans attributes). Grain composite key: `Uri.EscapeDataString(tenant)|method|path|key` so no component collides with the `|` separator. `AddOrleansIdempotencyStore` DI ext with optional TTL override.

**Design decisions locked during implementation.**
- **Widen shipped `IAgentControlPlaneClient` with DIM overloads, not new methods.** Clients implementing the interface don't need to implement the new overloads — DIM default silently drops the idempotency key. Concrete `AgentControlPlaneClient` overrides; mocks stay unchanged.
- **Opt-in `UseAgentControlPlaneIdempotency`**, not auto-mounted by `MapAgentControlPlane`. Consumers control pipeline position relative to auth. Same explicit-pipeline stance applied to `AddAgentControlPlaneOpenApi` / `MapAgentControlPlaneOpenApi`.
- **Orleans grain returns an Orleans-serialisable wire type** (`IdempotencyGrainBeginResult`), not the abstraction-level `IdempotencyBeginResult`. Control.Abstractions stays Orleans-free — same discipline as v0.8/v0.9 where Orleans wire types live in the Hosting.Orleans package.
- **Hosting.Orleans ProjectReference added for Control.Abstractions.** First cross-package reference from Hosting.Orleans → Control.*; justified because `IIdempotencyStore` is a neutral dedupe contract that also applies to future non-HTTP interop surfaces (gRPC, A2A). Alternative (putting `IdempotencyOptions` + type in Abstractions so Orleans just reads options via reflection) was rejected — `IdempotencyOptions` carries HTTP-specific fields (`PathExclusions`, `IncludeGetsInExclusion`, `MaxKeyLength`) that don't belong in Abstractions.
- **`OrleansIdempotencyStore` takes TTL via ctor, not `IOptions<IdempotencyOptions>`.** Avoids Orleans → Http.Server cross-package dep for a single TimeSpan field. Consumers who want HTTP-side TTL propagation pass it explicitly: `services.AddOrleansIdempotencyStore(ttl: TimeSpan.FromHours(48))`.

**Verification.**
- Build: `dotnet build Vais.Agents.sln -c Release` clean, 0 warnings, 0 errors.
- Tests: 549 across the whole solution. Core 318 (unchanged); Control.Http.Tests 38 (was 17, +21 — 10 server idempotency + 5 client idempotency + 6 OpenAPI); Hosting.Orleans 66 (was 61, +5 Orleans idempotency store). Others unchanged since v0.10.
- Packages: 22 `.nupkg` + 22 `.snupkg` at `0.11.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.11.0-preview; added idempotency probe (InMemoryIdempotencyStore round-trip: first=New, replay=Replay, mismatch=Mismatch with cached-body-byte=42) + OpenAPI type probe (DefaultDocumentName=v1, 3 OpenAPI types probed) + Orleans idempotency type probe (4 types) + client-options probe (auto-gen opt-in + OrleansIdempotencyStore.DefaultTtl = 24h). Final line: `"All twenty-two Vais.Agents.* 0.11.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean.

**Surprises / findings.**
- **`RS0026 — Do not add multiple overloads with optional parameters` was load-bearing on PR 2.** Adding new overloads to shipped methods like `CreateAsync(manifest, ct=default)` + `CreateAsync(manifest, idempotencyKey, ct=default)` failed the analyzer because both have a default `cancellationToken`. Fix: the NEW overloads' `cancellationToken` parameter is non-optional (no `= default`). Callers pass `default` explicitly when they want default cancellation. Small ergonomic cost, no behavioural change.
- **Orleans serializer code-gen requires wire types to live in the Orleans-attributed package.** `Control.Abstractions.IdempotencyBeginResult` has no `[GenerateSerializer]` (correctly — Orleans dep stays in hosting). When used directly as grain-method return type, Orleans startup threw `OrleansConfigurationException: unserializable or uncopyable types which are being referenced in grain interface signatures`. Fix: introduced `IdempotencyGrainBeginResult` in `Hosting.Orleans` with `[GenerateSerializer]`; the store translates between the two. Same pattern as v0.8's A2A surrogate.
- **`ORLEANS0014` forbids `ConfigureAwait(false)` in grain code** (re-encountered from v0.9 checkpointer). Removed all `.ConfigureAwait(false)` calls from grain method bodies.
- **Full-body-buffer response capture in middleware is straightforward on minimal APIs** — `HttpResponse.Body = MemoryStream` swap + `await next(context)` + copy back. No edge cases with route-handler streaming (future SSE endpoints would opt out via `text/event-stream` content-type check, documented in the middleware).
- **`Microsoft.AspNetCore.OpenApi` 9.x lives in `Microsoft.AspNetCore.App` shared framework** but NOT as an auto-included assembly — needs an explicit `<PackageReference>`. Added `9.0.11` to CPM + `Vais.Agents.Control.Http.Server.csproj`. First and only new package dep of v0.11.
- **The `VaisProblemDetailsOperationTransformer._urnsByStatus` map is the single source of truth for status→URN resolution.** When future Problem Details type URNs land, updating one dictionary keeps spec + runtime in sync automatically.

**Deferred to v0.12+ (explicitly documented as scope-cuts in the pillar plan).**
- Swagger UI / Redoc bundling — we publish a spec, consumers layer UI.
- Client codegen from the spec — consumers run Kiota / NSwag / `openapi-generator-cli` themselves.
- `RedisIdempotencyStore` — InMemory covers dev, Orleans covers durable; Redis when someone asks.
- Idempotency on non-HTTP inbound surfaces (MCP tool calls, A2A tasks) — each has its own native dedupe shape.
- Response header replay beyond status + content-type — Stripe replays a safe-list of custom headers; we don't yet.

**Tag handling.** Annotated `v0.11.0-preview` created on OSS `main` (commit `8b091c1`), not pushed. Tag message summarises the 4 PRs + test counts. All 22 packages at `0.11.0-preview` in the local feed.


### 2026-04-20 — v0.12 SSE streaming Invoke pillar landed (`v0.12.0-preview`)

No new packages — extensions only to `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Control.Http.Server`, `Vais.Agents.Control.Http.Client`. 22 total packages at `0.12.0-preview`. Closes the v0.6-era backlog item: *"SSE streaming Invoke on the HTTP surface (wire format + event taxonomy already specified in the v0.6 HTTP-API design doc; server/client impl deferred)."* Grounded in the spike + findings docs (`plans/actor-agents-oss-v0.12-sse-streaming-invoke-*.md`). Shipped on OSS `main` (not pushed) as two commits: `a4abcaa` (PRs 1-3 feat) + `b39f3e9` (API freeze).

**What landed (semantic).** New `CompletionDelta : AgentEvent` record in Abstractions with 5 fields (`TextDelta` + optional `ModelId` / `PromptTokens` / `CompletionTokens` / `ToolCalls`) — mirrors `CompletionUpdate` shape, joins the closed `AgentEvent` hierarchy (now 10 subtypes). New `IStreamingAiAgent` capability interface with `StreamAsync(userMessage, context, ct) : IAsyncEnumerable<AgentEvent>`. Mirrors v0.9's `IResumableAgentGraph<TState>` precedent — agents opt in to streaming by implementing. `StatefulAiAgent` implements; `OrleansAiAgentProxy` stays unimplemented (streaming-over-Orleans deferred to future pillar). HTTP streaming endpoint returns 501 `urn:vais-agents:streaming-not-supported` when the resolved agent doesn't implement the capability.

**What landed (wire).**
- **Core**: `StatefulAiAgent : IStreamingAiAgent` via new private `StreamEventsCoreAsync` helper that drives the per-turn loop yielding `AgentEvent`s directly (`TurnStarted` → `CompletionDelta` per provider update → `ToolCallStarted`/`ToolCallCompleted` synthesised around dispatch → `GuardrailTriggered` / `InterruptRaised` synthesised from caught exception fields → terminal `TurnCompleted` or `TurnFailed`). Existing `StreamAsync(string, CT) : IAsyncEnumerable<string>` (v0.10) stays source-compat — delegates to `StreamEventsCoreAsync` + filters to `CompletionDelta.TextDelta`. All 32 existing v0.10 streaming tests still pass.
- **Server** (`Control.Http.Server`): `POST /v1/agents/{id}/invoke/stream` emits SSE via channel-based multiplex. Unbounded `Channel<string>` multiplexes agent-produced event frames (`event: name\ndata: {json}\n\n`) + heartbeat-timer comment lines (`: heartbeat <utc>\n\n`, default 15s). Single SSE-writer task drains to `HttpResponse.Body` with per-frame flush. Linked `CancellationTokenSource` on `HttpContext.RequestAborted` coordinates shutdown. `StreamingEndpointAttribute` marker — idempotency middleware (v0.11) gained early-bail check so body-buffering doesn't swallow the stream. `AgentEventSerializer` (internal): 10-case `AgentEvent → (eventName, dataJson)` switch using `JsonSerializerDefaults.Web`. `StreamingInvokeOptions.HeartbeatInterval` (TimeSpan.Zero disables). `ProblemDetailsMapping.StreamingNotSupported` factory + URN const. `VaisProblemDetailsOperationTransformer._urnsByStatus` gained `"501"` entry.
- **Client** (`Control.Http.Client`): `IAgentControlPlaneClient` gains 2 new DIM overloads (`InvokeStreamAsync` text-only + `InvokeStreamEventsAsync` full events) — DIM defaults throw `NotSupportedException` so mocks don't need to implement. `AgentControlPlaneClient` overrides via `System.Net.ServerSentEvents.SseParser.Create<AgentEvent?>` + 10-case `ParseAgentEventFrame` dispatcher (maps `turn.started`/`turn.completed`/`turn.failed`/`tool.started`/`tool.completed`/`tool.replayed`/`guardrail.triggered`/`interrupt.raised`/`handoff.requested`/`delta` to the right concrete subtype). Text-only is `OfType<CompletionDelta>()` projection over full-events. Validates `Content-Type: text/event-stream`; rejects mismatches as `AgentControlPlaneException`. `System.Net.ServerSentEvents 10.0.2` added to CPM + client csproj.

**Design decisions locked during implementation.**
- **SSE `event:` field IS the wire discriminator.** Body JSON carries the concrete record's fields with no type-discriminator property. Consumers dispatch on event name; JSON polymorphism not needed.
- **`CompletionDelta` yielded on EVERY provider update** (including empty-text terminal updates carrying ToolCalls or final token usage). String-returning overload filters empties to preserve v0.10 behaviour; event-returning callers get full metadata observability.
- **Guardrail + interrupt events SYNTHESISED from caught exception fields** (`AgentGuardrailDeniedException.{Layer, Reason}` / `AgentInterruptedException.Interrupt.{InterruptId, Reason}`) rather than captured from the bus. Avoids bus-subscription cross-agent-leak potential; bus publishes the "real" event, yield delivers an equivalent synthesised copy.
- **Dual emission is deliberate.** Bus subscribers + streaming observers each see each event exactly once (different observation channels). Consumers who subscribe to bus AND enumerate the stream would see duplicates — that's a consumer bug, not a library concern.
- **`StreamingEndpointAttribute` metadata opt-out** rather than relying on v0.11's content-type check. The v0.11 check only skipped `CompleteAsync`; it still buffered the response body, which is fundamentally incompatible with SSE's flush-as-you-go semantics. Metadata-based opt-out is the correct layering; v0.11's content-type check stays as a secondary safeguard.
- **Streaming endpoint bypasses `AgentLifecycleManager`** — resolves via `IAgentRegistry.GetAsync` + `IAgentRuntime.GetOrCreate` directly. Documented cost: policy engine + audit log don't run on streaming invocations. Consumers needing those stay on unary `POST /v1/agents/{id}/invoke`. Future pillar can add a streaming method to the lifecycle manager.

**Verification.**
- Build: `dotnet build Vais.Agents.sln -c Release` clean, 0 warnings, 0 errors.
- Tests: 569 across the whole solution. Core 326 (was 318, +8 — `StatefulAiAgentStreamingEventsTests.cs`). Control.Http.Tests 48 (was 38, +10 — `AgentControlPlaneStreamingInvokeTests.cs`). ParityTests 19 (was 17, +2 — `StreamingInvokeParityTests.cs`). Others unchanged since v0.11.
- Packages: 22 `.nupkg` + 22 `.snupkg` at `0.12.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.12.0-preview; added streaming-invoke library-surface probe. Prints `Streaming invoke: events-yielded=4 first-event=TurnStarted last-event=TurnCompleted delta-count=2 urn-streaming-not-supported=urn:vais-agents:streaming-not-supported streaming-types-probed=4 heartbeat-default-seconds=15`. Final line: `"All twenty-two Vais.Agents.* 0.12.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean.

**Surprises / findings.**
- **`StatefulAiAgent` now implements `IStreamingAiAgent` unconditionally.** v0.10 added `StreamAsync(string) : IAsyncEnumerable<string>` as a concrete-class method that throws `InvalidOperationException` if the provider isn't streaming; v0.12 promotes this to an interface implementation. The 501 test needed a custom `NonStreamingAgentRuntime` returning a bare `IAiAgent` (no `IStreamingAiAgent`) because using a non-streaming provider alone wouldn't trigger the capability check at HTTP level — the agent-wrapper type is what matters, not the provider.
- **Idempotency middleware + SSE is fundamentally incompatible** with the v0.11 content-type-based opt-out; the middleware's `MemoryStream`-body-swap happens BEFORE `next()` fires, so the stream gets buffered until completion regardless of what content-type the endpoint sets. v0.12's `StreamingEndpointAttribute` fixes this properly via endpoint metadata — middleware checks `context.GetEndpoint()?.Metadata.GetMetadata<StreamingEndpointAttribute>()` and skips the WHOLE idempotency path for decorated endpoints.
- **`System.Net.ServerSentEvents 10.0.2` was already transitively available** (via SK → OpenAI SDK), but it wasn't added to Control.Http.Client's csproj as a direct dep until now. Adding it explicitly is correct — transitive packaging isn't a stable contract.
- **Channel-multiplex design required a single-reader writer** because `HttpResponse.Body` doesn't support concurrent writes. Agent producer task + heartbeat timer both write frame strings to the channel; the SSE-writer task drains sequentially. Linked CTS + `TryComplete` on the channel writer handles clean shutdown on client abort or agent completion.
- **SSE client round-trip via TestServer required `Microsoft.AspNetCore.App` framework reference on the ParityTests project**, plus 5 project refs (Control.Abstractions / Control.InProcess / Http.Client / Http.Server / Hosting.InMemory) + `Microsoft.AspNetCore.TestHost` package. First-time addition for a non-control-plane-focused test project — justified because the parity test asserts library-level ↔ HTTP-level behavioural equivalence.

**Deferred to v0.13+ (explicitly documented as scope-cuts in the pillar plan).**
- Orleans streaming passthrough. `OrleansAiAgentProxy` still doesn't proxy `StreamAsync`; 501 path covers it. Future pillar if someone asks.
- WebSocket transport. SSE only.
- Resume via `Last-Event-Id`. Mid-stream disconnect = new turn.
- Server-side event-bus fan-out (cluster-wide observability endpoint).
- OpenAPI schema emission for the SSE body — spec declares `text/event-stream` 200 response, consumers doing client codegen need hand-authored SSE parsing.
- Streaming through the lifecycle manager (policy + audit on SSE path).

**Tag handling.** Annotated `v0.12.0-preview` created on OSS `main` (commit `b39f3e9`), not pushed. Tag message summarises the 3 PRs + test counts. All 22 packages at `0.12.0-preview` in the local feed.

---

## v0.13.0-preview — Kubernetes CRD + operator pillar (2026-04-20)

Closes §7 backlog line: *"Kubernetes CRDs + operator (`Vais.Agents.Control.KubernetesOperator`) — declarative agents as native K8s resources; reconciler drives `IAgentLifecycleManager` verbs to match cluster state."*

Pillar plan: [`actor-agents-oss-v0.13-kubernetes-operator-pillar.md`](./actor-agents-oss-v0.13-kubernetes-operator-pillar.md). Spike + findings: [`actor-agents-oss-v0.13-kubernetes-operator-spike.md`](./actor-agents-oss-v0.13-kubernetes-operator-spike.md) + [`actor-agents-oss-v0.13-kubernetes-operator-findings.md`](./actor-agents-oss-v0.13-kubernetes-operator-findings.md).

**Scope:** single `Agent` CRD (`vais.io/v1alpha1`). `AgentGraph` → v0.14 paired with `IAgentGraphRegistry`; `AgentRun` → v0.15 paired with `IAgentRunRegistry` + run-status endpoint.

**What shipped.**
- New NuGet package `Vais.Agents.Control.KubernetesOperator` (22 → 23). `AgentEntity : CustomKubernetesEntity<AgentSpec, AgentStatus>` with `[KubernetesEntity(Group="vais.io", ApiVersion="v1alpha1", Kind="Agent")]` + `[KubernetesEntityShortNames("vagent", "vagents")]`. 10 public types + 6 const string fields on AgentEntity for consumer-code discoverability.
- `AgentEntityController : IEntityController<AgentEntity>` drives the 6-row reconcile decision table. `AgentEntityFinalizer : IEntityFinalizer<AgentEntity>` handles deletion via KubeOps-managed finalizer lifecycle. Every operator → runtime call carries `Idempotency-Key = $"{uid}:{generation}:{verb}"` for v0.11 middleware dedup.
- `ServiceAccountTokenHandler : DelegatingHandler` injects the projected-volume bearer token with TTL+mtime cache invalidation; `ServiceAccountPrincipalMapper : IPrincipalMapper` (optional runtime-side opt-in) maps `system:serviceaccount:<ns>:<sa>` → `AgentPrincipal{Id, TenantId=ns}`. `KubernetesSecretResolver` (internal) fetches K8s Secrets via `IKubernetesClient`; batches by distinct secret name.
- New in-repo-only project `Vais.Agents.Control.KubernetesOperator.Host` (Microsoft.NET.Sdk.Web, IsPackable=false) — Kestrel :8080 + health/readiness probes + `AddAgentKubernetesOperator` wiring. Multi-stage `Dockerfile` (alpine, non-root uid 65532, HEALTHCHECK wget).
- Hand-rolled CRD at `deploy/crds/vais.io_agents.yaml` with `x-kubernetes-preserve-unknown-fields: true` on `.spec` + `.status` (KubeOps 10.3.4 transpiler blocked on TimeSpan in the reachable type graph — deferred to a future tightening pillar).
- Helm chart at `deploy/helm/vais-agents-operator/` — Chart.yaml / values.yaml / _helpers.tpl / serviceaccount.yaml / clusterrole.yaml (6 rule sets: agents full + agents/status + agents/finalizers + secrets get-list-watch + events create-patch) / clusterrolebinding.yaml / deployment.yaml (projected SA-token volume + envvar mapping to `KubernetesOperatorOptions` + probes + security contexts) / crd.yaml (helm.sh/hook pre-install+pre-upgrade weight -10) + README. Top-level `deploy/README.md` quick-start.
- `KubeOps.Operator 10.3.4` + `KubeOps.Abstractions 10.3.4` + `Microsoft.Extensions.Http 10.0.6` added to CPM. `InternalsVisibleTo="Vais.Agents.Control.KubernetesOperator.Tests"` on the library csproj.

**PR shape — 2 commits on OSS `main`.**
- `623a47c feat(k8s): Kubernetes operator pillar (v0.13 PRs 1-3)` — 53 files, +3879. Package skeleton + CRD types + controller + reconcile + finalizer + SA-token handler + secret resolver + DI wiring + Host exe + Dockerfile + CRD YAML + Helm chart + 42 unit + integration-ish tests.
- `3a66e99 chore: API freeze for v0.13.0-preview — promote Unshipped -> Shipped` — 161 PublicAPI entries promoted. 22 existing packages unchanged.

**Decisions.**
- Secret-value injection into the manifest envelope was descoped from this pillar — the shipped runtime's `ISecretResolver` composite expects `secret://scheme/path` URIs and rejects literals, so operator-side resolved values can't flow through `ModelSpec.ApiKeyRef` or `OutboundCredentialRef.Ref` without a runtime-side wire-format change. v0.13 resolves secrets (fails early with `SecretResolutionException` → `ManifestValid=False` condition) but keeps the projection silent on secret values. Runtime-side inline-secret wire format is the operator-side half of a future pillar.
- CRD YAML is hand-rolled with `x-kubernetes-preserve-unknown-fields: true` on `.spec` + `.status` — KubeOps 10.3.4 transpiler fails on TimeSpan in `AutoscalingSpec.IdleTtl` + `RunBudget.MaxDuration`. A future tightening pillar lands either operator-local mirror types (TimeSpan → ISO-8601 string) or an upstream KubeOps fix.
- KubeOps 10.x `IEntityController<T>.ReconcileAsync` / `DeletedAsync` return `Task<ReconciliationResult<TEntity>>`, not `Task`. `ReconciliationResult<T>.Success(entity)` / `.Failure(entity, reason, ex, backoff)` encodes requeue, superseding the earlier `EntityRequeue<T>` delegate-injection approach that the spike drafted.
- Classes (not records) for `AgentSpec` / `AgentStatus` — K8s deserialisation expects parameterless ctor + mutable properties; records would add ~100 auto-synthesised PublicAPI entries for zero wire benefit.
- Introduced `IAgentEntityKubernetesClient` narrow internal abstraction over `IKubernetesClient.UpdateStatusAsync` — the full KubeOps `IKubernetesClient` has ~17 methods; narrow abstraction keeps test-fake surface minimal.

**Numbers.**
- Tests: 611 across the solution (569 v0.12 baseline + 42 new, zero regressions). New test project `Vais.Agents.Control.KubernetesOperator.Tests` — 42 tests across 10 files (3 smoke / 5 SpecHasher / 4 Projector / 5 IdempotencyKey / 5 ServiceAccountToken / 8 Controller / 4 Finalizer / 1 FullReconcileFlow / 2 ServiceCollection / 5 HelmChartShape).
- Packages: 23 `.nupkg` + 23 `.snupkg` at `0.13.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.13.0-preview; new Kubernetes operator library-surface probe. Prints `Kubernetes operator: entity-kind=Agent group=vais.io apiversion=v1alpha1 short-names=[vagent,vagents] phase-enum-values=6 status-conditions=3 secret-refs-supported=True preserve-on-delete-default=false finalizer=vais.io/agent-deactivate operator-types-probed=9`. Final line: `"All twenty-three Vais.Agents.* 0.13.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean.

**Surprises / findings.**
- **KubeOps 10.3.4 transpiler is TimeSpan-intolerant.** The automated `kubeops generate operator` path throws `ArgumentException: The given type System.TimeSpan is not a valid Kubernetes entity` walking the type graph from any CR type that transitively references `TimeSpan`. `AutoscalingSpec.IdleTtl` + `RunBudget.MaxDuration` in Vais.Agents.Abstractions trigger this. Hand-rolling the CRD YAML is the lean MVP — tightening via operator-local mirror types or upstream fix is a future concern.
- **Helm chart ships the CRD twice** — once standalone at `deploy/crds/vais.io_agents.yaml` (kubectl-apply installs) + once chart-embedded at `templates/crd.yaml` (helm-install). The two copies need manual sync pending a future consolidation. Flagged in chart README.
- **`ISecretResolver` expects URIs, not literals.** The operator-side resolved K8s Secret values can't flow directly through manifest fields like `ModelSpec.ApiKeyRef` — the runtime rejects non-`secret://` strings. Documented as a v0.13 limitation; production deployments use `env:` URIs in manifest fields + Helm-mounted K8s Secrets as env vars on the silo pod.
- **Host exe needs `Microsoft.NET.Sdk.Web`** (not `Microsoft.NET.Sdk`) for `WebApplication.CreateBuilder` + minimal-endpoint `/healthz`+`/readyz` probes. First time a Vais.Agents OSS project uses the Web SDK.
- **`IEntityController<T>` return type changed from `Task` (KubeOps 9.x) to `Task<ReconciliationResult<TEntity>>` (10.x).** The `ReconciliationResult<T>.Success` / `.Failure` factories encode requeue + propagate `Entity` back to the harness — cleaner than the 9.x `EntityRequeue<T>` delegate-injection pattern the spike drafted.

**Deferred to post-v0.13 (explicitly documented as scope-cuts).**
- `AgentGraph` CRD → v0.14 pillar (paired with `IAgentGraphRegistry` + `POST/GET/PATCH/DELETE /v1/graphs/{id}` HTTP verbs).
- `AgentRun` CRD → v0.15 pillar (paired with `IAgentRunRegistry` + `GET /v1/agents/{id}/runs/{runId}`).
- Leader election / multi-replica HA.
- In-process co-hosted mode (operator as `IHostedService` in the silo pod).
- Automated kind-in-CI cluster tests.
- Public container image publishing (repo ships Dockerfile; users build + push to their own registry).
- Inline-secret wire format so operator-resolved K8s Secret values flow end-to-end into `ModelSpec.ApiKeyRef` / `OutboundCredentialRef.Ref`.
- Multi-version CR (`v1alpha1` + `v1beta1`).
- Custom operator metrics + traces beyond KubeOps defaults.
- CRD schema tightening once TimeSpan transpiler support arrives (or mirror types land).

**Tag handling.** Annotated `v0.13.0-preview` created on OSS `main` (commit `3a66e99`), not pushed. Tag message summarises the pillar + limitations + deferred items. All 23 packages at `0.13.0-preview` in the local feed.

---

## v0.14.0-preview — Real policy engine (OPA/Rego adapter) pillar (2026-04-20)

Closes §7 backlog line: *"Real policy engine (`Vais.Agents.Control.Policy.Opa`) — OPA/Rego adapter behind the `IAgentPolicyEngine` contract shipped in v0.6."*

Pillar plan: [`actor-agents-oss-v0.14-opa-policy-engine-pillar.md`](./actor-agents-oss-v0.14-opa-policy-engine-pillar.md). Spike + findings: [`actor-agents-oss-v0.14-opa-policy-engine-spike.md`](./actor-agents-oss-v0.14-opa-policy-engine-spike.md) + [`actor-agents-oss-v0.14-opa-policy-engine-findings.md`](./actor-agents-oss-v0.14-opa-policy-engine-findings.md).

**Scope:** first production-grade `IAgentPolicyEngine` adapter. Sidecar-HTTP wire; full-manifest input schema v1; fail-closed default; ~2-day pillar (one library, no deployment artefacts).

**What shipped.**
- New NuGet package `Vais.Agents.Control.Policy.Opa` (23 → 24). `OpaPolicyEngine : IAgentPolicyEngine` with a 6-step evaluate state machine: build input via `OpaInputBuilder` → SHA-256 cache lookup → `POST /v1/data/{DataPath}` with linked timeout CTS → branch on response → parse → cache write / apply FailMode.
- `OpaResponseParser` accepts both wire shapes: `{"result": true|false}` and `{"result": {"allowed": ..., "reason": ...}}`. Adapter doesn't prescribe a Rego idiom.
- `OpaInputBuilder` produces the locked v1 schema `{schemaVersion, operation, principal, agent}` with the full `AgentManifest` via STJ `JsonSerializerDefaults.Web`. `principal` + `agent` nullable per the shipped contract.
- `DecisionCache` — `ConcurrentDictionary` keyed by SHA-256 of canonical-JSON input. 5s TTL default; 1024-entry bound with 25%-oldest-by-timestamp purge; `TimeSpan.Zero` TTL disables.
- Lazy one-shot policy-version log via `GET /v1/status` on first evaluation (non-blocking; guarded by `Interlocked.Exchange` on an int flag).
- `AddOpaPolicyEngine(IServiceCollection, Action<OpaPolicyEngineOptions>?)` DI extension wiring typed `HttpClient` + `TimeProvider` + options + singleton `IAgentPolicyEngine` seam over the transient typed-HttpClient-backed `OpaPolicyEngine`.
- **3 sample Rego policies** under `samples/opa-policies/` — `tenant-scoped-allow.rego` / `model-provider-allowlist.rego` / `budget-cap.rego` + `README.md` documenting patterns and composition.
- **Sidecar overlay doc** at `samples/opa-sidecar/README.md` — ConfigMap-mount + Helm overlay pattern against the v0.13 operator chart, with the known limitation that v0.13 doesn't yet expose `extraContainers` hooks (v0.14.1 polish pillar).
- **Schema contract** at `contracts/opa-input-schema.md` — full v1 schema + response shapes + Rego guard patterns + evolution protocol (additive at v1; breaking changes bump schemaVersion with one-minor dual-ship).
- Cross-links added to `deploy/README.md`.

**PR shape — 2 commits on OSS `main`.**
- `4831e88 feat(policy): OPA policy-engine pillar (v0.14 PRs 1-3)` — 30 files, +2100 (approx). Library + helpers + engine + DI + 33 unit tests + integration project + 6 Testcontainers tests + 3 Rego samples + sidecar overlay + schema doc.
- `910a99d chore: API freeze for v0.14.0-preview — promote Unshipped -> Shipped` — 2 files; 24 PublicAPI entries promoted. 23 existing packages unchanged.

**Decisions.**
- Sidecar HTTP wins over embedded Wasm (.NET Wasmtime tooling less mature) and Envoy ext-authz (overspecified for a first adapter). Future `Vais.Agents.Control.Policy.Opa.Wasm` package if someone needs zero-network-hop eval.
- Wide fixed input schema (full manifest) instead of narrow (just verb + principal). Real policies want model-provider allowlists, tool allowlists, and budget caps. `schemaVersion: "1"` discriminator gates future shape changes.
- `FailMode=Closed` default (deny on OPA error) is enterprise-safe. Dev convenience `FailMode=Open` available.
- 4xx = adapter / config bug → throw `InvalidOperationException`, NOT apply FailMode. Clean separation between config errors ("fix your setup") and runtime errors ("OPA is down; apply FailMode").
- Cache key = SHA-256 of canonical-JSON(input); 5s TTL + 1024-entry bound with 25%-by-timestamp purge. Not strict LRU but good enough for typical workloads; revisit on thrash.
- Policy distribution is documented, not shipped. Adapter is pure-HTTP; consumers pick OPA bundle server, ConfigMap-mounted rego, or Helm-inlined values.
- Test Rego fixtures copy to test-output-dir via `<None CopyToOutputDirectory="PreserveNewest">`. Integration tests mount fixture files into the OPA container via Testcontainers `WithResourceMapping`.

**Numbers.**
- Tests: 644 non-container across the solution (611 v0.13 baseline + 33 new unit tests). New test projects `Vais.Agents.Control.Policy.Opa.Tests` (33 unit — OpaInputBuilder:4 / OpaResponseParser:10 / DecisionCache:7 / OpaPolicyEngine:12) + `Vais.Agents.Control.Policy.Opa.IntegrationTests` (6 integration via Testcontainers). Integration bucket green in ~14s against real `openpolicyagent/opa:1.15.2`.
- Packages: 24 `.nupkg` + 24 `.snupkg` at `0.14.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.14.0-preview; new OPA library-surface probe. Prints `Opa policy engine: base-url=http://opa.test:8181/ data-path=vais/agents/allow timeout-ms=500 fail-mode=Closed cache-ttl-seconds=5 cache-max-entries=1024 fail-mode-values=2 iface-is-singleton=True opa-types-probed=4`. Final line: `"All twenty-four Vais.Agents.* 0.14.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean.

**Surprises / findings.**
- **OPA 1.15.2 is the latest stable** (not 1.0.0 as findings doc speculated pre-spike). Pinned in `OpaContainer`. Docker Desktop's K8s / `openpolicyagent/opa:1.15.2` works out of the box for integration tests.
- **Testcontainers 4.11's `ContainerBuilder()` parameterless ctor is obsolete** — switched to `ContainerBuilder(image)` overload. Small API churn but cleanly caught by CS0618.
- **No `Testcontainers.Opa` shipped on NuGet** (verified 2026-04-20). Hand-rolled `OpaContainer : IAsyncDisposable` around the generic `ContainerBuilder`. Pattern mirrors Redis / Postgres module structure.
- **`RunBudget` has no `MaxTokens` field** — the shipped shape is `{MaxTurns, MaxToolCalls, MaxPromptTokens, MaxCompletionTokens, MaxDuration}`. Rego fixtures + samples use `maxPromptTokens` / `maxCompletionTokens` / `maxTurns` separately.
- **Typed-HttpClient singleton wrapper pattern:** `AddHttpClient<OpaPolicyEngine>(...)` registers the concrete as transient (typed-HttpClient default; `HttpClient` pooling lives in `IHttpClientFactory`), and `TryAddSingleton<IAgentPolicyEngine>(sp => sp.GetRequiredService<OpaPolicyEngine>())` wraps the transient in a singleton for the interface-side resolve. Initial smoketest assertion (reference equality across concrete + interface) failed because it hit the transient behaviour; corrected to assert singleton-ness across repeated interface resolves.
- **Test-class naming for container exclusion:** `OpaPolicyEngineContainerTests` (class name contains "Container") lets `--filter "FullyQualifiedName!~Container"` cleanly exclude it from the non-container test bucket. Matches the Redis/Postgres convention.
- **JsonObject indexer `input["principal"]` returns C# null** (not a `JsonValue` representing null) when the slot holds a null node. Input-builder tests serialise to JSON and re-parse via `JsonDocument` to check `ValueKind.Null` on the wire.

**Deferred to post-v0.14 (explicitly documented as scope-cuts).**
- Embedded Wasm adapter (`Vais.Agents.Control.Policy.Opa.Wasm`).
- Envoy ext-authz gRPC adapter.
- OPA decision log forwarding for observability.
- Bundle server + signature verification.
- Helm chart `opa:` sub-values block integrating sidecar into `deploy/helm/vais-agents-operator/` (currently docs-only overlay).
- Rego linter / policy-CI tooling.
- Policy-version pinning via request headers (advanced safety).
- Bulk evaluation (batch multiple verbs per OPA call).
- Rego authoring guide / style-guide doc.
- Multi-engine composition helper (`CompositePolicyEngine`) — consumer concern.

**Tag handling.** Annotated `v0.14.0-preview` created on OSS `main` (commit `910a99d`), not pushed. Tag message summarises the pillar + limitations + deferred items. All 24 packages at `0.14.0-preview` in the local feed.

---

## v0.15.0-preview — CLI (`vais`) pillar (2026-04-20)

Closes §7 backlog line: *"CLI (`vais apply / get / invoke / logs / signal`) over the HTTP client."*

Pillar plan: [`actor-agents-oss-v0.15-cli-pillar.md`](./actor-agents-oss-v0.15-cli-pillar.md). Spike + findings: [`actor-agents-oss-v0.15-cli-spike.md`](./actor-agents-oss-v0.15-cli-spike.md) + [`actor-agents-oss-v0.15-cli-findings.md`](./actor-agents-oss-v0.15-cli-findings.md).

**Scope:** first first-party CLI over the v0.6 HTTP control plane + v0.12 streaming. Thin wrapper over `IAgentControlPlaneClient`; no new runtime surface. `kubectl`-shape muscle memory (`apply`, `get`, `delete`, `config`). ~2-2.5-day pillar (one library + test project).

**What shipped.**
- New NuGet package `Vais.Agents.Cli` (24 → 25). Ships as a dotnet tool — `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>`. Install via `dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview`.
- 14 subcommands: full 7-lifecycle-verb parity (`apply` / `get` / `invoke` / `delete` / `cancel` / `signal` / streaming `logs`) + 4 config subcommands (`config get-contexts`, `current-context`, `use-context`, `set-context`) + `init` scaffold + `version`.
- `apply -f <file>` reads YAML or JSON via the shipped loaders; tries `CreateAsync` with auto-Idempotency-Key; on status-code 409 falls back to `UpdateAsync` (mirrors `kubectl apply` create-or-update). Supports stdin via `-f -`.
- `get agents [name] -o json|yaml|table` with `--label-prefix` + `--limit`; default = table for list, YAML for single-item (kubectl idiom).
- `invoke <id> --text "..." [--stream] [-o text|json]`. Unary → prints `AgentInvocationResult.Text` or JSON envelope. Streaming → SSE attach via v0.12 `InvokeStreamEventsAsync`, renders events through `EventRenderer` (coloured per subtype, accumulates `CompletionDelta` text into coherent assistant turns). Ctrl-C → exit `130`.
- `logs <id> [--session] [--only <kinds>] [--since <iso>]` — live-run SSE attach. Zero new runtime surface; reuses v0.12 streaming.
- `signal <id> --kind <kind> --payload '{...}'` — inline JSON or `@file.json`. Validates as `JsonDocument` before dispatch.
- `delete <id> [--force]` — `AnsiConsole.Profile.Capabilities.Interactive` detects TTY; prompts confirm interactively, auto-accepts in scripts.
- `cancel <id>` — non-destructive; no prompt.
- `init <name> [-o <file>] [--model <p>] [--mode <m>]` — scaffolds a starter YAML manifest with sensible defaults. Internal `BuildScaffold` extracted for unit-test access.
- `config` branch — 4 subcommands for kubectl-shape context management. Config file at `~/.vais/config.yaml` (Windows: `%USERPROFILE%\.vais\config.yaml`); `VAIS_CONFIG` env override.
- `ClientFactory.Create(config, contextOverride, tokenFlag)` — public builder that resolves active context + applies the token precedence chain (`--token` > `VAIS_TOKEN` > context user's `token` / `tokenFile`) + sets `HttpClient.BaseAddress` + Authorization header. Returns concrete `AgentControlPlaneClient` so callers reach v0.11 idempotency + v0.12 streaming overloads.
- `TokenResolver` (internal) + `VaisConfigFile` (internal, YAML round-trip via `YamlDotNet` + camelCase naming + `OmitNull` default handling) + `ProblemDetailsParser` (internal, maps `AgentControlPlaneException.StatusCode` + `Type` URN to POSIX exit code + stderr markup) + `OutputFormatter` (internal, dispatches `-o` flag through `IAnsiConsole` + STJ + YamlDotNet) + `EventRenderer` (internal, per-`AgentEvent`-subtype Spectre formatter) + `ArgumentFileReader` (internal, curl-style `@file` convention).

**PR shape — 2 commits on OSS `main`.**
- `a7d9a79 feat(cli): CLI (vais) pillar (v0.15 PRs 1-3)` — 21 files, +2400 approx. Library + commands + helpers + 43 unit tests + CPM entries for Spectre.Console.Cli 0.55.0 + Spectre.Console.Testing 0.55.0.
- `e53f34a chore: API freeze for v0.15.0-preview — promote Unshipped -> Shipped` — 2 files; 40 PublicAPI entries promoted. 24 existing packages unchanged.

**Decisions.**
- Framework = `Spectre.Console.Cli` 0.55.0 over System.CommandLine / Cocona. Rendering story (tables / panels / colour) matters for a cloud-CLI user experience; ~200KB dep cost acceptable.
- Verb set maps 1:1 to `IAgentControlPlaneClient` with zero gaps. No new HTTP endpoints required. Adapter-only pillar.
- `vais logs` uses live-run SSE attach via v0.12 — zero new runtime surface. Audit-log query (`vais audit`) + journal replay (`vais logs --runId`) deferred to v0.16+ alongside shipped run-registry endpoints.
- `apply` idempotency via status-code 409 check (not URN match) — no `urn:vais-agents:agent-already-exists` URN ships today.
- Config file = `~/.vais/config.yaml` (kubectl-shape: `clusters + users + contexts + currentContext`) with `VAIS_CONFIG` env override. Auth precedence: `--token` > `VAIS_TOKEN` > context user.
- Exit codes POSIX: `0 / 1 / 2 / 3 / 4 / 130`.
- Package shape = dotnet tool only. `PackAsTool` nupkgs can't be referenced as library deps (NU1212), so the smoketest probe validates the CLI via nupkg file-existence + `DotnetToolSettings.xml` inspection inside the zip instead of library-surface calls.

**Numbers.**
- Tests: 687 non-container across the solution (644 v0.14 baseline + 43 new CLI unit tests). New test project `Vais.Agents.Cli.Tests` with **43 tests** across 9 files:
  - `VaisConfigFileTests` (5) — YAML round-trip + path resolution + env override + find lookups
  - `TokenResolverTests` (5) — flag / env / context / tokenFile / no-source precedence
  - `ClientFactoryTests` (5) — context selection + cluster missing + valid context + context override
  - `ProblemDetailsParserTests` (6) — 401/403-policy/500/409 exit-code mapping + stderr rendering
  - `OutputFormatterTests` (5) — format parsing + JSON/YAML renderers
  - `InitCommandTests` (3) — scaffold defaults + override + handler+systemPrompt stubs
  - `ArgumentFileReaderTests` (4) — plain value pass-through + null pass-through + `@file` read + missing-file throw
  - `LogsCommandFilterTests` (5) — `--only` parse + kebab-case event-kind names + null/empty handling + case-insensitive matching
  - `EventRendererTests` (5) — TurnStarted green prefix + TurnCompleted token counts + delta accumulation + TurnFailed red + no-op flush
- Packages: 25 `.nupkg` + 25 `.snupkg` at `0.15.0-preview` in `artifacts/packages/`.
- Smoketest: refreshed to 0.15.0-preview; new CLI probe file-checks the dotnet-tool nupkg + inspects its `DotnetToolSettings.xml` metadata. Prints `CLI dotnet tool: nupkg-exists=True tool-command=vais entry-point=Vais.Agents.Cli.dll version=0.15.0-preview`. Final line: `"All twenty-four Vais.Agents.* library packages + Vais.Agents.Cli dotnet tool at 0.15.0-preview consumed cleanly from a plain .NET 9 console app."` Ran clean.

**Surprises / findings.**
- **Spectre.Console.Cli 0.55.0 API churn**: `Command.Execute` signature changed to take a `CancellationToken` parameter AND moved from `public override` to `protected override`. All 9 command classes use the new signature + protected access.
- **`AgentControlPlaneClient` doesn't implement `IDisposable`**: removed `using var client = ...` across all 4 verb commands. CLI processes are short-lived; HttpClient leak at process exit is fine.
- **No `urn:vais-agents:agent-already-exists` URN ships**: `apply` falls back to `UpdateAsync` on HTTP status-code 409, not URN match. Simpler + works against the current server.
- **`PackAsTool` nupkgs can't be referenced as library deps**: NU1212 fires when a non-tool project tries to `PackageReference` a `<PackAsTool>true</PackAsTool>` package. The smoketest probes via nupkg-file-existence + `DotnetToolSettings.xml` inspection instead of library-surface calls.
- **Spectre's `CommandAppTester` can't capture output from `AnsiConsole.Console` static calls**: `IAnsiConsole` injection is required for output capture. For `InitCommand` we extracted `BuildScaffold` as an internal static so tests bypass the Console.* state entirely. `OutputFormatter` + `EventRenderer` already inject `IAnsiConsole`, so they test cleanly via `TestConsole`.
- **Manifest loaders live under the flat `Vais.Agents.Control.Manifests` namespace** (not `.Json` / `.Yaml` sub-namespaces, despite the package names). Single `using` reaches both `JsonAgentManifestLoader` + `YamlAgentManifestLoader`.
- **XML crefs to overloaded `IAgentControlPlaneClient.*Async` methods fire `CS0419` ambiguity errors** under `TreatWarningsAsErrors`. All method crefs rewritten as plain `<c>MethodName</c>` text.
- **`GuardrailTriggered.Layer` is an enum (`GuardrailLayer`), not a string**: rendered via implicit `ToString()` in `EventRenderer`.
- **`Handoff` record carries `FromAgent` / `ToAgent`, not `TargetAgentId`**: renderer now prints `from → to`.

**Deferred to post-v0.15 (explicitly documented as scope-cuts).**
- `vais audit` / audit-log query. Needs new HTTP endpoint (`GET /v1/audit`).
- `vais logs --runId <id>` journal replay. Needs run-registry HTTP surface (blocker shared with AgentRun CRD).
- OIDC device-flow auth (`vais auth login`). Polish pillar.
- kubectl-style exec plugin (`users.<n>.exec: ...`). Polish pillar.
- Shell completion (bash / zsh / fish / PowerShell).
- `vais describe <id>` (kubectl-style detailed view).
- `vais port-forward`-equivalent.
- `vais top` (resource usage).
- Standalone self-contained exe (single-file publish).
- Command aliases (`vais ls`, `vais rm`).
- `vais version --check` (remote NuGet version drift check).

**Tag handling.** Annotated `v0.15.0-preview` created on OSS `main` (commit `e53f34a`), not pushed. Tag message summarises the 14-subcommand surface + config file + auth precedence + exit-code contract + deferred items. All 25 packages at `0.15.0-preview` in the local feed.

---

### 2026-04-21 — v0.16.0-preview complete (Phase 3 Pillar A — runtime container + compose + Helm + docs)

**Goal.** Productise the library into a deployable runtime — a `vais-agents-runtime` container image that partners can `docker compose up` or `helm install`, plus the install guides that let them start from zero. Answers Phase 3 user-story US-1: "install the runtime locally in Docker, or in cloud via K8s."

**What landed (branch `033-logging-improvement-read`, four PRs).**

- **PR 1 — Runtime host + composition root + Dockerfile.**
  - `src/Vais.Agents.Runtime.Host/` — `csproj` (`Microsoft.NET.Sdk.Web`, `net9.0`, `Exe`, `IsPackable=false`), `Program.cs` (thin entry), `CompositionRoot.cs` (static helpers split for testability), `RuntimeOptions.cs` (record + `FromEnvironment()` + `EnsureValid()`), `OrleansActiveHealthCheck.cs`, `appsettings.json`, `Dockerfile` (alpine multi-stage, uid/gid 65532, `/var/lib/vais/plugins` VOLUME), `.dockerignore`.
  - `tests/Vais.Agents.Runtime.Host.Tests/` — 7 composition-root guards, all green. Uses NSubstitute for `IGrainFactory` + `IClusterClient` stubs.
  - `Directory.Packages.props` — pins `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Exporter.Console` at 1.15.2 (runtime-host only — library packages stay exporter-agnostic) + `NSubstitute` 5.3.0 (test dep).
  - Full OSS solution: **0 warnings, 0 errors**.

- **PR 2 — docker-compose recipes.**
  - `deploy/compose/` with 5 compose files: 2 bases (`localhost`, `clustered`) + 3 overlays (`opa`, `langfuse`, `otel`). Policies dir with allow-all `example.rego`. 150-line README with 6 base-overlay combinations + 3-replica smoke recipe + teardown + known limitations. `.gitignore` entry for `deploy/compose/data/`.
  - Validated locally: all 7 base-overlay pairings + the 4-way combined overlay pass `docker compose -f ... config --quiet`.

- **PR 3 — Helm chart.**
  - `deploy/helm/vais-agents-runtime/` with `Chart.yaml` (0.1.0, appVersion 0.16.0-preview, kubeVersion >=1.28.0-0, no dependencies — no Redis subchart by design), `values.yaml`, 5 templates (`_helpers.tpl`, `serviceaccount.yaml`, `service.yaml`, `configmap-opa.yaml`, `deployment.yaml`), `NOTES.txt`, 200-line `README.md`.
  - `helm lint` clean. 6 value-combinations render cleanly; missing-connection scenario trips `required` with an actionable message matching the PR 1 unit-test invariant. Full-fat render (clustered + OPA + OTel + Langfuse + plugins + 3 replicas) = 4 resources (ConfigMap + Deployment + Service + ServiceAccount).

- **PR 4 — Install guides + docs sweep.**
  - `docs/guides/install-the-runtime-locally.md` (240-line docker-compose walkthrough).
  - `docs/guides/deploy-the-runtime-to-kubernetes.md` (230-line Helm walkthrough: kind quickstart → production with external Redis → OPA opt-in → observability).
  - `docs/reference/runtime-configuration.md` (full env-var / appsettings / Helm-values cross-reference).
  - `docs/concepts/architecture.md` — added "Runtime tier (v0.16 Pillar A)" section explaining the two-audiences-two-answers split.
  - `docs/reference/packages.md` — version bump to 0.16.0-preview, Runtime container row added, `Hosting.Orleans` key-entry-points corrected (`AddOrleansAgentRuntime` + `ConfigureAgentGrains` + `AddOrleansAgentEventBus`, not the non-existent `AddAgenticOrleansHosting`).
  - `docs/index.md` — Getting-started + Guides + Reference entries for the new docs.

**Surprises / findings.**

- **Spike sketch drifted from reality in 5 places.** The pillar plan was written before the runtime host existed, so several API names it prescribed were not real. Resolved during PR 1 against the actual library surface:
  - `AddAgenticOrleansHosting` — **does not exist**. Composition uses `AddOrleansAgentRuntime` + `AddOrleansAgentEventBus` + `ConfigureAgentGrains` (silo-side grain deps).
  - `AddInProcessAgentControlPlane` — **does not exist**. The runtime host explicitly registers `InMemoryAgentRegistry` + `LoggerAuditLog` + `AgentLifecycleManager` via lambda factory, mirroring `AgentControlPlaneAuthTests`.
  - `SkCompletionProviderFactory` / `MafCompletionProviderFactory` — **do not exist**. Only plain `SkCompletionProvider` / `MafCompletionProvider` classes ship, consumer-instantiated. Deferred to Pillar B when manifest-driven instantiation lands.
  - `ISiloHost.Services` — **removed in Orleans 10.x**. Health check wires against `ILocalSiloDetails` + `ISiloStatusOracle` instead.
  - `AllowAllPolicyEngine` — **wrong name**. Actual allow-all fallback is `NullAgentPolicyEngine.Instance`, baked into `AgentLifecycleManager` when no `IAgentPolicyEngine` is registered.

- **Durability-sidecar ordering discipline locked by 3 unit tests.** All three sidecars (`OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore`) use `TryAddSingleton` — registering them *after* the generic control-plane wiring silently loses to the in-memory defaults (the v0.11 footgun, re-encountered in practice). `Composition_Registers_OrleansBacked_*_Store` tests fail loudly on any regression.

- **Helm chart drifted from pillar plan in 4 places** — all refinements during PR 3: (a) consolidated the separate `grainStorage` knob into `clustering` because Orleans 10.x shares the connection string; (b) added `clustering.existingSecret` for production Secret-based wiring; (c) dropped the `appsettings.Production.json` ConfigMap — env vars cover every v0.16 knob; (d) kind integration test deferred to Pillar F polish (Pillar A doesn't run Docker in CI).

- **Langfuse v2, not v3.** v3's web + worker + clickhouse split is too heavy for a dev compose recipe. Partners wanting v3 fidelity run the Helm chart against a platform-team Langfuse.

- **Orleans 10.x has no production Postgres stream provider.** Clustered + Postgres degrades to in-silo memory streams. Runtime logs a startup WARN; install guide calls it out as a known limitation; Redis is the default for a reason.

- **Invoke returns 501.** v0.16 scaffolds the runtime + full control-plane verb surface; manifest-driven agent instantiation ships with Pillar B / v0.17. Create / Get / Delete + OpenAPI + idempotency all work today. Partners see a clean `501 urn:vais-agents:agent-not-instantiable` with Problem Details explaining the roadmap.

**Deferred to Pillar B–F.**
- Pillar B (v0.17) — manifest-driven instantiation (the 501 goes away).
- Pillar C (v0.18) — plugin loader for `/var/lib/vais/plugins`.
- Pillar D (v0.19) — graph as first-class deployable (`AgentGraph` CRD + `/v1/graphs/*` HTTP).
- Pillar E (v0.20) — cross-runtime agent refs.
- Pillar F — image signing, SBOM, NetworkPolicy template, HPA template, CI image push to GHCR + kind integration test, chiseled-base flip.

**Tag handling.** Annotated `v0.16.0-preview` on the merge commit deferred to user confirmation — tags are destructive enough to warrant an explicit decision.

---

### 2026-04-21 — v0.17.0-preview complete (Phase 3 Pillar B — manifest-driven agent instantiation)

**Goal.** Turn a stored `AgentManifest` into a running `StatefulAiAgent` so `vais invoke` stops returning `501 urn:vais-agents:agent-not-instantiable`. Partner user-story US-4 (pure-YAML agents) end-to-end; enables US-2 once Pillar C lands the plugin loader.

**What landed (branch `033-logging-improvement-read`, four PRs, all uncommitted working tree — same bundle-at-tag pattern as Pillar A).**

- **PR 1 — `Vais.Agents.Runtime.Instantiation` package + translator + OrleansAgentRegistry.**
  - New library package with 8 contracts (`IAgentManifestTranslator`, `IModelProviderFactory`, `IGuardrailFactory`, `IStaticToolRegistry`+builder, `IPromptTemplateRegistry`+builder, `IPromptFileLoader`, `ICompletionProviderPool`), 7 impls (`AgentManifestTranslator`, `CompletionProviderPool`, `StaticToolRegistry`, `PromptTemplateRegistry`, `FileSystemPromptFileLoader` + `ManifestInstantiationException` + 13-URN catalog), 4 DI extensions.
  - `OrleansAgentRegistry` bundled into `Hosting.Orleans` — grain-per-id + directory grain + concrete service with sync `Register(AgentManifest)` / `Remove(string, string)` shims for `AgentLifecycleManager` duck-typing compatibility.
  - 21 unit tests green.

- **PR 2 — Three built-in model-provider factories + four guardrail built-ins.**
  - `OpenAIModelProviderFactory` (MEAI `IChatClient` via OpenAI SDK 2.10.0), `AnthropicModelProviderFactory` (Anthropic.SDK 5.5.1's `AnthropicClient.Messages` implements `IChatClient` directly), `AzureOpenAIModelProviderFactory` (Azure.AI.OpenAI 2.2.0-beta.4).
  - Five guardrail classes in `Core.Guardrails` (LengthCap + RegexAllowlist × 2 layers + RegexDenylist × 2 layers + LlmAsJudge with invariant-culture formatting) + six `IGuardrailFactory` impls keyed on `(Name, Layer)` + `ParamHelpers` utility with URN-stamped error handling.
  - `AddBuiltinModelProviders()` + `AddBuiltinGuardrails()` DI extensions.
  - 25 new unit tests (46 total green).

- **PR 3 — Runtime host wiring + grain seam + A2A manifest extension.**
  - Added `StatefulAgentOptions.CompletionProvider` slot so translator-resolved providers reach the grain; modified `AiAgentGrain` activation to prefer `supplied.CompletionProvider ?? _defaultProvider` (breaking-but-additive ctor change with *REMOVED* entry).
  - `A2ARemoteAgentRef` record + `AgentManifest.A2ARemoteAgents` init-only property in `Abstractions`; translator validates `a2a:*` refs against declaration.
  - `Runtime.Host.CompositionRoot.ConfigureServices` swapped `InMemoryAgentRegistry` → `OrleansAgentRegistry`, wired `AddAgentManifestInstantiator` + `AddBuiltinModelProviders` + `AddBuiltinGuardrails` + `ISecretResolver` + translator-backed `ConfigureAgentGrains`.
  - Three new composition-root guards (10 total Runtime.Host tests).
  - Grain-interface serialisation fix (surfaced via `CrossHostTests`): `IAgentRegistryGrain` payloads switched from `AgentManifest` to JSON strings + `OrleansAgentRegistry.{Serialize,Deserialize}Manifest` helpers; `Abstractions` stays Orleans-free.

- **PR 4 — Docs + integration test + update flow + PublicAPI promotion.**
  - `docs/concepts/declarative-agents.md` — 10-step pipeline, three `SystemPromptSpec` shapes, 12-URN failure table, update semantics.
  - `docs/guides/author-an-agent-in-yaml.md` — end-to-end 8-step walkthrough.
  - `docs/concepts/architecture.md` — new "Manifest instantiation tier" section with ASCII pipeline diagram. "25 packages" → "26 packages".
  - `docs/reference/packages.md` — version bump + Runtime.Instantiation row + Hosting.Orleans updates.
  - `docs/index.md` — Concepts / Guides / Reference entries added.
  - `IAgentManifestInvalidator` contract added to `Control.Abstractions` (layering-friendly); `IAgentManifestTranslator` inherits it. `AgentLifecycleManager.UpdateAsync` + `EvictAsync` wire `IAgentRuntime.Remove(id)` + `IAgentManifestInvalidator.InvalidateAsync(id)` so next invoke re-activates with current manifest. In-flight runs untouched.
  - `ManifestInstantiationIntegrationTests` — two scenarios: apply+invoke-returns-response (end-to-end StatefulAiAgent.AskAsync against a fake provider) + register-v2+invalidate+invoke-picks-up-new-prompt. 12 Runtime.Host tests green.
  - PublicAPI promotion across six assemblies (Abstractions / Control.Abstractions / Control.InProcess / Core / Hosting.Orleans / Runtime.Instantiation). All Unshipped entries merged into Shipped; two `*REMOVED*` ctor entries cleaned out.

**All 20 test projects green. Full solution: 0 warnings, 0 errors.**

**Surprises / findings.**

- **Azure.AI.OpenAI 2.3.x stable doesn't exist on nuget.org.** Nearest was `2.5.0-beta.1`; stepped down to stable `2.2.0-beta.4`. Track for a future stable bump.
- **Anthropic.SDK 5.5.1 `AnthropicClient.Messages` directly implements MEAI `IChatClient`.** No separate bridge extension needed — cleaner than the findings doc anticipated.
- **Windows-locale quirk**: `LlmAsJudgeOutputGuardrail` reason-string formatting was culture-dependent (Russian locale → comma decimal). Forced to `CultureInfo.InvariantCulture`.
- **CrossHostTests broke mid-PR-3** because `IAgentRegistryGrain.RegisterAsync(AgentManifest)` crossed the grain boundary without an Orleans serializer. Fix: JSON-string grain interface + state, keeping `Abstractions` Orleans-free.
- **AgentLifecycleManager's reflection-based duck-typing** onto `Register(AgentManifest)` missed `OrleansAgentRegistry.RegisterAsync`. Fix: sync shims on the concrete registry.
- **Grain-per-agent-provider seam** required modifying `AiAgentGrain` ctor (previously required `ICompletionProvider` from DI as a silo-wide singleton). Made the DI param optional + grain prefers `supplied.CompletionProvider ?? _defaultProvider` — additive for v0.16 hosts that still register a silo-wide provider, enabling v0.17's per-agent providers.
- **Drift from findings §Q10**: `Handler.TypeName` is non-nullable in `AgentHandlerRef`'s record ctor, so the "TypeName null" check the findings sketched is impossible. Pivoted to `Model != null` as the declarative-path switch; semantic preserved (manifests without Model → `501 urn:vais-agents:handler-not-loaded`).
- **Two contracts added beyond findings scope**: `IPromptTemplateRegistry` + `IPromptFileLoader` (`FileSystemPromptFileLoader` default) — without them the "all three `SystemPromptSpec` shapes" acceptance criterion couldn't ship.

**Deferred to v0.17.1 / Pillar B polish.**

- `docs/guides/ship-a-guardrail.md` + `docs/guides/ship-a-custom-model-provider.md` — contract is documented in XML comments + `declarative-agents.md` is enough for partners to extend today.
- `docs/reference/manifest-schema.md` — `AgentManifest` XML-docs + `GET /openapi/v1.json` cover the wire format; full hand-written reference is polish.
- HTTP warnings surface + CLI plumbing + `handler.typeName + Model` both-set WARN at apply time — surfaced as runtime log today; user-facing warning plumbing is polish.
- Lazy `McpToolSource` + `A2ARemoteAgentTool` materialization — translator validates declarations today; tool-wiring ships when a partner use case lands.

**Tag handling.** Annotated `v0.17.0-preview` deferred to user confirmation. Matches the v0.16 protocol: one bundle commit for all four PRs + docs housekeeping on OSS `main`, then tag.

---

### 2026-04-21 — v0.18.0-preview complete (Phase 3 Pillar C — plugin model for code-authored agents)

**Goal.** Extend the runtime so `vais apply` + `vais invoke` work for agents whose behaviour doesn't fit the declarative `Model` + `SystemPromptSpec` + `Tools` + `Guardrails` shape. Partners ship `IAiAgent` DLLs via the container's `/var/lib/vais/plugins` mount; the runtime loads them at silo startup and the translator routes manifests to them by `Handler.TypeName`. Partner user-story US-2 (code-authored agents deployable through the manifest pipeline) end-to-end.

**What landed (branch `033-logging-improvement-read`, four PRs, all uncommitted working tree — same bundle-at-tag pattern as Pillars A + B).**

- **PR 1 — `Vais.Agents.Runtime.Plugins` package + loader + registry + DI extension.**
  - New library package: `AssemblyPluginLoader` (scans subfolders, `Assembly.LoadFromAssemblyPath` into per-plugin `PluginAssemblyLoadContext`), `IPluginHandlerRegistry` + `PluginHandlerRegistry` (singleton, ordinal-comparer lookup), `PluginAssemblyLoadContext` (non-collectible, 10-entry shared-types carve-out at the DI seam — Abstractions / Core / Control.Abstractions + DI/hosting/logging/options/configuration abstractions + MEAI + Polly.Core), `DefaultHandlerFactory<T>` (generic + non-generic) using `ActivatorUtilities.CreateInstance`, `PluginLoaderOptions` (ABI match policy + collision semantics + convention-discovery toggle), `VaisRuntimeAbi.CurrentVersion = "0.18"`, `AssemblyDependencyResolver`-based transitive resolution, `AddAgentPlugins(...)` DI extension, `PluginLoadException` + `PluginUrns` catalog (5 URNs).
  - Abstractions additions: `IAgentHandlerFactory` (primary plugin contract with `HandlerTypeName` + `CreateAsync(manifest, sp, ct)`), `[assembly: VaisPlugin(targetApiVersion, params handlers)]` attribute.
  - `Vais.Agents.Core` addition: `StatefulAgentOptions.Agent` slot (plugin-supplied `IAiAgent` overrides declarative construction).
  - 23 unit tests green — handler registry, default factory, VaisPluginAttribute, load context, loader (missing dir / empty dir / garbage DLL / no-DLL), DI extension.
  - `Directory.Packages.props` added `Microsoft.Extensions.Logging 10.0.6` pin to support the loader's startup logging.

- **PR 2 — Translator plugin-branch + grain wiring + 2 new URNs.**
  - `AgentManifestTranslator.TranslateAsync` — new branch **before** the `Model is null` check: query `IPluginHandlerRegistry.TryGet(manifest.Handler.TypeName)`; on match, `await factory.CreateAsync(manifest, sp, ct)` + stash on `StatefulAgentOptions.Agent`. If `Model` also set, `IManifestApplyDiagnosticsSink.Record(agentId, handler-and-declarative-fields-both-set, ...)` warns at apply time; plugin wins. `OperationCanceledException` propagates unwrapped. Factory throws surface as `urn:vais-agents:plugin-factory-throw` with inner exception preserved; throws are NOT cached (retry re-invokes).
  - `AgentManifestInstantiatorServiceCollectionExtensions.AddAgentManifestInstantiator` — translator ctor gained optional `IPluginHandlerRegistry` + `IManifestApplyDiagnosticsSink` params; DI extension resolves both via `GetService` (null-tolerant).
  - `IManifestApplyDiagnosticsSink` contract added to `Control.Abstractions` (lightweight record-on-warn seam; HTTP control plane can layer a sink in a future polish release).
  - 2 new URN consts on `ManifestInstantiationUrns` (`PluginFactoryThrow`, `HandlerAndDeclarativeFieldsBothSet`).
  - `AiAgentGrain.OnActivateAsync` widened the internal field type from `StatefulAiAgent?` to `IAiAgent?` and prefers `supplied.Agent` verbatim; persisted `SystemPrompt` is re-applied via `IAiAgent.SystemPrompt` setter. Plugin-owned history rehydration stays a factory concern (v0.19+).
  - 13 new `PluginTranslationTests` green (59 total Runtime.Instantiation.Tests).

- **PR 3 — Runtime host wiring + sample + integration test.**
  - `RuntimeOptions.PluginsDirectory` (new) + `FromEnvironment()` reads `VAIS_PLUGINS_DIRECTORY` (unset = default `/var/lib/vais/plugins`; explicit empty string disables the loader).
  - `CompositionRoot.ConfigureServices` — wires `AddAgentPlugins(options.PluginsDirectory)` **before** `AddAgentManifestInstantiator` so the translator's ctor picks up `IPluginHandlerRegistry` at build time. Skipped entirely when `PluginsDirectory` is null/whitespace.
  - `appsettings.json` — documented `Plugins:Directory` key alongside a comment pointing at the `VAIS_PLUGINS_DIRECTORY` env override.
  - `samples/PluginAgentWeather/MyApp.WeatherAgent/` — `net9.0` class library, `[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.WeatherAgent")]`, trivial `WeatherAgent : IAiAgent` with hardcoded "Sunny!" reply. `CopyLocalLockFileAssemblies=true` + `SelfContained=false` for `dotnet publish`. Overlay `Dockerfile.overlay` + end-to-end README with `vais apply` / `vais invoke`.
  - `tests/Vais.Agents.Runtime.Host.PluginFixture/` — test-only fixture class library, ProjectReferenced with `ReferenceOutputAssembly=false` so it builds as a sibling DLL but isn't linked into the test assembly.
  - 6 new `CompositionRootTests` (18 total) — plugin registry registered/not-registered/ordering guards + 3 env-parse tests.
  - 2 new `PluginLoadingIntegrationTests` — end-to-end: stage fixture DLL into temp plugins dir, boot composition root, translate manifest, assert `options.Agent` is the plugin's `WeatherAgent` + `AskAsync("hello") == "Sunny!"`. Second test asserts empty-dir disable path surfaces `handler-not-loaded`. 20 total Runtime.Host.Tests.
  - `samples/README.md` — added `PluginAgentWeather` row (22 samples total).

- **PR 4 — Docs + PublicAPI promotion + milestone log + tag.**
  - `docs/concepts/runtime-plugins.md` (new) — loader design, isolation model, `VaisPluginAttribute` semantics, ABI-matching rules, `IAgentHandlerFactory` contract + `IAiAgent` convention path, lifecycle (per-activation factory calls), full URN catalogue, security posture note (plugins run with host's `IServiceProvider`; trust boundary = runtime container, not sandbox).
  - `docs/guides/package-an-agent-as-a-plugin.md` (new) — 9-step walkthrough from `dotnet new classlib` through overlay-image publish, `vais apply` + `vais invoke`, troubleshooting table.
  - `docs/concepts/declarative-agents.md` — rewrote `handler.typeName` coexistence table to "plugin wins + WARN" semantics; added v0.18 URNs to the failure table; clarified the `declarative` sentinel convention.
  - `docs/concepts/architecture.md` — added Runtime.Plugins node to the layering mermaid; "25 packages" → "27 packages" + new Plugin tier section with ASCII diagram parallel to Pillar B's.
  - `docs/reference/packages.md` — version bump 0.17 → 0.18, 26 → 27 packages, Plugin model (v0.18) section + Runtime.Plugins row, scenario bundle for "Plugin-authored agent shipped as a DLL".
  - `docs/reference/runtime-configuration.md` — added Plugin loader section with `VAIS_PLUGINS_DIRECTORY` + `Plugins:Directory` + disable guidance + startup-log grep hints; updated the composition-root-baked decisions to reflect v0.17 `OrleansAgentRegistry` swap + v0.18 loader ordering.
  - `docs/reference/problem-details-urns.md` — regrouped the catalogue into three sections (core / v0.17 manifest instantiation / v0.18 plugin model); added 2 runtime + 4 startup-log-only plugin URNs with caller-response guidance.
  - `docs/index.md` — new runtime-plugins concept row, package-an-agent-as-a-plugin guide row, 27-package reference line, plugin-authoring quick-map entry.
  - `docs/getting-started/installation.md` — 25 → 27 packages bump (also caught stale v0.17-era wording).
  - PublicAPI.Shipped promotion across six assemblies (Abstractions / Control.Abstractions / Core / Runtime.Instantiation / Runtime.Plugins / Hosting.Orleans). Runtime.Plugins is wholly new — 52 entries promoted. All Unshipped files reset to the `#nullable enable` header.

**All 22 test projects green. Full solution: 0 warnings, 0 errors.**

**Surprises / findings.**

- **`IAgentRegistry` is read-only in the integration test context.** The composition root wires `OrleansAgentRegistry`, which requires a live silo for RPC — the `Substitute.For<IGrainFactory>()` stub doesn't survive the manifest roundtrip. `PluginLoadingIntegrationTests` post-hoc `RemoveAll<IAgentRegistry>() + AddSingleton<InMemoryAgentRegistry>()` to keep the test in-process. Acceptable for an integration test that targets the loader+translator seam, not the registry surface.
- **Test-fixture plugin isolation via `ReferenceOutputAssembly=false`.** The fixture csproj builds as a sibling project (registered in the solution under tests/) but doesn't link into the test assembly — the integration test walks `AppContext.BaseDirectory` up two levels and over into the fixture's bin directory, copies DLLs into a temp plugins dir, and verifies end-to-end load. Cleaner than invoking `dotnet publish` at test time; fixture DLL stays locked after load (non-collectible `AssemblyLoadContext`), so cleanup is best-effort.
- **`ArgumentException.ThrowIfNullOrWhiteSpace` on `AddAgentPlugins`'s `pluginsDirectory`** meant the composition root can't call the extension with an empty string. Guarded at the composition-root level (`if (!string.IsNullOrWhiteSpace(options.PluginsDirectory)) services.AddAgentPlugins(...)`) rather than relaxing the library guard — keeps the library-level contract strict, centralises the disable-mode policy in the host.
- **`RS0026` backcompat analyzer on `PluginLoadException`.** First attempt had both constructors carrying optional `string? pluginPath = null`; analyzer treats that as "multiple overloads with optional parameters". Resolved by dropping the default value — `pluginPath` is required in both ctors.
- **`AgentManifest.Handler` is non-nullable** (required constructor arg), so the plugin branch always has a `TypeName` to query. Declarative manifests use the `declarative` sentinel; `TryGet` returns false → fall-through to v0.17 path.

**Deferred to v0.18.1 / Pillar C polish.**

- HTTP control-plane `IManifestApplyDiagnosticsSink` implementation — sink contract + translator emission work today, but no HTTP layer consumes it yet. Apply-time WARN surfaces only via the translator's exception-less Record call; a polish PR will thread it onto the `vais apply` response body.
- `vais plugins list` CLI subcommand — runtime startup logs the load summary, but the HTTP surface has no enumeration endpoint. `/v1/plugins` + CLI subcommand is on the v0.18.x roadmap.
- Hot reload + collectible `AssemblyLoadContext` — explicit non-goal per Phase 3 pillar plan. Plugin updates = new image tag = rolling deploy.
- Non-.NET plugins (WASM / gRPC / stdio servers) — out of scope for Pillar C; the ABI is .NET-only.

**Tag handling.** Annotated `v0.18.0-preview` on OSS commit `454ec33` (2026-04-21). Two-commit bundle on OSS `main`: `464a8b6` (library layer — Runtime.Plugins package + Abstractions / Control.Abstractions / Core / Runtime.Instantiation / Hosting.Orleans additions + 36 new tests + PublicAPI.Shipped promotion across 5 assemblies) + `454ec33` (runtime host wiring + sample + PluginFixture + 8 new Runtime.Host.Tests + 2 new docs pages + 7 swept docs pages + tag).


---

## v0.19.0-preview — Graph as first-class deployable (2026-04-21)

**Branch:** `033-logging-improvement-read` → merged to `main`.

**Scope.** Pillar D of Phase 3 runtime productisation. Adds a complete management surface for `AgentGraph` manifests: HTTP control plane (`/v1/graphs` REST API), Kubernetes CRD (`kind: AgentGraph`), and four new `vais` CLI commands (`get-graphs`, `delete-graph`, `invoke-graph`, `graph-logs`). `vais apply` now dispatches mixed-kind files (`kind: Agent` + `kind: AgentGraph` in the same YAML stream).

**Four PRs shipped in this milestone.**

- **PR 1 — Graph control plane HTTP API.** `POST /v1/graphs`, `GET /v1/graphs`, `GET /v1/graphs/{id}`, `PATCH /v1/graphs/{id}`, `DELETE /v1/graphs/{id}`, `POST /v1/graphs/{id}/invoke`, `POST /v1/graphs/{id}/invoke/stream`, `POST /v1/graphs/{id}/runs/{runId}/resume`, `POST /v1/graphs/{id}/runs/{runId}/resume/stream`. Typed client additions to `IAgentControlPlaneClient` + `AgentControlPlaneClient`. `IAgentGraphRegistry` / `InMemoryAgentGraphRegistry`. 16 new `GraphControlPlaneEndpointTests` added.
- **PR 2 — Graph manifest loader + `ManifestResource` union type.** `JsonAgentGraphManifestLoader.LoadAllResourcesFromStringAsync` — parses multi-document YAML/JSON streams preserving order, dispatches `kind: Agent` to existing loader. `ManifestResource` discriminated union (`AgentCase` / `AgentGraphCase`). `YamlAgentGraphManifestLoader` delegates after YAML→JSON normalisation with empty-content guard.
- **PR 3 — `AgentGraph` Kubernetes CRD + operator controller.** `AgentGraphEntity`, `AgentGraphSpec`, `AgentGraphStatus`, `AgentGraphPhase`, `AgentGraphHandleRef`. `AgentGraphEntityController` (spec-hash diff, six-phase state machine, three conditions, idempotency key). `AgentGraphEntityFinalizer` (evict-or-preserve). `deploy/crds/vais.io_agentgraphs.yaml`. 13 new operator tests.
- **PR 4 — CLI commands + docs + PublicAPI promotion.** `GetGraphsCommand`, `DeleteGraphCommand`, `InvokeGraphCommand`, `GraphLogsCommand`, `GraphEventRenderer`. `ApplyCommand` updated to dispatch mixed-kind resources. `Program.cs` wired. `YamlAgentGraphManifestLoader.LoadAllResourcesFromStringAsync` empty-content guard. 19 new CLI tests. PublicAPI.Shipped promotion across 8 assemblies (Abstractions, Control.Abstractions, Core, Control.InProcess, Hosting.Orleans, Control.Http.Client, Control.Http.Server, Control.KubernetesOperator). New docs: `concepts/graph-as-deployable.md`, `guides/deploy-a-graph-to-the-runtime.md`. Updated: `reference/cli-subcommands.md`, `concepts/kubernetes-operator.md`.

**All test assemblies green. Full solution: 52 projects, 0 errors, 0 warnings.**

**Surprises / findings.**

- **YAML test fixtures must use `metadata.id` / `metadata.version`, not `spec.id` / `spec.version`.** `JsonAgentGraphManifestLoader.ParseGraph` reads identity fields from the `metadata` block (matching the JSON envelope spec), not `spec`. Initial test YAML used the K8s-CRD convention (`spec.id`) and failed validation. Fixed in `ApplyCommandGraphDispatchTests`.
- **Agent protocol array uses `kind:`, not `type:`.** `ParseProtocols` in `JsonAgentManifestLoader` reads the `kind` field from each protocol item. Test fixture used `type: Http` (mirroring .NET property name) and failed. Fixed.
- **`YamlToJson("")` returns `"null"`, not `"[]"`.** Empty YAML produces a single YAML document with a null root node, which `YamlToJson` serialises as `"null"`. `JsonAgentGraphManifestLoader.ParseAndValidate("null")` treated this as a malformed document. Fixed by short-circuiting empty content in `YamlAgentGraphManifestLoader.LoadAllResourcesFromStringAsync`.
- **Stale binary false-pass.** The `--no-build` flag during iterative test runs masked build failures — 43 tests passed against old binaries before the fresh build revealed the actual test count was 62 (with 3 failures). Always run `dotnet test` without `--no-build` for the final check.

**Tag handling.** Tag `v0.19.0-preview` to be applied to OSS `main` post-merge.

---

## v0.20.0-preview — Cross-runtime graph refs (2026-04-21)

**Branch:** `033-logging-improvement-read` → merged to `main`.

**Scope.** Pillar E of Phase 3 runtime productisation. Adds the ability for a graph node on runtime A to invoke an agent deployed on a different runtime instance (runtime B) by setting `ref.runtimeUrl` in the node manifest. The local orchestration path is unaffected; only nodes with a non-null `RuntimeUrl` take the remote branch.

**Three PRs shipped in this milestone.**

- **PR 1 — Core machinery.** `GraphAgentRef` gains additive `string? RuntimeUrl = null` field. `IAgentRemoteInvoker` interface + `RemoteAgentInvocationException` added to `Vais.Agents.Abstractions`. `HttpAgentRemoteInvoker` (internal) in `Control.Http.Client` — `ConcurrentDictionary` keyed by normalised URL, 2-retry loop (500 ms / 1 000 ms) on 503/504/429, bearer forwarding via `HttpRequestMessage.Headers.Authorization`. `AddAgentRemoteInvoker()` DI extension. Both `InProcessGraphOrchestrator` and MAF `GraphNodeExecutor` gain identical remote branch in `ExecuteNodeAsync`. `AgentGraphLifecycleManager` gains two optional ctor params (`IAgentRemoteInvoker?`, `Func<string?>?`). `Runtime.Host` wires `AddHttpContextAccessor` + `AddAgentRemoteInvoker` + bearer extraction. 27 new tests; 0 warnings.

- **PR 2 — Manifest loader + CRD schema.** `JsonAgentGraphManifestLoader.ParseNodes` parses optional `runtimeUrl` from each node's `ref` object with URI validation (absolute http/https; other schemes or relative URLs throw `AgentManifestValidationException`). `AgentGraphManifestEnvelope.SerializeNodes` emits `runtimeUrl` when set (enables envelope round-trip). YAML loader unchanged — delegates to JSON loader automatically. `deploy/crds/vais.io_agentgraphs.yaml` documents `runtimeUrl: { type: string }` under `spec.nodes.items.properties.ref`. K8s projector unchanged (`spec.Nodes.ToList()` already preserves the field). 7 new tests; 0 warnings.

- **PR 3 — Docs + PublicAPI promotion + tag.** New docs: `docs/concepts/cross-runtime-graphs.md` (concept page) + `docs/guides/compose-a-graph-across-runtimes.md` (step-by-step guide). Sweeps: `docs/concepts/graph-as-deployable.md` (runtimeUrl field + v0.20 feature row), `docs/reference/cli-subcommands.md` (cross-runtime apply note), `docs/concepts/architecture.md` (cross-runtime callout). PublicAPI.Shipped promotion for `Abstractions` (`IAgentRemoteInvoker`, `RemoteAgentInvocationException`) and `Control.Http.Client` (`HttpAgentRemoteInvokerServiceCollectionExtensions`). Both Unshipped files reset. 0 new tests.

**All test assemblies green. Full solution: 52 projects (OSS), 0 errors, 0 warnings.**

**Surprises / findings.**

- **`IAgentRemoteInvoker` package placement.** First attempt placed the interface in `Control.Abstractions`; `Core` doesn't reference `Control.Abstractions`, so `InProcessGraphOrchestrator` couldn't see it. Moved to `Abstractions` (namespace `Vais.Agents`) alongside `AgentHandle` + `AgentInvocationRequest` — the natural home for a remote-invocation surface that both orchestrators + the MAF adapter need.
- **PublicAPI.Shipped.txt `GraphAgentRef` ctor change.** The third positional parameter on the record required updating the 3-arg `Deconstruct` + the ctor signature in `Shipped.txt`. Missed on first pass; RS0016 / RS0017 errors caught it at build time.
- **`IHttpClientFactory` not in scope.** `Microsoft.Extensions.Http` was missing from `Control.Http.Client.csproj` — CS0246 on `IHttpClientFactory`. Added the package.
- **Test stub needs `BaseAddress`.** `HttpAgentRemoteInvoker` sends relative paths (`/v1/agents/…`). The test `StubHttpMessageHandler` needs the `HttpClient` constructed with `BaseAddress` set; without it every send throws "invalid request URI". Fixed in the test helper.
- **K8s projector is a no-op for this feature.** `AgentGraphSpecProjector.ToManifest` copies `spec.Nodes.ToList()` verbatim; `GraphNode` already carries `GraphAgentRef.RuntimeUrl`. No code change needed; added a test to lock the invariant.

**Deferred to post-v0.20.**
- A2A `runtimeUrl` on `GraphAgentRef` (`a2a:` scheme) — v0.21.
- Per-remote Polly config overrides — v0.21.
- OIDC / OAuth 2.0 token exchange for service-to-service identity — v0.21 security hardening.
- SSE streaming cross-remote (`IAgentRemoteInvoker.StreamAsync`) — follow-up PR.

**Tag handling.** Annotated `v0.20.0-preview` on OSS commit (three-commit bundle: PR 1 `a8aa64c` + PR 2 + PR 3).

---

## Phase 3 complete — Pillar F docs + samples (2026-04-21)

**Branch:** `033-logging-improvement-read` → merged to `main` (OSS repo).

**Scope.** Pillar F of Phase 3 runtime productisation. End-to-end docs and six samples that give Phase 3 a coherent getting-started story from "install the runtime" to "cross-runtime graph".

**What landed.**

- **Three new getting-started / tutorial pages:**
  - `docs/getting-started/install-the-runtime.md` — 5-minute quickstart: build image, start localhost mode, verify healthz/readyz, point CLI at it.
  - `docs/getting-started/deploy-your-first-agent.md` — 10-minute walkthrough: write greeter.yaml, apply, get, invoke, stream, update, delete.
  - `docs/tutorials/from-zero-to-graph-in-20-minutes.md` — end-to-end tutorial: start runtime, deploy two agents, compose a graph, invoke unary + streaming, add state bindings.

- **Six end-to-end samples:**
  - `samples/runtime-docker-compose/` — localhost + clustered + OPA/OTel/Langfuse overlays; 0 lines of code (config only).
  - `samples/declarative-agent-yaml/` — greeter + summarizer YAML manifests; `vais apply` + `vais invoke` workflow; 0 lines of C#.
  - `samples/code-agent-plugin/` — `TranslateAgent : IAiAgent` that injects `IHttpClientFactory` from the runtime DI container and calls OpenAI directly; demonstrates constructor injection + overlay Dockerfile pattern.
  - `samples/graph-code-authored/` — multi-agent graph authored in C# using `InProcessGraphOrchestrator<PipelineState>` + typed POCO state + streaming event log; hermetic (no API key, no Docker).
  - `samples/graph-yaml-authored/` — same two-step classifier → responder pipeline as a YAML `AgentGraph` manifest; `vais invoke-graph` + `--stream`.
  - `samples/graph-cross-runtime/` — `enrich-then-summarize` graph with one node's `ref.runtimeUrl` pointing at a second runtime container; step-by-step multi-runtime setup.

- **Doc sweeps:**
  - `docs/concepts/architecture.md` — added v0.19 graph control-plane tier section (parallel to v0.17 manifest instantiation tier and v0.18 plugin tier sections).
  - `docs/reference/packages.md` — bumped version to `0.20.0-preview`; added v0.19 graph deployable + v0.20 cross-runtime refs scenario bundles.
  - `samples/README.md` — added 6 new Phase 3 sample rows to the index table; updated sample count to 27.

**Phase 3 status.** All five pillars (A–E) and Pillar F docs/samples are complete. Phase 3 runtime productisation is **DONE**.

| Pillar | What | Version |
|---|---|---|
| A | Runtime container + docker-compose + Helm | v0.16.0-preview |
| B | Manifest instantiation tier (declarative agents) | v0.17.0-preview |
| C | Plugin loader (code-authored agents as DLLs) | v0.18.0-preview |
| D | Graph as first-class deployable | v0.19.0-preview |
| E | Cross-runtime graph refs | v0.20.0-preview |
| F | End-to-end docs + 6 samples | v0.20.0-preview |

---

## v0.23 Python plugins pillar — PRs 1-4 (2026-04-24)

**Branch:** `main` (OSS repo). Four-PR bundle, all merged.

**Scope.** Pillar E of the v0.23 cycle — Python tool-contributing plugins over MCP stdio, managed by the Vais runtime. Enables `transport: plugin` in agent manifests; Python logic contributes tools indistinguishably from .NET-native tools.

**What landed.**

- **PR 1 — Plugin scanner + descriptor models:**
  - `PythonPluginScanner` — scans `plugin.yaml` + `pyproject.toml` per subfolder; emits `PythonPluginDescriptor`.
  - `PluginYamlDeserializer`, `PyprojectTomlReader` — two-file descriptor assembly.
  - `PythonPluginUrns` — 6 URN constants (`LoadFailed`, `AbiMismatch`, `HandshakeTimeout`, `Exited`, `Unavailable`, `AmbiguousFolder`).
  - `PythonPluginLoaderOptions`, `PythonRestartPolicy`, `PythonPluginAbi`.
  - `Vais.Agents.Runtime.Plugins.Python` project wired to the solution.

- **PR 2 — Subprocess supervisor + MCP handshake:**
  - `PythonSubprocessSupervisor` — spawns `interpreter entrypoint`, wires stdio → `McpClient`, handshake + `tools/list` verification, exponential-backoff restart loop.
  - `PythonPluginHostService` — `IHostedService` + `IPythonPluginHost`; supervises all loaded plugins.
  - `PythonPluginServiceCollectionExtensions.AddPythonPlugins(...)` — DI registration.
  - Test infrastructure: `MockMcpResponder`, `FakeSubprocessHandle`, hermetic supervisor tests.

- **PR 3 — `INamedToolSourceProvider` + manifest translator integration:**
  - `INamedToolSourceProvider` interface in `Abstractions`; `PythonPluginHostService` implements it.
  - `McpServerRef` XML doc updated: `transport: "plugin"` is now a first-class value.
  - `JsonAgentManifestLoader`: `transport: plugin` skips `command`/`url` validation.
  - `AgentManifestTranslator`: resolves `transport: plugin` MCP servers via `INamedToolSourceProvider`; emits `McpServerUnavailable` / `McpToolNotFound` on failure.
  - `ManifestInstantiationUrns.McpServerUnavailable` + `McpToolNotFound` added.
  - 3 new `INamedToolSourceProvider.GetByName` integration tests.
  - 1 new `ManifestLoaderTests` test (`McpServer_Plugin_Transport_Requires_No_Command_Or_Url`).

- **PR 4 — Sample + docs + PublicAPI promotion (this entry):**
  - `samples/PluginAgentResearchPlanner/` — Python plugin with 3 tools (heuristic/hermetic, no LLM), overlay Dockerfile, declarative agent manifest.
  - `docs/concepts/polyglot-plugins.md` — new concept page.
  - `docs/guides/package-a-python-plugin.md` — step-by-step guide.
  - `docs/reference/problem-details-urns.md` — v0.23 section (6 Python URNs + 2 instantiation URNs).
  - `docs/reference/runtime-configuration.md` — Python plugin loader sub-section (`VAIS_PYTHON_PLUGINS_DIRECTORY`).
  - `docs/roadmap/deferred-backlog.md` — "Non-.NET plugins" marked PARTIALLY SHIPPED v0.23.
  - `docs/index.md`, `docs/concepts/runtime-plugins.md`, `samples/README.md` — index updates.
  - Selective PublicAPI promotion: `INamedToolSourceProvider` (Abstractions), `McpServerUnavailable` / `McpToolNotFound` (Instantiation), full Python project (Runtime.Plugins.Python).

**All test assemblies green.**

**Surprises / findings.**

- **`transport: plugin` loader validation blocker.** The JSON manifest loader required exactly one of `command`/`url` for every MCP server. The fix was a sentinel check: if `transport == "plugin"`, skip that validation. YAML loader inherits the fix via delegation.
- **`INamedToolSourceProvider` in Abstractions, not in Runtime.Plugins.Python.** The interface must live in Abstractions so the manifest translator (Runtime.Instantiation) can reference it without taking a dependency on the Python plugin project. The concrete implementation (`PythonPluginHostService`) lives in Runtime.Plugins.Python.
- **Selective PublicAPI promotion required.** `Abstractions/PublicAPI.Unshipped.txt` contained in-flight streaming-journal-replay entries (`CompletionDeltaRecorded`, `ReplayMode`, `IA2AGraphNodeInvoker`) that must NOT be promoted. Only the two v0.23 lines (`INamedToolSourceProvider`, `GetByName`) were moved to Shipped.
- **`uv.lock` cannot be hand-authored.** The hash-pinned lockfile requires `uv lock` to generate — left out of the committed sample with a README note.

**Deferred to post-v0.23.**

- Container smoke test (requires Docker + Python 3.13 + uv on CI) — flagged as pending.
- Secret propagation to Python plugin subprocesses (env var injection) — pillar-plan item.
- Log correlation between .NET runtime and Python subprocess — pillar-plan item.
- Node.js, Go, Rust, WASM plugin runtimes — deferred; see §3 in deferred-backlog.

**Tag handling.** User will apply `git tag v0.23.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-24 — v0.24 Pillar F: First-class Python agents complete

**Goal.** Promote Python plugins from MCP tool servers to full grain-backed agents: durable state, typed wire protocol, Python SDK, hermetic sample, integration tests, and full docs.

**Shipped across three PRs.**

- **PR 1 — Core .NET wiring:**
  - `IPythonAgentChannel` interface extracted from `PythonSubprocessSupervisor` so `PythonAgentShim` can be unit-tested without spawning a subprocess.
  - `PythonAgentShim` implements `IAiAgent` + `IOpaqueStateCarrier`; drives `vais/agent.invoke` and `vais/agent.reset` over the existing stdio MCP channel via `McpClient.SendRequestAsync`.
  - `PythonHandlerKind` enum (`McpToolServer` / `AgentHandler`) added to `PythonPluginDescriptor`; loader reads `kind: agent-handler` from `plugin.yaml` and routes to `PythonAgentShim` instead of `McpToolSourceAdapter`.
  - `PythonPluginLoaderOptions` gains `MaxAgentStateSizeBytes` (default 1 MiB) and `DefaultInvokeTimeoutSeconds` (default 60).
  - New v0.24 URNs: `python-agent-invoke-failed`, `python-agent-invoke-timeout`, `python-agent-state-too-large`, `python-agent-protocol-error`, `python-agent-handler-collision`.
  - 73 unit tests (all passing); new `PythonAgentShimTests` with fake channel + 3 `IOpaqueStateCarrier` tests.

- **PR 2 — Durability + Python SDK + hermetic sample + integration tests:**
  - `IOpaqueStateCarrier` interface added to `Vais.Agents.Core` — grain-agnostic opaque blob pass-through.
  - `AiAgentGrainState.OpaqueState [Id(2)]` — durable Python state field; restored on `OnActivateAsync`, saved after each `AskAsync`, cleared on `ResetAsync`.
  - `vais-agent-sdk` (Python package in `samples/python-agent-sdk/`): `AgentRequest`, `AgentResponse`, `AgentUsage`, `AgentJournalEntry` Pydantic models + `run(invoke)` synchronous-stdin dispatcher (Windows-compatible; 19 pytest passing).
  - `samples/PluginAgentLangGraphResearcher/` — hermetic two-node state machine (plan → summarize, heuristic/no LLM), serves as the hermetic integration test target; `plugin.yaml` with `kind: agent-handler`, `pyproject.toml` with `targetApiVersion = "0.24"`, overlay Dockerfile.
  - `PythonAgentWireTests` — 3 integration tests gated by `VAIS_RUN_PYTHON_PLUGIN_TESTS=1`; use production `PythonSubprocessSupervisor`; all pass in ~15s with Python installed.

- **PR 3 — Docs, PublicAPI promotion, ABI bump:**
  - `docs/concepts/polyglot-agents.md` — new: three-way comparison table, architecture diagram, wire protocol JSON examples, state model, lifecycle, error URNs.
  - `docs/guides/package-a-python-agent.md` — new: 10-step guide, troubleshooting table, state persistence reference.
  - `docs/reference/problem-details-urns.md` — v0.24 Python agent URN table (5 URNs).
  - `docs/reference/runtime-configuration.md` — `VAIS_PYTHON_AGENT_MAX_STATE_BYTES`, `invokeTimeoutSeconds` note, startup log lines.
  - `docs/roadmap/deferred-backlog.md` — PARTIALLY SHIPPED bullet updated to include v0.24; v0.24.x backlog listed.
  - `PythonPluginAbi.CurrentVersion` bumped `"0.23"` → `"0.24"`.
  - `samples/PluginAgentResearchPlanner/research-planner/pyproject.toml` `targetApiVersion` updated to `"0.24"`.
  - All four `PublicAPI.Unshipped.txt` files cleared; new entries promoted to `Shipped.txt` (Abstractions, Core, Hosting.Orleans, Runtime.Plugins.Python).

**Surprises / findings.**

- **Windows asyncio `ProactorEventLoop` incompatibility.** `asyncio.loop.connect_read_pipe` fails on Windows with `OSError [WinError 6]`. The Python SDK runner was rewritten to use a synchronous `for raw in sys.stdin` loop with `asyncio.run()` per message — MCP stdio uses newline-delimited JSON (no Content-Length framing), so synchronous line reading is fully correct.
- **FastMCP cannot register custom method handlers.** The Python side needs a bespoke JSON-RPC dispatcher; FastMCP only handles MCP spec methods. The `vais-agent-sdk` implements its own dispatch table.
- **`McpClient.SendRequestAsync` accepts custom method names.** `.NET` MCP client is not limited to spec methods — `"vais/agent.invoke"` works as-is, no monkey-patching required.
- **Explicit interface implementation required for `IOpaqueStateCarrier` on `PythonAgentShim`.** The grain casts via `is IOpaqueStateCarrier` so explicit impl is fine and keeps `_opaqueState` private to the shim.
- **PublicAPI const value tracking.** Bumping `PythonPluginAbi.CurrentVersion` from `"0.23"` to `"0.24"` requires a `Shipped.txt` update (the analyzer tracks the string value, not just the symbol name).

**Deferred to v0.24.x.**

- Streaming `vais/agent.stream` wire method (per-token SSE-style events from Python agent).
- Hot-reload of Python agent plugin without silo restart.
- Per-tool-call telemetry events from Python subprocess.
- Secret propagation to Python agent subprocesses (env var injection of `secretRefs`).

**Tag handling.** User will apply `git tag v0.24.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-25 — v0.29 Identity pillar: `IAgentIdentityProvider` OIDC adapter complete

**Goal.** Ship the first concrete implementation of the `IAgentIdentityProvider` contract that has been contract-only since v0.4. Covers both directions: inbound JWT Bearer validation and outbound OAuth2 `client_credentials` token acquisition. Works with any OIDC-compliant IdP (Keycloak, Auth0, Microsoft Entra).

**What landed.**

- **`Vais.Agents.Identity.Oidc`** — new standalone NuGet-packagable project:
  - `OidcAgentIdentityOptions` — `Authority`, `ClientId`, `Audience?`, `ValidateAudience` (opt-in, default `false`), `ValidateIssuer` (default `true`), `ClockSkew` (default 30 s).
  - `OidcAgentIdentityProvider` — implements `IAgentIdentityProvider` + `IDisposable`:
    - Inbound: extracts Bearer token from `AgentInvocationRequest.Metadata["authorization"]`, validates via `JsonWebTokenHandler` + JWKS from OIDC discovery; maps `sub` → `Id`, `tid`/`tenant_id` → `TenantId`, `scope`/`scp` → `Scopes`.
    - Outbound: `client_credentials` grant against the discovered token endpoint; per-`(agentId, credentialRef)` in-memory cache with 30-second expiry safety margin using double-checked lock + `SemaphoreSlim` per key (same pattern as `OidcTokenExchangeRemoteIdentityProvider`).
  - `OidcAgentIdentityServiceCollectionExtensions.AddOidcAgentIdentity(configure?)` — registers `IConfigurationManager<OpenIdConnectConfiguration>` (auto-refreshing JWKS), typed `HttpClient`, and `IAgentIdentityProvider` singleton.
  - `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` wired; public API analyzer clean.

- **`Vais.Agents.Abstractions`** — `AgentInvocationMetadataKeys` static class with `Authorization = "authorization"` constant. IdP-neutral; any future inbound adapter reads the same key. Added to `PublicAPI.Unshipped.txt`.

- **`Microsoft.IdentityModel.JsonWebTokens 8.0.1`** + **`Microsoft.IdentityModel.Protocols.OpenIdConnect 8.0.1`** pinned in `Directory.Packages.props` (floor matches the transitive version from `Microsoft.AspNetCore.Authentication.JwtBearer 9.0.0`).

- **`Vais.Agents.Identity.Oidc.Tests`** — 14 unit tests, all green, no network calls:
  - `OidcAgentIdentityProviderInboundTests` (8 tests): generates RSA key pair in-process, creates real JWTs with `JsonWebTokenHandler`, injects `StaticConfigurationManager<OpenIdConnectConfiguration>` — covers principal mapping, Bearer stripping, missing/empty auth header, wrong signing key, expired token (zero clock skew), `scp` claim fallback.
  - `OidcAgentIdentityProviderOutboundTests` (6 tests): uses `RecordingHttpMessageHandler` + `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) — covers token response, form-body field check, cache hit, cache expiry refresh, per-agent isolation, upstream 401 error.

**Surprises / findings.**

- **`AcquireOutboundAsync` receives only `credentialRef` (not `Type`).** The interface passes the secret URI only; there is no `Type` field at the call site. The OIDC provider unconditionally does `client_credentials` — this is correct for the adapter but the constraint is documented in XML doc comments.
- **`ValidateAudience` default must be `false`.** Setting `ValidateAudience=true` without `Audience` throws on every token. Honest default: `false`, with opt-in by setting both `ValidateAudience=true` and `Audience`. The provider enforces `ValidateAudience && !string.IsNullOrEmpty(Audience)` as the effective flag.
- **`IConfigurationManager<OpenIdConnectConfiguration>` for testability.** Injecting the interface rather than the concrete `ConfigurationManager<T>` lets tests pass `StaticConfigurationManager<OpenIdConnectConfiguration>` with pre-loaded JWKS — no mock framework needed, no network.
- **Auth0 / Entra compatibility is free.** OIDC discovery + standard `client_credentials` grant works identically against Auth0 (`{domain}/.well-known/openid-configuration`) and Entra (`{tenant-id}/v2.0/.well-known/openid-configuration`). No IdP-specific code.

**Deferred.**

- Auth0 / Entra integration tests (need real tenant credentials — CI cannot carry them).
- JWKS rotation retry on `SecurityTokenSignatureKeyNotFoundException` (the `ConfigurationManager` auto-refreshes on a 24-hour cadence; explicit `RequestRefresh()` on rotation is a post-v0.29 hardening item).
- `CompositionRoot` opt-in wiring for `AddOidcAgentIdentity` (the adapter ships as a leaf; wiring is consumer-side via `AddOidcAgentIdentity()`).
- `ServiceAccountPrincipalMapper` Helm toggle — still unwired.

**Tag handling.** User will apply `git tag v0.29.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-25 — v0.30 `ServiceAccountPrincipalMapper` runtime-side opt-in

**Goal.** Wire the full JWT bearer-authentication pipeline into the runtime host so that Kubernetes ServiceAccount tokens can authenticate against the control plane without any consumer-side code. Scope B: class move + full env-var + Helm toggle + auth pipeline in `Program.cs`.

**What landed.**

- **`ServiceAccountPrincipalMapper` moved** from `Vais.Agents.Control.KubernetesOperator` (namespace `Vais.Agents.Control.Kubernetes`) → `Vais.Agents.Control.Http.Server` (namespace `Vais.Agents.Control.Http`). Motivation: `KubernetesOperator` depends on `KubeOps.Abstractions` + `KubeOps.Operator`; pulling those into the runtime host via a new project reference would be unacceptable bloat. `Control.Http.Server` is already referenced by `Runtime.Host`. ⚠️ Breaking change for any consumer who imported the old namespace.

- **`RuntimeOptions`** — three new properties parsed from env vars:
  - `JwtAuthority` (`string?`) — `VAIS_JWT_AUTHORITY`: OIDC discovery authority URL.
  - `JwtAudience` (`string?`) — `VAIS_JWT_AUDIENCE`: optional token audience restriction.
  - `UseSaPrincipalMapper` (`bool`) — `VAIS_SA_PRINCIPAL_MAPPER=true`: opt-in SA mapper.

- **`CompositionRoot.ConfigureServices`** — new block after step 4 (HTTP control plane): when `JwtAuthority` is set, registers `ServiceAccountPrincipalMapper` BEFORE `AddAgentControlPlaneJwtAuth` when `UseSaPrincipalMapper=true` (so `TryAddSingleton<DefaultPrincipalMapper>` inside the extension is skipped), then calls `AddAgentControlPlaneJwtAuth(o => { o.Authority = ...; o.Audience = ... })`.

- **`Program.cs`** — conditional `UseAuthentication()` + `UseAuthorization()` + `UseAgentControlPlanePrincipalMapping()` gated on `JwtAuthority` being set; startup log gains `jwt=` field.

- **Helm chart** — `deploy/helm/vais-agents-runtime/values.yaml`: new `auth:` section (`jwtAuthority`, `jwtAudience`, `serviceAccountPrincipalMapper`). `templates/deployment.yaml`: conditional env var blocks for the three auth vars.

- **`PublicAPI.Shipped.txt`** — `KubernetesOperator` entries for `ServiceAccountPrincipalMapper` removed (class moved). `Control.Http.Server/PublicAPI.Unshipped.txt` entries added with new `Vais.Agents.Control.Http` namespace.

- **Tests** — `Vais.Agents.Runtime.Host.Tests` gains:
  - `ServiceAccountPrincipalMapperTests` (8 unit tests): SA sub format → namespace extraction, non-SA sub → passthrough, missing sub → null, scope extraction, `NameIdentifier` claim fallback, truncated SA sub → fallback.
  - `CompositionRootTests` additions (4 tests): JWT auth absent when no authority; auth wired when authority set + default mapper; SA mapper wins when flag set + ordering invariant; `AsyncLocalAgentContextAccessor` wired.

- **Build.** 58 projects, 0 errors, 0 warnings. 35/35 new + existing composition + mapper tests green. 59/59 `KubernetesOperator.Tests` green (no regression).

**Surprises / findings.**

- **`Program.cs` had zero JWT wiring before this pillar.** `IPrincipalMapper` was registered but `UseAuthentication` / `UseAgentControlPlanePrincipalMapping` were never called. The mapper was wired via DI but the middleware chain never ran — discovering this was the root reason Scope B was necessary rather than Scope A (which would have been a no-op without Program.cs changes).
- **Ordering invariant is critical.** `AddSingleton<IPrincipalMapper, ServiceAccountPrincipalMapper>()` must precede `AddAgentControlPlaneJwtAuth(...)` because the extension uses `TryAddSingleton<DefaultPrincipalMapper>`. Reversed order silently falls back to the default. Locked in by the `Composition_ServiceAccountPrincipalMapper_Registered_When_UseSaPrincipalMapper_Set` composition test.
- **`JwtBearerOptions.Authority` is framework-available in `Runtime.Host`.** No package reference needed — `Microsoft.NET.Sdk.Web` includes `Microsoft.AspNetCore.App` shared framework which carries `JwtBearerOptions`.

**Deferred.**

- Auth0 / Entra / Keycloak integration tests for the full runtime-auth path (need real tenant credentials).
- JWKS rotation retry (`RequestRefresh()` on `SecurityTokenSignatureKeyNotFoundException`).
- `AddOidcAgentIdentity` wiring in `CompositionRoot` (the OIDC adapter ships as a leaf library; wiring is still consumer-side).
- mTLS / API-key alternative auth schemes.

**Tag handling.** User will apply `git tag v0.30.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-25 — v0.31 Secret propagation to Python agent subprocesses

**What shipped.**

End-to-end wiring of `spec.secrets` from `plugin.yaml` into Python subprocess environment variables:

- **`PluginYamlSpec.Secrets`** — new `Dictionary<string, string>` property on the YAML model; maps ref-names to `secret://…` URIs. Deserialized by `PluginYamlDeserializer` via YamlDotNet's `IgnoreUnmatchedProperties` path.
- **`PythonPluginDescriptor.SecretDeclarations`** — new `IReadOnlyDictionary<string, string>` body-property (default empty); populated by `PythonPluginScanner` from parsed YAML. Ref-name validation enforces `[A-Za-z_][A-Za-z0-9_]*`; invalid names cause the plugin to be skipped with `urn:vais-agents:python-plugin-load-failed`.
- **`PythonPluginHostService.ResolveSecretsAsync`** — resolves each declaration via the injected `ISecretResolver?`; returns a new descriptor with `SecretRefs` populated as `VAIS_SECRET_<REF>=<value>`. Missing resolver or unresolvable URI → plugin skipped with `urn:vais-agents:python-plugin-secret-resolution-failed`.
- **`DefaultPythonPluginReloader`** — same resolution logic applied after each re-scan; reload aborted with `ScanFailed + SecretResolutionFailed` URN on failure.
- **`PythonPluginServiceCollectionExtensions`** — `AddPythonPlugins` resolves `ISecretResolver?` from DI and passes it to the host and reloader.
- **`PythonPluginUrns.SecretResolutionFailed`** — new URN constant (`urn:vais-agents:python-plugin-secret-resolution-failed`).
- **`Vais.Agents.Runtime.Plugins.Python.csproj`** — added `ProjectReference` to `Vais.Agents.Control.Abstractions` (holds `ISecretResolver` / `SecretNotFoundException`).
- **Sample `PluginAgentLangGraphResearcherLive/plugin.yaml`** — replaced stale deferred-backlog comment with live `spec.secrets: OPENAI_API_KEY: "secret://env/OPENAI_API_KEY"` block.
- **Sample `graph.py`** — updated to read `VAIS_SECRET_OPENAI_API_KEY` from env (via module-level `_OPENAI_API_KEY = os.environ["VAIS_SECRET_OPENAI_API_KEY"]`) and pass it explicitly to `ChatOpenAI(api_key=...)`. The sample now fully exercises the secret injection pipeline rather than relying on env-var inheritance.
- **10 new tests** across `PluginYamlDeserializerTests`, `PythonPluginScannerTests`, and `PythonPluginHostServiceTests`.

**Environment variable naming.** `VAIS_SECRET_<REF>` where `<REF>` is the ref-name verbatim from `plugin.yaml`. The Python subprocess reads `os.environ["VAIS_SECRET_MY_KEY"]` directly.

**Resolution semantics.** Failure is treated as a load failure (plugin skipped / reload aborted), not a silent no-op, to prevent the subprocess starting with missing credentials.

**Deferred.**
- `secret://k8s/…` scheme (Kubernetes Secret direct lookup without the operator) — not needed today; consumers use `secret://env/…` with K8s secrets mounted as env vars.
- Per-secret rotation without silo restart — would require a reload trigger from the secret store; out of scope.

**Tag handling.** User will apply `git tag v0.31.0-preview` after verifying the build. Do not tag autonomously.


---

### 2026-04-25 — v0.32 OPA bundle-server + signature verification

**What shipped.**

Helm chart + samples closing the v0.14 deferred item: _"Policies are loaded from disk / ConfigMap today; there is no signed-bundle pipeline."_ The `Vais.Agents.Control.Policy.Opa` .NET adapter is **unchanged** — bundle distribution is OPA-internal.

**Helm chart `opa.bundle.*` sub-values block (`deploy/helm/vais-agents-runtime/`).**

- `values.yaml` — new `opa.bundle.*` block: `enabled`, `url`, `resource`, `polling.{min,max}DelaySeconds`, `serviceAuthTokenSecret / Key`, `signing.{enabled, keyId, algorithm, existingSecret, existingSecretKey}`. Signing defaults to RS256 (PKI-standard).
- `templates/configmap-opa-config.yaml` (new) — renders an OPA-native `config.yaml` ConfigMap when `opaBundleMode=true` (sidecar mode + bundle enabled). Contains `services:` + `bundles:` + optional `keys:` sections. Secrets (signing key, bearer token) are referenced as `${OPA_BUNDLE_SIGNING_KEY}` / `${BUNDLE_SERVER_TOKEN}` OPA env-substitution placeholders — never embedded in the ConfigMap.
- `templates/_helpers.tpl` — two new helpers: `vais-agents-runtime.opaBundleMode` (true when sidecar + bundle enabled) and `vais-agents-runtime.opaConfigMapName`.
- `templates/deployment.yaml` — OPA sidecar now has two render paths: ConfigMap-mount (default, unchanged) and bundle mode. In bundle mode: args switch to `--config-file=/opa-config/config.yaml`; `opa-config` volume + `opa-tmp` emptyDir (required for `readOnlyRootFilesystem: true` + OPA's temp bundle cache) added; `OPA_BUNDLE_SIGNING_KEY` / `BUNDLE_SERVER_TOKEN` env vars injected via `secretKeyRef` only when the corresponding secrets are configured.
- `templates/configmap-opa.yaml` — updated guard: skips rendering the policy ConfigMap in bundle mode (it is unused and would be orphaned).

**Smoke-tested via `helm template`:** default (no OPA); ConfigMap mode; bundle mode (no signing); bundle mode + signing; bundle mode + signing + bearer-token auth. All render correctly.

**Sample `samples/opa-bundle-server/`.**

- `bundle/vais-agents.rego` — starter deny-closed Rego: allows Invoke/Query/Signal from any known principal; allows Create/Update/Evict/Cancel only from the `ops` tenant.
- `Dockerfile` — nginx 1.27-alpine image; serves `/bundle.tar.gz` on port 8888.
- `nginx.conf` — minimal nginx with ETag support (OPA conditional-request optimisation).
- `sign-bundle.sh` — builds an OPA bundle (`opa build`); optionally signs it (`opa sign` + `openssl genrsa`) and writes the RS256 key pair. Outputs kubectl + Helm commands to wire the public key into Kubernetes.
- `docker-compose.yaml` — local-dev stack: `bundle-server` (nginx) + `opa` (polling the bundle server). Commented-out signing config block for when `--sign` is used.
- `README.md` — full walkthrough: Quick Start (unsigned local dev) → Signed bundle (production) → Authenticated bundle server → Helm values reference → CI publishing pattern.

**`samples/opa-sidecar/README.md`** — updated Known Limitations section; added cross-link to `samples/opa-bundle-server/` noting that the runtime Helm chart now has native `opa.bundle.*` support.

**Deferred (still in §11 OPA/policy polish backlog).**
- `deploy/helm/vais-agents-operator/` OPA integration — separate item.
- OPA decision-log forwarding — separate §Observability item.
- Embedded Wasm adapter, Envoy ext-authz — separate items.

**Tag handling.** User will apply `git tag v0.32.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-25 — v0.33 SSE streaming for cross-runtime invokes

**What shipped.**

`IAgentRemoteInvoker.StreamAsync` — the streaming counterpart to v0.20's `InvokeAsync`, closing
the deferred §2 cross-runtime backlog item.

- **`IAgentRemoteInvoker.StreamAsync`** (new interface method on `Vais.Agents.IAgentRemoteInvoker`).
  Returns `IAsyncEnumerable<AgentEvent>`. Identical parameter list to `InvokeAsync` (runtimeUrl,
  handle, request, bearerToken, cancellationToken). 501 from the remote (Orleans proxy with no
  streaming support) surfaces as `RemoteAgentInvocationException` — callers can detect it via
  `ex.Status == HttpStatusCode.NotImplemented`.

- **`AgentSseParser`** (new `internal static` class in `Vais.Agents.Control.Http.Client`).
  Extracted from `AgentControlPlaneClient.ParseAgentEventFrame` (private method deleted).
  `AgentSseParser.ParseEventFrame` is the single canonical SSE event-name → `AgentEvent` subtype
  switch, shared by both `AgentControlPlaneClient.InvokeStreamEventsAsync` and the new
  `HttpAgentRemoteInvoker.StreamAsync`. No drift risk when new `AgentEvent` subtypes are added.

- **`HttpAgentRemoteInvoker.StreamAsync`** (implemented in `Vais.Agents.Control.Http.Client`).
  POSTs to `/v1/agents/{id}/invoke/stream?version=X`, sets `Accept: text/event-stream`,
  forwards bearer token + identity-provider transformation (mirrors `InvokeAsync`),
  uses `HttpCompletionOption.ResponseHeadersRead` + `System.Net.ServerSentEvents.SseParser`.
  No retry logic (unlike `InvokeAsync`) — a mid-stream failure cannot safely be replayed.

- **Test stub updates.** `StubRemoteInvoker` and `ThrowingRemoteInvoker` in
  `InProcessGraphOrchestrator_RemoteBranchTests` + `StubRemoteInvoker` in
  `InProcessGraphOrchestrator_A2ABranchTests` updated to implement the new interface method.

- **6 new tests** in `HttpAgentRemoteInvokerTests`: full event taxonomy round-trip, bearer token
  forwarding, null bearer = no auth header, path construction with/without version, 501 →
  `RemoteAgentInvocationException`, and unknown event names skipped (forward-compat).

- **`PublicAPI.Unshipped.txt`** — `IAgentRemoteInvoker.StreamAsync` declared.

**Orchestrator wiring.** `InProcessGraphOrchestrator` and `GraphNodeExecutor` (MAF) continue to
call `InvokeAsync` for remote graph-node execution. Threading remote `AgentEvent` objects through
the graph's `AgentGraphEvent` stream requires a separate event-bus design call; deferred.

**Deferred.**

- Orchestrator passthrough of remote `AgentEvent` objects through the graph stream — needs an
  event-bus design call first (see §4 Orchestration backlog).
- `vais get-remote-runtimes` / runtime topology discovery — separate CLI polish pillar (§2).
- `OrleansAiAgentProxy.StreamAsync` passthrough — returns 501 by design; revisit when a
  consumer needs silo-spanning streaming (§2).

**Tag handling.** User will apply `git tag v0.33.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-25 — v0.34 Runtime topology discovery (`vais get-remote-runtimes`)

**What shipped.**

`GET /v1/runtimes` server endpoint + `GetRemoteRuntimesAsync()` client method + `vais get-remote-runtimes` CLI command, closing the §2 deferred backlog item.

- **`IRemoteRuntimeTopology` + `RemoteRuntimeEntry`** (new in `Vais.Agents.Control.Abstractions`,
  namespace `Vais.Agents.Control`). `RemoteRuntimeEntry(string Url, string IdentityMode)` — no
  credentials. Consumers read the topology snapshot via `GetEntries()`.

- **`SimpleRemoteRuntimeTopology`** (`internal` in `Vais.Agents.Control.Http.Client`). Wraps a
  pre-built `IReadOnlyList<RemoteRuntimeEntry>` built from `RemoteRuntimeOptionsMap` at
  `AddAgentRemoteInvoker` call time.

- **`AddAgentRemoteInvoker` DI registration** — both overloads (parameterless + map-based) now
  call `services.TryAddSingleton<IRemoteRuntimeTopology>(...)`. The map-based overload projects
  each `RemoteRuntimeOptions` entry to `RemoteRuntimeEntry(url, identityMode.ToString())`,
  stripping `ClientId`, `ClientSecretRef`, `TokenExchangeEndpoint`, `Audience`,
  `ServiceAccountTokenPath`, `RetryDelays`, etc.

- **`RuntimeInfo` + `RuntimeListResponse`** — declared in both `Vais.Agents.Control.Http.Server`
  (`RuntimeContracts.cs`) and re-declared in `Vais.Agents.Control.Http.Client` (`WireTypes.cs`).
  Same dual-declaration pattern as `AgentApplyResponse`.

- **`GET /v1/runtimes`** endpoint handler in `AgentControlPlaneEndpointRouteBuilderExtensions`.
  Exposed as a standalone `MapRuntimeTopologyControlPlane()` extension (same pattern as
  `MapPluginControlPlane`) and called from `MapAgentControlPlane()`. Resolves
  `IRemoteRuntimeTopology` via `GetService<>` (optional) — returns empty list when not
  registered, never throws.

- **`IAgentControlPlaneClient.GetRemoteRuntimesAsync()`** — default DIM returns an empty
  `RuntimeListResponse` so existing mock implementations compile unchanged.
  `AgentControlPlaneClient` overrides: `GET /v1/runtimes`, deserialise response.

- **`vais get-remote-runtimes` CLI command** (`GetRemoteRuntimesCommand`). Table output:
  URL | IDENTITY MODE columns. Supports `--output table|json|yaml`, `--context`, `--token`.
  Registered in `Program.cs`.

- **4 new endpoint tests** in `RemoteRuntimeTopologyEndpointTests`:
  — 200 with configured runtimes
  — 200 with empty items when topology not registered
  — sensitive-fields leak test (clientSecret, clientId, clientSecretRef, tokenPath, tokenExchangeEndpoint, audience must not appear in response body)
  — response structure contains only Url + IdentityMode

- **`PublicAPI.Unshipped.txt`** updated in 3 projects: `Vais.Agents.Control.Abstractions`,
  `Vais.Agents.Control.Http.Client`, `Vais.Agents.Control.Http.Server`.

**Deferred.**

- Orchestrator passthrough of remote `AgentEvent` objects through the graph stream — needs an
  event-bus design call first (see §4 Orchestration backlog).
- `OrleansAiAgentProxy.StreamAsync` passthrough — returns 501 by design. (SHIPPED v0.35)

**Tag handling.** User will apply `git tag v0.34.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-25 — v0.35 Orleans streaming passthrough (`OrleansAiAgentProxy.StreamAsync`)

**Goal.** Eliminate the 501 `urn:vais-agents:streaming-not-supported` returned by the HTTP
SSE endpoint when the runtime is Orleans-backed. `OrleansAiAgentProxy` should implement
`IStreamingAiAgent` so the HTTP server's `if (agent is not IStreamingAiAgent)` gate passes.

**What landed.**

- **`IAiAgentGrain.StreamAgentAsync`** — new grain interface method returning
  `IAsyncEnumerable<AgentEvent>`, leveraging Orleans 10.x native streaming support
  (confirmed from Microsoft Learn docs). The grain turn is held open for the full
  duration; state is persisted on the terminal `TurnCompleted` or `TurnFailed` event
  (before it is yielded) so persistence is consistent regardless of subsequent failures.
- **`AiAgentGrain.StreamAgentAsync`** — delegates to the inner `IStreamingAiAgent`
  (both `StatefulAiAgent` and `PythonAgentShim` implement it). Sets OTel activity
  status to Error on `TurnFailed`. Logs completion time at Debug.
- **`OrleansAiAgentProxy : IAiAgent, IStreamingAiAgent`** — `StreamAsync` forwards to
  `_grain.StreamAgentAsync`; refreshes `_historyCache` / `_systemPromptCache` in a
  `finally` block (errors suppressed — stale cache is safe) so `proxy.History` reflects
  the post-stream state.
- **`AgentEventSurrogate` extended for `CompletionDelta`** — `AgentEventKind.CompletionDelta = 9`,
  fields `[Id(22)] TextDelta` and `[Id(23)] ToolCallsJson`, `CompletionDeltaSurrogateConverter`
  registered. `JournalEntrySurrogateHelpers.ParseToolCalls` / `SerializeToolCalls` promoted
  to `internal static` for reuse.
- **`IStreamingAiAgent.cs` doc updated** — removed stale claim that Orleans doesn't support
  `IAsyncEnumerable<T>` grain returns; notes v0.35 Orleans 10.x support.
- **Tests** — `StreamingHistorySizeProvider` added to the Orleans test cluster fixture;
  3 new grain integration tests: event-ordering, history-persistence, multi-turn history
  accumulation. All 78 Orleans tests pass; full-solution run clean (0 failures).

**Key finding.** Orleans 10.x natively supports `IAsyncEnumerable<T>` grain method returns
with `CancellationToken` pass-through (confirmed via Microsoft Learn / Orleans cancellation
tokens docs). The prior `IStreamingAiAgent` doc comment was incorrect.

**Deferred.**

- True silo-spanning streaming visibility (Orleans grain turn held for full stream duration —
  currently acceptable; no consumer has reported contention yet).
- Proxy `StreamAsync` does not emit events to `IAgentEventBus` (consistent with
  `IStreamingAiAgent` contract; event-bus fan-out remains a non-streaming concern).

**Tag handling.** User will apply `git tag v0.35.0-preview` after verifying the build. Do not tag autonomously.

---

### 2026-04-25 — v0.36 MAF `GraphNodeExecutor` durable resume parity

**Goal.** Close the gap flagged in the v0.9 deferred-backlog (§4 Orchestration): the MAF
adapter's `MafGraphOrchestrator` did not implement `IResumableAgentGraph<TState>` because
MAF's own `CheckpointManager` uses a different checkpoint format. This pillar wires Vais's
own `IGraphCheckpointer` into the MAF path, bypassing MAF's format entirely.

**What landed.**

- **`GraphMessage.ResumeFromNodeId`** (non-positional `init` property) — carries the
  interrupt-node id from `MafGraphOrchestrator.RunAsync` into the first MAF executor call.
  The targeted executor skips its body and evaluates outgoing edges (MAF's equivalent of
  InProcess's `skipNodeBody` flag). Cleared on every outgoing message so only the targeted
  executor skips; downstream executors run normally.
- **`MafGraphBuilder.Build` — `startNodeId` + `checkpointer` params** — `startNodeId`
  overrides the workflow entry executor; for resume runs this is the interrupt node, so
  `InProcessExecution` delivers the initial `GraphMessage` directly to that executor (proved
  by the spike test `MafGraphBuilder_StartNodeId_Override_Sets_StartExecutorId`). `checkpointer`
  is threaded down to each `GraphNodeExecutor`.
- **`GraphNodeExecutor` checkpointing** — saves checkpoints at the three InProcess parity points:
  interrupt (before halting), end (on completion), per-step (after body execution, inside the
  body guard so no checkpoint is written for the skipped resume iteration). `IGraphCheckpointer`
  is optional (null = v0.9 no-op, backward-compatible).
- **`MafGraphOrchestrator<TState> : IResumableAgentGraph<TState>`** — `ResumeAsync` /
  `ResumeStreamAsync` added; validates checkpoint, rehydrates state bag, splices
  `"resume.payload"`, rebuilds the workflow starting at the interrupt node, emits
  `GraphResumed` at run start. `IGraphCheckpointer` is an optional constructor parameter
  (null = no-op, compatible with all v0.9 callers). `ResumeAsync` and `ResumeStreamAsync`
  throw `InvalidOperationException` if called without a checkpointer.
- **`PublicAPI.Unshipped.txt`** — new constructor overloads, `ResumeAsync`,
  `ResumeStreamAsync`, updated `MafGraphBuilder.Build` signature, `GraphMessage.ResumeFromNodeId`
  property, `*REMOVED*` entries for the old shorter signatures.
- **Tests (5 new):**
  - `MafGraphBuilder_StartNodeId_Override_Sets_StartExecutorId` — spike confirming MAF
    delivers initial message to the non-entry start executor.
  - `Interrupt_Saves_Checkpoint_And_ResumeAsync_Continues_To_End` — MAF run → interrupt →
    load checkpoint → MAF resume → `GraphCompleted`.
  - `Cross_Host_InProcess_Interrupted_Resumes_On_Maf` — InProcess runs to interrupt; MAF
    resumes from the same `IGraphCheckpointer`; final checkpoint `IsComplete = true` with
    correct state.
  - `ResumeAsync_Without_Checkpointer_Throws_InvalidOperationException` — null-checkpointer
    guard.
  - `ResumeAsync_Preserves_State_Through_Interrupt` — state written before interrupt is
    present in checkpoint and in the resumed final state.
  - All 14 MAF tests pass; full-solution run clean (0 failures).

**Key finding.** MAF's `ICheckpointManager` format bridging was never needed. `StartExecutorId`
on `WorkflowBuilder` is the only hook required: building with the interrupt node as entry
causes `InProcessExecution` to deliver the initial message there directly. The
`GraphMessage.ResumeFromNodeId` flag then makes that executor skip its body — identical
semantics to InProcess's `skipNodeBody`. No MAF-internal checkpoint API is touched.

**Deferred.**

- HITL `RequestPort`-based interrupts (deferred v0.9 alongside durable resume; now that
  checkpointing works the remaining gap is the `RequestPort` wiring, which needs a separate
  design for inbound resume payloads via MAF's port surface).
- Per-super-step checkpoint save ordering vs. MAF's `ExecutorCompletedEvent` — currently saved
  inside the executor body (parity with InProcess); if MAF's event stream is re-ordered by
  future versions this may need adjustment.

**Tag handling.** User will apply `git tag v0.36.0-preview` after verifying the build. Do not tag autonomously.
