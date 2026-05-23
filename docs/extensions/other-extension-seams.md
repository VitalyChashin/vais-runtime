# Other extension seams

A catalog of the runtime's named extension points beyond the two gateway middleware chains. Each seam has a concept page (the "why" and the contract), a base interface or abstract class in `Vais.Agents.Abstractions`, and at least one reference implementation in `Vais.Agents.Core` or a shipped package. None of these have a dedicated authoring tutorial in this pass — the contracts are small enough that the concept page plus the source-tree reference impls are usually enough.

## Input middleware

**Interface:** `IAgentInputMiddleware`
**What it intercepts:** the agent's input before any LLM call. Memory injection, retrieval, redaction, system-prompt assembly all hook in here.
**Concept:** [`concepts/session.md`](../concepts/session.md)
**Reference impls:** `HistoryAssembler`, `SystemPromptInjector` (auto-registered by `AddContainerPlugins`)

The runtime owns input shaping. Plugins receive only the prepared context — they don't load memory or fetch retrieval inputs themselves. This is the seam that does the preparation, and it's the seam Phase-2 cognitive primitives plug into.

## Guardrails

**Interfaces:** `IInputGuardrail`, `IOutputGuardrail`, `IToolGuardrail`
**What they intercept:** input messages (before model call), output text (after model call), tool calls (during execution). Each guardrail returns Pass / Deny / Interrupt.
**Concept:** [`concepts/guardrails.md`](../concepts/guardrails.md)

Guardrails predate the gateway middleware chains and remain for backwards compatibility. New code prefers gateway middleware — it has stronger composition semantics and uniform observability. Use guardrails when the check is intrinsic to a single message or tool's semantics (e.g., per-tool privilege check), not when it's a cross-cutting concern.

## Completion providers

**Interfaces:** `ICompletionProvider`, `IStreamingCompletionProvider`
**What they intercept:** the actual LLM call.
**Concept:** [`concepts/architecture.md`](../concepts/architecture.md)
**Reference impls:** `SkCompletionProvider` (Semantic Kernel), `MafCompletionProvider` (Microsoft Agent Framework)

Implement a custom completion provider to integrate a backend the runtime doesn't ship an adapter for — a private LLM, a custom inference service, a fake provider for tests. Most authors reach for `MafCompletionProvider` or `SkCompletionProvider` first; both wrap arbitrary MEAI `IChatClient` implementations transitively.

## Session stores

**Interface:** `IAgentSession`
**What it intercepts:** the agent's conversation history — read, append, reset.
**Concept:** [`concepts/session.md`](../concepts/session.md)
**Reference impls:** `InMemoryAgentSession` (per-process), Orleans-backed grain state (durable)

The default `IAgentSession` for a runtime-hosted agent is the Orleans `AiAgentGrain`'s persistent state. Implement a custom session when you need a non-Orleans backing (e.g., a per-tenant Postgres table you query directly).

## History reducers

**Interface:** `IHistoryReducer`
**What it intercepts:** the chat history before it's sent to the model — compress, summarize, drop turns.
**Concept:** [`concepts/session.md`](../concepts/session.md)
**Reference impls:** `NoopHistoryReducer.Instance`

A history reducer runs every turn before the model call. Implement when you need long-context summarization or windowing. Be careful with state — the reducer is reentrant; per-call state goes in local variables.

## Prompt composers

**Interface:** `ISystemPromptComposer`
**What it intercepts:** assembly of the system prompt from layered contributors.
**Concept:** [`concepts/prompt.md`](../concepts/prompt.md)
**Reference impl:** `AggregatingSystemPromptComposer`

A composer concatenates contributions from `ISystemPromptContributor` instances (each can add a layer — base persona, tool list, retrieval context, etc.). Implement a custom composer if the default aggregation order or formatting doesn't fit. The default impl emits one `SystemSegment` section per contributor so per-contributor breakdowns surface through the section telemetry pipeline.

## Context providers

**Interface:** `IContextProvider`
**What it intercepts:** the per-turn context contribution before the system prompt is finalised — memory, retrieval, tenant policy, time/place hints, observability-only metadata.
**Concept:** [`concepts/context.md`](../concepts/context.md)
**Reference impls:** `KnowledgeRetrievalContextProvider` (emits a `retrieval.docs` SystemSegment from any `Microsoft.Extensions.VectorData` store).

Implement to surface anything that should ride in the LLM request without being part of the conversation history. Each provider returns a `ContextContribution` carrying a typed `Section[]`; the resolver enforces id uniqueness and ordering, the packer drops sections under budget pressure, the flattener collapses the surviving list into a `CompletionRequest` for the provider. See the [wire-context-sections tutorial](../guides/wire-context-sections.md) for the section model end-to-end.

## Section window packers

**Interface:** `ISectionWindowPacker`
**What it intercepts:** the resolved section list before the flattener — applies a `SectionBudgetContext` (char or token cap) by dropping or truncating sections.
**Concept:** [`concepts/context.md`](../concepts/context.md#section-window-packer)
**Reference impls:** `DefaultSectionWindowPacker` (priority-driven shed; truncation only on `SystemSegment` / `Metadata`); `LegacyPackerAdapter` (wraps an `IContextWindowPacker` so pre-v0.5 packers keep working).

Implement when the default "shed by descending `Budget.Priority`, size as tiebreak" rule isn't enough — e.g., a tokenizer-aware packer that prefers truncating one large RAG section over dropping a smaller policy section, or one that consults runtime cost signals.

## Section telemetry sinks

**Interface:** `ISectionTelemetrySink`
**What it intercepts:** the per-turn `SectionTelemetrySnapshot` (every section's chars / tokens / ratio / outcome plus aggregate budget summary) between the packer and the flattener.
**Concept:** [`concepts/context.md`](../concepts/context.md#observability)
**Reference impls:** `LoggingSectionSink` (structured Information log), `OtelSectionSink` (`vais.request.*` Activity tags), `LangfuseSectionEnrichment` (`langfuse.section.*` tags + JSON breakdown), `PrometheusSectionSink` (6 metrics — chars / tokens / ratio histograms, outcome counter, budget gauge), `EventBusSectionSink` (publishes `RequestSectionsBuilt` on `IAgentEventBus`).

Implement when you need a custom audit pipeline, an eval guard ("fail any run missing a `cognition.diee.goal_stack` section"), or to export sections into a downstream system the shipped sinks don't cover. Sink failures are logged at `Warning` and swallowed — telemetry never breaks the data path (architecture principle P9).

## Graph predicate operators

**What they intercept:** evaluation of conditional edge predicates in graph manifests (`condition: { op: equals, key: ..., value: ... }`).
**Reference:** [`reference/graph-predicate-operators.md`](../reference/graph-predicate-operators.md)
**Built-in operators:** `equals`, `notEquals`, `contains`, `startsWith`, `endsWith`, `regex`, `isNull`, `isNotNull`, `inSet`, `notInSet`

Extend the vocabulary by registering a custom `IGraphEdgePredicate` implementation. Useful when your routing logic outgrows simple key comparisons — e.g., evaluating a Power Fx expression, calling a remote service, applying a learned classifier. The reference operator set covers the common cases; extension is rare.

## Policy engines

**Interface:** `IAgentPolicyEngine`
**What it intercepts:** control-plane verbs (`Apply`, `Get`, `Delete`, `Invoke`, …).
**Concept:** [`concepts/opa-policy-engine.md`](../concepts/opa-policy-engine.md)
**Reference impl:** `Vais.Agents.Control.Policy.Opa` (OPA-backed Rego policies)

The OPA policy engine is the production-ready choice. Implement a custom `IAgentPolicyEngine` only when OPA doesn't fit — e.g., delegation to an in-process .NET policy library, or a custom decision cache strategy.

## Event subscribers

**Interfaces:** `IAgentEventBus`, `IAgentGraphEventBus`
**What they intercept:** per-agent invocation events (`InvocationStarted`, `InvocationCompleted`, `Error`) and graph lifecycle events (`GraphStarted`, `NodeStarted`, `NodeCompleted`, `GraphFailed`, `GraphCompleted`).
**Reference:** [`reference/events.md`](../reference/events.md)
**Reference impls:** `InMemoryAgentEventBus`, `OrleansAgentEventBus` (cross-silo via Orleans streams)

Subscribe to react to events without being on the call path. Audit logs, custom dashboards, alerting, side-channel automation — all good fits. Subscriber exceptions are logged at WARN and swallowed (per architectural principle P9); the bus is a fan-out mechanism, not a transaction.

## Picking the right seam

| You want to… | Seam |
|---|---|
| Gate every LLM call | [LLM gateway middleware](author-an-llm-gateway-middleware.md) — DI or hot-published `kind: Extension` (`llmGatewayMiddleware` seam) |
| Gate every tool call | [MCP gateway middleware](author-an-mcp-gateway-middleware.md) — DI or hot-published `kind: Extension` (`toolGatewayMiddleware` seam) |
| Inject memory or retrieval before the model sees the prompt | Input middleware |
| Add a named contribution (tenant policy, RAG hits, observability-only metadata) to every turn | Context provider |
| Customise how the section list fits a budget | Section window packer |
| Wire the per-section breakdown into a custom backend or eval pipeline | Section telemetry sink |
| Enforce a per-tool semantic rule | Guardrail |
| Integrate a non-SK / non-MAF LLM backend | Completion provider |
| Persist conversation state in your own store | Session store |
| Compress or window long histories | History reducer |
| Restructure how the system prompt is assembled | Prompt composer |
| Add a custom routing condition to graph manifests | Graph predicate operator |
| Gate control-plane verbs with a custom policy | Policy engine |
| React to agent or graph events asynchronously | Event subscriber |

When the seam isn't obvious: middleware first. The two gateway chains have the best composition semantics; reach for a more specialized seam only when middleware can't express what you need.

## Memory middleware — a deferred case

A first-class memory middleware (vector retrieval, summarization, working-memory eviction) is **not** in the runtime today — it's a Phase 2 cognitive-architecture concern. The `IAgentInputMiddleware` seam exists and is the right place to implement it; a public implementation is deferred. If you need memory now, you can:

- Implement your own `IAgentInputMiddleware` that loads retrieval context from a vector store and prepends it to the system prompt.
- Use the runtime's input-middleware seam directly without waiting for a shipped memory primitive.

The contract is small and the seam is stable; the deferral is about which features ship in OSS, not whether the seam supports them.

## Next

- **[Author an LLM gateway middleware](author-an-llm-gateway-middleware.md)** — the most common authoring task.
- **[Author an MCP gateway middleware](author-an-mcp-gateway-middleware.md)** — same shape for tool calls.
- [Concepts → Execution loop](../concepts/execution-loop.md) — where these seams sit in the agent's turn.
- [Concepts → Architecture](../concepts/architecture.md) — the 32-package layering and which seam lives where.
