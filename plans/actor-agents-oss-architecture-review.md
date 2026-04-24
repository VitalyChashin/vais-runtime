# Architectural review — agent harness foundations ("Kubernetes for agents")

Companion doc to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md). Created 2026-04-18.

---

## Scope and non-goals

**Scope.** Decide the abstraction surface required before we start implementing agent-harness components (execution loop, tools, memory, context management, prompt construction, guardrails) and the cloud control plane that schedules and runs them. Write the contracts; defer the components themselves.

**Non-goals.** No component implementations. No manifest schema. No concrete guardrail rules. No memory-store impl. No Python SDK design. No control-plane HTTP API. No pivot to `net10.0`. This review produces **abstractions + a task list**.

**Method.** Three parallel prior-art surveys plus a full inventory of the current public surface. Surveys captured *abstraction shapes* — interfaces converged on by the state of the art — not feature checklists. Synthesis below maps those shapes onto what we already have, what to add, what to rename, and what to defer.

---

## 0. Executive summary (TL;DR)

1. **Current surface is a solid execution-primitive foundation but is missing the *conversation keying* abstraction that every other framework has converged on** (Thread / Session / Run). We carry history on `IAiAgent` directly, which conflates "agent identity" with "conversation identity" and blocks multi-session-per-agent, branch/fork, and checkpoint-replay patterns.
2. **Six harness pillars** — execution loop, tools, memory, context, prompt, guardrails. We cover ~½ with minimum-viable abstractions (tools, streaming, filters, RAG retriever). Missing: session/memory split, context providers, step/turn budgets, prompt templates, typed guardrail layers, tool approval.
3. **The cloud runtime ("K8s for agents") needs only a handful of new abstractions now**: an `AgentManifest` record, a minimal lifecycle verb set (`Create / Invoke / Signal / Cancel / Update / Evict`), and an identity contract. Everything else we already have via Orleans (placement, persistence, streams) — we just haven't exposed it as a control-plane surface.
4. **Protocol alignment is narrow**: MCP for agent↔tool, A2A for agent↔agent. Both have official .NET SDKs at GA (MCP 1.2, A2A 1.0). Everything else (NLIP, AGNTCY ACP, Agent Protocol, IBM ACP) is either merged into A2A, speculative, or abandoned. Alignment cost is two adapter packages, not a custom wire protocol.
5. **Polyglot is an emergent property of getting the protocols right, not a separate workstream.** If we expose agents via A2A (inbound) and allow A2A/MCP as tool sources (outbound), Python/TS agents plug in as HTTP peers — no VAIS2-specific shim.
6. **Breaking-change budget.** Roughly 20 abstraction changes land in `v0.4.0-preview`. Twelve are additive; eight require `PublicAPI.Unshipped.txt` entries that replace or extend shipped types. `v0.3.0-preview` stays as the stable snapshot during design-partner feedback.

---

## 1. Baseline — the current public surface

Full inventory is in the repo at `oss/agentic/src/**/PublicAPI.Shipped.txt`. Summary for this review:

### Abstractions package (9 interfaces, 11 records, 1 enum, 1 delegate)

| Category | Types |
|---|---|
| **Provider** | `ICompletionProvider`, `IStreamingCompletionProvider` |
| **Agent** | `IAiAgent`, `IAgentRuntime` |
| **Context** | `IAgentContextAccessor`, `AgentContext` |
| **Events** | `IAgentEventBus`, `AgentEvent` (+ `TurnStarted/Completed/Failed`) |
| **Middleware** | `IAgentFilter` |
| **Orchestration** | `IAgentOrchestrator`, `OrchestrationStep`, `AgentParticipant` |
| **Tools** | `ITool`, `IToolRegistry` |
| **Knowledge** | `IKnowledgeRetriever`, `KnowledgeChunk` |
| **Usage** | `IUsageSink`, `UsageRecord` |
| **Data** | `ChatTurn`, `CompletionRequest/Response/Update`, `AgentChatRole` |

### Core package

Default in-process implementations: `StatefulAiAgent`, `StatefulAgentOptions`, `AsyncLocalAgentContextAccessor`, `NullAgentEventBus`, `NullUsageSink`, `SequentialOrchestrator`, `RoundRobinOrchestrator`, plus `AgenticDiagnostics` / `AgenticTags` / `AgenticMetrics` (OTel GenAI conventions).

### Supporting packages

- `Hosting.InMemory` — `InMemoryAgentRuntime`, `InMemoryAgentEventBus`
- `Hosting.Orleans` — `AiAgentGrain`, `OrleansAgentRuntime`, `OrleansAgentEventBus`, `OrleansAgentContextAccessor`
- `Ai.SemanticKernel` — `SkCompletionProvider`, `SkToolBinder`
- `Ai.MicrosoftAgentFramework` — `MafCompletionProvider`, `MafToolBinder`
- `Persistence.VectorData` — `VectorStoreKnowledgeRetriever<TKey,TRecord>`, `KnowledgeRetrievalFilter`
- `Persistence.Redis` / `Persistence.Postgres` — Orleans clustering + grain storage + (Redis only) streams
- `Observability.OpenTelemetry` / `Observability.Langfuse` — `OpenTelemetryUsageSink`, `LangfuseEnrichmentFilter`

### What the current surface is good at

- **Stack neutrality.** Nothing in `Abstractions` imports SK/MAF/Orleans. Adapters do the translation.
- **Observability.** OTel GenAI conventions baked in at the ActivitySource/Meter level.
- **Extensibility.** Filters, event bus, context accessor, retriever, orchestrator, usage sink are all interfaces.
- **Record-based data types.** Structural equality + STJ round-tripping for free.

### What's missing (forward-references to §3)

- Named **conversation / session** primitive (universal in prior art).
- **Short-vs-long-term memory** split (CrewAI + Mastra are the high-water mark here).
- **Context providers** layered per turn (MAF's `AIContextProvider`).
- **Step / turn / cost budgets** on the execution loop.
- **Tool approval / HITL** and **checkpoint / resume** hooks.
- **Prompt template** abstraction.
- **Typed guardrail layers** (MAF's three-layer Agent/Function/Chat middleware).
- **Handoff** as first-class orchestration primitive.
- **Agent manifest** + **lifecycle verbs** for the control plane.
- **MCP / A2A** adapters.

---

## 2. Prior-art lessons distilled

Full surveys archived in the parent conversation. Two compressed tables; the rest of the doc cites them by item.

### 2.1 Where agent frameworks converge (table-stakes shapes)

| Convergence | Evidence | Our current state |
|---|---|---|
| **Agent + Tool + Thread triad** | SK (`Agent`/`Tool`/`AgentThread`), MAF (`AIAgent`/tool/`AgentSession`), AutoGen AgentChat, CrewAI, Mastra, OpenAI Assistants | ✂️ **Missing Thread.** `IAiAgent.History` is the only surrogate. |
| **Tool schemas derived from language artifacts** | Python type hints + docstring (everywhere non-.NET); `[KernelFunction]` + `[Description]` (SK); Zod (Mastra) | ⚠️ We require consumers to hand-build `JsonElement ParametersSchema`. No typed helper. |
| **Streaming as a separate same-shape method** | Universal (`InvokeStreamingAsync`, `run_stream`, `stream`) | ✅ `IStreamingCompletionProvider` + `StatefulAiAgent.StreamAsync` |
| **Thread is the short-term memory boundary** | SK `AgentThread`, MAF `AgentSession`, LangGraph `thread_id`, Mastra `threadId`, OpenAI `thread_id` | ✂️ Implicit only via `IAiAgent.History` |
| **Graph orchestration for complex flows** | LangGraph, MAF Workflows, AutoGen GraphFlow | ✂️ We have Sequential + RoundRobin only |
| **Handoff as named primitive** | OpenAI Swarm/Agents SDK, SK `Handoff`, MAF `HandoffBuilder`, AutoGen `Swarm`, CrewAI hierarchical | ✂️ Missing |
| **Checkpoint + HITL coupled** | LangGraph (`interrupt()` + checkpointer), MAF Workflows (`RequestInfoExecutor`), AutoGen, CrewAI `human_input` | ✂️ Missing |
| **Agent-as-tool composition** | Pydantic-AI, Mastra, MAF, CrewAI all document it | ⚠️ Possible manually via `ITool` wrapping; no first-class helper |

### 2.2 Where agent frameworks diverge (design choices we must make)

| Divergence | Options seen | Our lean |
|---|---|---|
| **Memory model** | (a) Opaque per-agent thread (SK/MAF/AutoGen/Pydantic-AI); (b) Typed state machine (LangGraph); (c) Explicit STM/LTM/Entity taxonomy (CrewAI/Mastra) | **(a) + escape valve to (c)** — keep a simple session, add optional `IMemoryStore` with STM/LTM tags. Avoid (b) — it demands a graph runtime. |
| **Prompt composition** | Plain string (MAF/AutoGen/LangGraph/Mastra) vs template engine (SK Handlebars/Liquid) vs decorator (Pydantic-AI) | **Plain string + pluggable `IPromptTemplate`.** Don't ship a template engine; let consumers adopt SK's if they want it. |
| **System prompt location** | Attribute on agent vs. message the graph author injects | **Attribute on agent** (we already have this via `IAiAgent.SystemPrompt`). Keep it. |
| **Guardrails attachment** | Three-layer middleware (MAF) vs. per-task callback (CrewAI) vs. output validator (Pydantic-AI) vs. plugin filters (SK) vs. none (LangGraph/AutoGen) | **Adopt MAF's three layers** as the mental model (Agent / Tool / Model). Implement via existing `IAgentFilter` + two new typed filter interfaces. |
| **HITL** | `interrupt()` (LangGraph), `RequestInfoExecutor` (MAF), `human_input=True` (CrewAI), `UserProxyAgent` (AutoGen) | **Interrupt-style** (closest to LangGraph). `IApprovalGate` on tool invocation, resume via session replay. |
| **Orchestration-as-code vs. config** | MAF/LangGraph (code), CrewAI (config), SK YAML declarative (both) | **Code-first now, declarative later.** Mirror the MAF Workflow surface shape; defer YAML. |

### 2.3 Where runtimes converge (cloud control plane)

Every runtime with a published control-plane shape converges on three primitives:

1. **Keyed, turn-based execution context** — `session` / `thread` / `run` / `workflowId` / `virtual-object-key` / `actor-id`. Single-writer-per-key, linearised messages, associated persistent state. **This is the Orleans virtual-actor primitive with a different name. We already have it.**
2. **Durable journal / event history** — Temporal event history, Restate journal, Inngest step memoisation, Dapr Workflows checkpointing, AgentCore microVM + separate Memory service. Every "agent runtime" is re-badged durable execution. **We don't have this yet; Orleans grain state is point-in-time, not a journal.**
3. **Tools as typed retriable units** — Activities / steps / `ctx.run` / Gateway MCP tools. Automatic retry boundary, JSON-schema'd input, side-effect tracking. **We have the schema + retry (Polly pipeline); we lack the journal-step identity.**

Universal lifecycle verbs: **Create · Invoke · Signal · Query · Cancel · Update · Deactivate/Evict**.

Minimal agent manifest intersection across all runtimes: `{ id, version, image-or-handler, protocol, tools, state-store, identity, autoscaling }`. Only AWS Bedrock AgentCore and OpenAI Assistants ship a *dedicated agent-level* control-plane object. Everyone else reuses workflow / actor / function / CRD primitives with agent conventions on top — which is the cheaper path for us too.

### 2.4 Interop protocol landscape (April 2026)

Clear winners for each interop axis:

- **Agent↔Tool → MCP** — `modelcontextprotocol/csharp-sdk` v1.2.0 on NuGet, stable. No credible competitor.
- **Agent↔Agent → A2A v1.0** — `a2aproject/a2a-dotnet` on NuGet (`A2A` + `A2A.AspNetCore`). LF-governed, IBM ACP officially merged in, Cisco/AGNTCY on TSC.
- **Client↔Agent** — no clear cross-vendor winner; A2A is the least-speculative bet, but most production agents expose a bespoke HTTP surface.

Flagged as dead or speculative: OpenAI Assistants API (sunset 2026-08-26), `agi-inc/agent-protocol` (no release since Apr 2024), IBM's standalone ACP (merged into A2A Sept 2025), AGNTCY's `acp-spec` (archived Apr 11, 2026), NLIP (ECMA-ratified Dec 2025 but zero visible production adoption).

**Design implication.** Two adapter packages — `Vais2.Agents.Protocols.Mcp` and `Vais2.Agents.Protocols.A2A` — give us the full interop surface. Nothing else is worth building to right now.

---

## 3. Harness pillar analysis (six pillars)

Each pillar gets the same structure: **current state · gap · proposed abstraction · deferred implementation**.

### 3.1 Execution loop

**Current state.** `StatefulAiAgent.AskAsync` runs a single turn: build request → Polly pipeline → filter chain → provider → persist turn → emit events → report usage. Streaming variant (`StreamAsync`) bypasses filters and resilience intentionally. No multi-step tool-call loop inside the agent — SK/MAF adapters own it. No step / turn / cost budget. No cancellation beyond `CancellationToken`. No checkpoint.

**Gap.**

- **Tool-call loop is opaque.** Consumers cannot intercept between tool-call and tool-result. Approval, logging, and journaling all have to hook inside the adapter — which means per-stack duplication.
- **No budget primitives.** Every framework surveyed has `MaxTurns` / `max_iterations` / `recursion_limit` / `UsageLimits`. We have none, relying on provider-side token limits only.
- **No interrupt / resume.** LangGraph `interrupt()` and MAF `RequestInfoExecutor` are the universal shape for HITL; we have no equivalent.
- **Streaming + filters mutually exclusive.** A deliberate choice in `v0.3.0-preview` but it blocks any filter that observes the full response (e.g., a guardrail that validates *after* streaming completes).

**Proposed abstractions (interface sketches, no impl).**

```csharp
// Step budget carried through a run; each loop increments counters and checks.
public sealed record RunBudget(
    int? MaxTurns = null,
    int? MaxToolCalls = null,
    int? MaxPromptTokens = null,
    int? MaxCompletionTokens = null,
    TimeSpan? MaxDuration = null);

// Interrupt / resume signal. Raised from a tool or guardrail; caller re-invokes
// the session with a ResumeInput once the external decision lands.
public sealed record AgentInterrupt(
    string InterruptId,
    string Reason,
    JsonElement Payload);

// Tool-call dispatch is lifted out of adapters into a pluggable step.
public interface IToolCallDispatcher
{
    Task<ToolCallOutcome> DispatchAsync(
        ToolCallRequest request,
        CancellationToken cancellationToken);
}

// Streaming filter — runs around streaming turns without buffering the full response.
// Resolves the "streaming bypasses filters" gap by offering hooks for start / delta / end.
public interface IStreamingAgentFilter
{
    ValueTask OnStreamStartAsync(CompletionRequest request, CancellationToken ct);
    ValueTask<CompletionUpdate> OnStreamDeltaAsync(CompletionUpdate update, CancellationToken ct);
    ValueTask OnStreamEndAsync(CompletionResponse final, CancellationToken ct);
}
```

**Deferred impl.** The tool-call dispatcher implementation, the interrupt serialisation / replay machinery, the MAF `Workflow`-equivalent graph executor — all post-abstraction.

---

### 3.2 Tools / skills

**Current state.** `ITool` is a tight contract: `Name`, `Description`, `JsonElement ParametersSchema`, `InvokeAsync`. `IToolRegistry` is the per-turn catalogue. SK and MAF each have a `ToolBinder` that translates `ITool` to their native shape.

**Gap.**

- **Schema authoring is raw.** Consumers write `JsonElement` by hand. Every other framework generates from type hints/attributes. This is the single biggest friction point in the current surface.
- **No MCP tool source.** MCP is now the de-facto agent↔tool standard. We have no way to plug in an MCP server as a tool catalogue.
- **No approval / HITL hook.** Tool invocation is all-or-nothing; there's no way to insert a "require human confirmation" gate on a specific tool.
- **No tool result typing.** `Task<string>` return; callers parse JSON ad-hoc.
- **No agent-as-tool helper.** Pydantic-AI / Mastra / MAF / CrewAI all document this; we'd need a wrapper today.

**Proposed abstractions.**

```csharp
// Typed tool factory — schema derived from T via STJ's JsonSchemaExporter (net9+)
// or a Pydantic-AI-style descriptor. Keeps ITool as the wire-level contract.
public static class Tool
{
    public static ITool FromFunc<TInput, TOutput>(
        string name,
        string description,
        Func<TInput, CancellationToken, Task<TOutput>> handler);
}

// Source of tools (catalogue-style). Allows live / remote discovery.
// IToolRegistry becomes a composition of these.
public interface IToolSource
{
    IAsyncEnumerable<ITool> DiscoverAsync(CancellationToken cancellationToken);
}

// Approval gate — consulted before InvokeAsync. Deny, allow, or defer (interrupt).
public interface IToolApprovalPolicy
{
    ValueTask<ToolApprovalDecision> EvaluateAsync(
        ITool tool,
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken);
}

public enum ToolApprovalDecision { Allow, Deny, RequireApproval }
```

**Deferred impl.**

- `Vais2.Agents.Protocols.Mcp` adapter — `McpToolSource(IMcpClient)` discovering server tools.
- `Vais2.Agents.Protocols.A2A.OutboundAgentAsTool` — remote A2A agent exposed as a local `ITool`.
- Default `StjSchemaTool.FromFunc<T>(...)` using `JsonSchemaExporter`.
- Policy implementations (always-allow, require-for-destructive, prompt-user, etc.).

---

### 3.3 Memory

**Current state.** `IAiAgent.History` is the only memory — an in-process `IReadOnlyList<ChatTurn>` owned by the agent. Orleans host persists it via grain state (point-in-time, whole-history). No long-term memory, no semantic recall, no working-memory concept.

**Gap.**

- **No session / thread primitive.** Every framework has one; we collapse "agent identity" and "conversation identity" into one grain key, which prevents one agent from running multiple concurrent sessions, branching a conversation, or replaying a specific session for checkpointing.
- **No short-vs-long-term split.** CrewAI (STM/LTM/Entity/Contextual) and Mastra (thread + resource + working memory) are the high-water marks. We don't need their full taxonomy, but we need the *split*.
- **No semantic recall.** The `IKnowledgeRetriever` interface exists but is scoped to external RAG, not to *agent-authored* memory.
- **No message-history reducer / summariser.** SK has `ChatHistoryReducer`; we have nothing.

**Proposed abstractions.**

```csharp
// Named, keyed conversation container. Replaces IAiAgent.History as the canonical
// short-term memory boundary. Maps 1:1 to Orleans grain state but is stack-neutral.
public interface IAgentSession
{
    string SessionId { get; }
    string AgentId { get; }
    IReadOnlyList<ChatTurn> History { get; }
    ValueTask AppendAsync(ChatTurn turn, CancellationToken ct);
    ValueTask<IReadOnlyList<ChatTurn>> SnapshotAsync(CancellationToken ct);
    ValueTask ResetAsync(CancellationToken ct);
}

// Pluggable long-term / working memory. Scopes carry MAF-style context.
public interface IMemoryStore
{
    ValueTask WriteAsync(MemoryScope scope, string key, MemoryItem item, CancellationToken ct);
    ValueTask<MemoryItem?> ReadAsync(MemoryScope scope, string key, CancellationToken ct);
    IAsyncEnumerable<MemoryItem> SearchAsync(MemoryScope scope, string query, int topK, CancellationToken ct);
}

public sealed record MemoryScope(
    string? SessionId = null,
    string? AgentId = null,
    string? TenantId = null,
    MemoryDurability Durability = MemoryDurability.LongTerm);

public enum MemoryDurability { ShortTerm, LongTerm, Working }

// History reducer — SK ChatHistoryReducer-alike. Lets consumers compress history
// without changing the session primitive.
public interface IHistoryReducer
{
    ValueTask<IReadOnlyList<ChatTurn>> ReduceAsync(
        IReadOnlyList<ChatTurn> history,
        int? targetTokens,
        CancellationToken cancellationToken);
}
```

**Deferred impl.**

- `OrleansAgentSession` — wraps existing `AiAgentGrain` state.
- `InMemoryAgentSession` for dev / tests.
- `RedisMemoryStore`, `PostgresMemoryStore`, `VectorMemoryStore` (on top of `VectorStoreKnowledgeRetriever`).
- `SummarisingHistoryReducer` (uses a completion provider to summarise old turns).

---

### 3.4 Context management

**Current state.** `IAgentContextAccessor` supplies ambient context (user, tenant, correlation id). `KnowledgeRetrievalFilter` is the only context-contributing filter — it prepends retrieved chunks to the system prompt.

**Gap.**

- **No MAF-style `IContextProvider` chain.** MAF's `AIContextProvider` hook contributes messages or middleware per turn; composable. Our filter model can do it but loses the semantic of "this is a context contribution, not a behaviour change."
- **No context window packing.** Nobody handles the case where history + retrieved context > model's context window.
- **No pinning / eviction.** Some turns (system prompt, recent tool outputs) should survive summarisation; we have no way to tag them.

**Proposed abstractions.**

```csharp
// Composable per-turn context contribution. Mirrors MAF's AIContextProvider.
// Each provider runs before the provider call; outputs merged into the request.
public interface IContextProvider
{
    ValueTask<ContextContribution> InvokeAsync(
        ContextInvocationContext context,
        CancellationToken cancellationToken);
}

public sealed record ContextContribution(
    string? SystemPromptAddendum = null,
    IReadOnlyList<ChatTurn>? InjectedHistory = null,
    IReadOnlyList<ITool>? AdditionalTools = null);

// Window packer — runs after all context providers, fits everything into the model's
// window by applying IHistoryReducer + dropping optional contributions by priority.
public interface IContextWindowPacker
{
    ValueTask<CompletionRequest> PackAsync(
        CompletionRequest candidate,
        int? modelContextWindow,
        CancellationToken cancellationToken);
}
```

**Deferred impl.** `KnowledgeRetrievalContextProvider` (migrate the existing filter to this shape); `TokenBudgetContextWindowPacker`; `PinnedMessageMarker` helpers.

---

### 3.5 Prompt construction

**Current state.** `IAiAgent.SystemPrompt` is a plain nullable string. `StatefulAgentOptions.SystemPrompt` is set at construction. No templating, no composition, no role/persona abstraction.

**Gap.**

- **No template engine abstraction.** SK has Handlebars/Liquid/SK templates; MAF has none; most others (Pydantic-AI, CrewAI, Mastra) use string interpolation. We don't need to build a template engine — but we need a plug point so consumers who *want* SK's can wire it in.
- **No multi-part composition.** Every framework with structured prompts (CrewAI role/goal/backstory, Pydantic-AI `@system_prompt` decorators, MAF multiple `ContextProvider`s) lets you compose the system prompt from parts. We force a single string.
- **No persona / identity slot.** For multi-agent scenarios where each participant has a distinct persona, we currently stuff it into the string.

**Proposed abstractions.**

```csharp
// Plug-point for template rendering. Default impl is String.Format-style;
// consumers can wire SK's PromptTemplateFactory here.
public interface IPromptTemplate
{
    ValueTask<string> RenderAsync(
        string template,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken);
}

// Assembles a system prompt from multiple contributors, in a defined priority order.
// Mirrors MAF's ContextProvider-stacking but scoped to the system-prompt slot.
public interface ISystemPromptComposer
{
    ValueTask<string?> ComposeAsync(
        AgentContext context,
        CancellationToken cancellationToken);
}

public interface ISystemPromptContributor
{
    int Priority { get; }
    ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken ct);
}
```

**Deferred impl.** `FormatStringPromptTemplate` default. `AggregatingSystemPromptComposer` that walks DI-registered contributors by priority. SK bridge (`SkPromptTemplateAdapter`) in the SK package.

---

### 3.6 Guardrails

**Current state.** `IAgentFilter` is the only middleware point. It wraps the whole turn; there's no distinction between "before the model", "before the tool", "after the output". `KnowledgeRetrievalFilter` and `LangfuseEnrichmentFilter` are the shipped filter examples — neither is a guardrail per se.

**Gap.**

- **One-layer middleware is insufficient.** MAF's three-layer split (Agent / Function / Chat) is the cleanest abstraction the survey turned up: *Agent* guards the turn (rate limit, audit), *Function* guards tool calls (validation, sandboxing), *Chat* guards the model interaction (prompt-injection detection, token counting).
- **No approval / denial result.** Filters today can throw or mutate the request; they can't return "denied with user-facing reason" cleanly.
- **No policy composition.** Can't express "if a guardrail denies, retry with a fallback" or "if input-guardrail and output-guardrail both pass, skip the content filter."
- **No cost / budget enforcement.** `UsageRecord` is emitted *after* the turn; nothing checks budgets *before* the turn.

**Proposed abstractions** — three thin interfaces, all optional, all coexist with the existing `IAgentFilter`.

```csharp
// Before the provider is called. Can deny, mutate, or pass through.
public interface IInputGuardrail
{
    ValueTask<GuardrailOutcome> EvaluateAsync(
        CompletionRequest request,
        AgentContext context,
        CancellationToken cancellationToken);
}

// After the provider returns, before AppendAsync. Sees the full response.
public interface IOutputGuardrail
{
    ValueTask<GuardrailOutcome> EvaluateAsync(
        CompletionResponse response,
        AgentContext context,
        CancellationToken cancellationToken);
}

// Around each tool invocation. Sees arguments + result.
public interface IToolGuardrail
{
    ValueTask<GuardrailOutcome> BeforeInvokeAsync(
        ITool tool,
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken);

    ValueTask<GuardrailOutcome> AfterInvokeAsync(
        ITool tool,
        JsonElement arguments,
        string result,
        AgentContext context,
        CancellationToken cancellationToken);
}

public sealed record GuardrailOutcome(
    GuardrailDecision Decision,
    string? Reason = null,
    JsonElement? Replacement = null);

public enum GuardrailDecision { Pass, Deny, Replace, Interrupt }
```

**Deferred impl.** `BudgetEnforcingInputGuardrail` (consults `RunBudget`). `TokenCounterGuardrail` (emits cost before the turn). `ContentSafetyGuardrail` bindings (Azure AI Content Safety, Llama Guard, etc.) shipped as separate adapter packages. MAF-convergent naming: "guardrail" not "filter" for the three typed layers; `IAgentFilter` remains for generic cross-cutting middleware.

---

## 4. Orchestration — upgrading beyond Sequential + RoundRobin

**Current state.** Two orchestrators ship: `SequentialOrchestrator` (pipeline) and `RoundRobinOrchestrator` (group chat with termination predicate). `OrchestrationStep` and `AgentParticipant` are the data carriers.

**Gap (from §2.1 convergence).** Three universal primitives we don't have:

1. **Handoff.** Agent-A transfers the live conversation to Agent-B, preserving history. Modeled as a tool on the LLM in OpenAI Swarm / MAF `HandoffBuilder` / AutoGen `Swarm`. Not expressible in our current orchestrators.
2. **Graph / DAG.** Executors connected by typed edges with state passed between them. LangGraph, MAF Workflows, AutoGen GraphFlow. Our Sequential is a degenerate chain of this.
3. **Composable termination conditions.** AutoGen's `MaxMessageTermination & TextMentionTermination` with `&`/`|` combinators. We have a `TerminationPredicate` delegate — functional but not composable.

**Proposed abstractions.**

```csharp
// Promote the delegate to a composable interface; keep the delegate as an adapter.
public interface ITerminationCondition
{
    ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<OrchestrationStep> steps,
        CancellationToken cancellationToken);
}

// Explicit handoff emitted by an orchestrator or a participant.
public sealed record Handoff(
    string FromAgent,
    string ToAgent,
    string? HandoffMessage = null,
    IReadOnlyList<ChatTurn>? HistoryToCarry = null);

// Graph orchestrator — DAG of participant-or-function executors.
// First cut: sequential + conditional edges; cycles deferred until we understand
// the checkpoint story better.
public interface IAgentGraphExecutor : IAgentOrchestrator
{
    // Construction via IAgentGraphBuilder — fluent code-first config.
}
```

**Keep both current orchestrators.** They cover 80% of real-world multi-agent scenarios and their shapes aren't going to change. The graph orchestrator is additive.

**Deferred impl.** `HandoffOrchestrator`, `GraphOrchestrator`, `ChatHistoryHandoffPolicy` (what of the history to carry on a handoff), LLM-driven next-speaker selection (AutoGen `SelectorGroupChat` shape).

---

## 5. Cloud control plane — "Kubernetes for agents"

The survey delivers a sharp finding: **the only two projects with a dedicated agent-level control-plane object are AWS Bedrock AgentCore and OpenAI Assistants.** Everyone else reuses workflow / actor / function / CRD primitives with agent conventions layered on top.

**That's the cheaper path for us too.** We already have the substrate (Orleans virtual actors + grain state + streams). What we *don't* have is the declarative manifest and the lifecycle verb surface. Those are the new abstractions.

### 5.1 What we already have (do not rebuild)

| Control-plane concern | Mechanism we already ship |
|---|---|
| Keyed, turn-based execution context | Orleans grain identity (`IAiAgentGrain` + grain key) |
| Single-writer-per-key semantics | Orleans virtual actor guarantees |
| Placement / scheduling | Orleans placement directors |
| Elasticity | Orleans silo scale-out |
| Scale-to-idle | Orleans deactivation (grain idle timeout) |
| Per-agent state persistence | `AiAgentGrainState` + Redis/Postgres grain storage |
| Cross-silo event fan-out | `OrleansAgentEventBus` over Orleans streams (Redis-backed) |
| Observability | `AgenticDiagnostics` ActivitySource + `OpenTelemetryUsageSink` |

### 5.2 What we need to add (new abstractions)

```csharp
// Declarative spec. Maps to an IAgentRuntime registration and optional provisioning.
// Intersection of the fields every control-plane surveyed requires (§2.3).
public sealed record AgentManifest(
    string Id,
    string Version,
    string? Description,
    AgentHandlerRef Handler,          // code reference (class name) or image ref
    IReadOnlyList<ProtocolBinding> Protocols,
    IReadOnlyList<ToolRef> Tools,
    MemoryRef? Memory,
    IdentityRef? Identity,
    AutoscalingSpec? Autoscaling,
    IReadOnlyDictionary<string, string>? Labels);

public sealed record AgentHandlerRef(string TypeName, string? AssemblyName = null);
public sealed record ProtocolBinding(string Kind, string? Endpoint = null);  // "Http" | "A2A" | "Mcp" | "SignalR"
public sealed record ToolRef(string Name, string? Source = null);            // local | MCP server | A2A agent
public sealed record MemoryRef(string Provider, string? ConnectionName = null);
public sealed record IdentityRef(string? InboundAuth = null, string? OutboundCredentials = null);
public sealed record AutoscalingSpec(int MinReplicas = 0, int? MaxReplicas = null, string? Target = null);

// Lifecycle verbs — the universal set from §2.3.
public interface IAgentLifecycleManager
{
    ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken ct);
    ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken ct);
    ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken ct);
    ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken ct);
    ValueTask CancelAsync(AgentHandle handle, CancellationToken ct);
    ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken ct);
    ValueTask EvictAsync(AgentHandle handle, CancellationToken ct);
}

// Discovery — minimal registry surface.
public interface IAgentRegistry
{
    IAsyncEnumerable<AgentManifest> ListAsync(string? label = null, CancellationToken ct = default);
    ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default);
}

// Identity — the only concern Orleans does not already give us.
public interface IAgentIdentityProvider
{
    ValueTask<AgentPrincipal> AuthenticateInboundAsync(AgentInvocationRequest request, CancellationToken ct);
    ValueTask<OutboundCredential> AcquireOutboundAsync(string agentId, string credentialRef, CancellationToken ct);
}
```

### 5.3 Explicitly not building now

- No HTTP control-plane API (the `IAgentLifecycleManager` is the in-process contract; HTTP is an adapter later).
- No CRD (follows HTTP API).
- No manifest YAML dialect (the record is the canonical shape; YAML is just a deserialisation target later).
- No policy engine (ABAC / quotas / priority) — deferred.
- No multi-region / federation — deferred.

---

## 6. Interop and polyglot

Per §2.4, alignment is two adapter packages. These are design-partner-ready; polyglot interop (Python / TS / remote agents) falls out for free.

### 6.1 `Vais2.Agents.Protocols.Mcp` (new package)

- **Inbound**: expose a `Vais2.Agents` agent as an MCP server — other clients (Claude Desktop, Cursor, VS Code) can call it.
- **Outbound**: consume an MCP server as a tool source — `McpToolSource : IToolSource` wrapping `IMcpClient` from `modelcontextprotocol/csharp-sdk`.
- Depends on `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` (both GA on NuGet).

### 6.2 `Vais2.Agents.Protocols.A2A` (new package)

- **Inbound**: `ASP.NET Core` endpoint that serves a `.well-known/agent-card.json` + JSON-RPC message endpoints. The `TaskManager` / `ITaskStore` shape from the A2A .NET SDK maps 1:1 onto `IAgentLifecycleManager` above — an `OrleansTaskStore` backed by `AiAgentGrain` bridges the two.
- **Outbound**: `A2AClient` wrapped as a remote `ITool` or as an `AgentParticipant.Provider` — so any A2A agent (Python, Go, Java) plugs into `SequentialOrchestrator` / `RoundRobinOrchestrator` / the forthcoming graph orchestrator.
- Depends on `A2A` + `A2A.AspNetCore`.

### 6.3 Polyglot consequence

No VAIS2-specific polyglot wire protocol. Python/TS agents reach us via A2A; we reach them via A2A or MCP. The abstraction implication is that `IAgentLifecycleManager.InvokeAsync` and `IToolSource.DiscoverAsync` must be serialisation-shape-stable enough that their JSON maps cleanly to A2A message types and MCP tool descriptors.

That's a single constraint on the new records (`AgentInvocationRequest`, `AgentInvocationResult`, `ToolDescriptor`): **no non-serialisable members, no `Func<>` payload, no adapter-specific types.** Already how all our records are shaped; re-confirm per record.

### 6.4 Explicitly deferred

- NLIP, AGNTCY SLIM, Agent Protocol — none worth adapter investment in 2026.
- Python SDK — if we want to own the Python client experience, that's a separate, large workstream. For now, A2A is the SDK.
- WebAssembly / gVisor agent isolation — deferred until we have a durable-execution story (§7 below).

---

## 7. What durable execution means for the abstractions

**Observation from §2.3.** Temporal, Restate, Dapr Workflows, Inngest, AgentCore all converge on "record every side effect, replay or resume from last success." Our current abstractions are point-in-time: the `AiAgentGrainState` holds *current* history, not a journal of events.

**Implication for the review.** We don't need to *build* a durable-execution runtime now (huge scope). But the abstractions we introduce in §3-§5 must *allow for* plugging one in later. Concretely:

- `ITool.InvokeAsync` should be **idempotent where possible** — consumers implementing durable execution need to replay tool calls. Documented expectation, not a type change.
- `IToolCallDispatcher` (§3.1) is explicitly designed to be the journal-step boundary. A `JournaledToolCallDispatcher` can wrap any inner dispatcher and record every call.
- `AgentInterrupt` (§3.1) is the resume-primitive hook. The journal side will serialise + replay these.
- `IAgentSession` (§3.3) becomes the natural checkpoint unit: session state + journal = full resume context.
- `IAgentLifecycleManager.SignalAsync` (§5.2) is shaped after Temporal `Signal` / OpenAI `submit_tool_outputs` / Inngest event — the universal "resume a waiting run with data" verb.

**Explicitly deferred.** The journal storage, the replay harness, the checkpointer interface (`ICheckpointer.Save/Restore`). Those are big; this review flags them as the next major design surface after component impls start landing.

---

## 8. Gap-vs-change matrix

What happens to each existing abstraction when §3-§7 land. **Keep / Extend / Rename / Deprecate / Add.**

| Current type | Disposition | Rationale |
|---|---|---|
| `ICompletionProvider` | **Keep** | Stack-neutral, well-shaped. |
| `IStreamingCompletionProvider` | **Keep** | Same. |
| `IAiAgent` | **Extend** — add `Session` property; keep `History` as `Session.History` shim for one release | Session becomes the canonical conversation handle. |
| `IAgentRuntime` | **Extend** — `GetOrCreate(id, sessionId?)` overload | Multi-session per agent. |
| `StatefulAiAgent` | **Extend** — opts grow: `IMemoryStore`, `IContextProvider[]`, `IInputGuardrail[]`, `IOutputGuardrail[]`, `IToolGuardrail[]`, `RunBudget`, `IToolCallDispatcher`, `IPromptTemplate`, `ISystemPromptComposer`, `IHistoryReducer`, `IContextWindowPacker` | Every new pillar lands here as opt-in. Defaults preserve v0.3 behaviour. |
| `StatefulAgentOptions` | **Extend** | Same. |
| `IAgentContextAccessor` / `AgentContext` | **Keep** | Still the right ambient-context primitive. |
| `IAgentEventBus` / `AgentEvent` family | **Extend** — add `ToolCallStarted`, `ToolCallCompleted`, `HandoffRequested`, `InterruptRaised`, `GuardrailTriggered` | Closed hierarchy expands. Breaking for wire serialisation, not source. |
| `IAgentFilter` | **Keep** (but deprioritise in docs) | Generic cross-cutting; guardrails are the typed first choice now. |
| `IAgentOrchestrator` / `OrchestrationStep` / `AgentParticipant` | **Keep** | |
| `SequentialOrchestrator` / `RoundRobinOrchestrator` | **Keep** | |
| `TerminationPredicate` delegate | **Extend** — add `ITerminationCondition` interface; delegate stays as adapter | Composability. |
| `ITool` | **Keep** | Wire-level shape unchanged. |
| `IToolRegistry` | **Extend** — add `IToolSource` and let the registry aggregate sources | MCP + A2A tool discovery. |
| `IKnowledgeRetriever` / `KnowledgeChunk` | **Keep** | |
| `KnowledgeRetrievalFilter` | **Migrate** to `KnowledgeRetrievalContextProvider` (new shape from §3.4). Old type stays as `[Obsolete]` for one release. | Context-provider shape is the universal convergence. |
| `IUsageSink` / `UsageRecord` | **Keep** | |
| `ChatTurn` / `CompletionRequest` / `CompletionResponse` / `CompletionUpdate` | **Keep** | |
| `AgentChatRole` | **Extend** — add `Tool` member (for tool-result turns in history) | Every framework surveyed has this as a distinct role. |
| `IAgentGrain` | **Extend** — session-keyed invocations | Enabler for multi-session. |

**New types** (all new in `Vais2.Agents.Abstractions` unless noted):

| Type | Pillar | Notes |
|---|---|---|
| `IAgentSession` | Memory | Canonical conversation handle. |
| `IMemoryStore` / `MemoryScope` / `MemoryItem` / `MemoryDurability` | Memory | |
| `IHistoryReducer` | Memory | |
| `IContextProvider` / `ContextContribution` | Context | MAF-convergent. |
| `IContextWindowPacker` | Context | |
| `IPromptTemplate` | Prompt | Plug-point; default in Core. |
| `ISystemPromptComposer` / `ISystemPromptContributor` | Prompt | |
| `IInputGuardrail` / `IOutputGuardrail` / `IToolGuardrail` / `GuardrailOutcome` / `GuardrailDecision` | Guardrails | MAF three-layer. |
| `IToolCallDispatcher` / `ToolCallRequest` / `ToolCallOutcome` | Execution | |
| `IToolApprovalPolicy` / `ToolApprovalDecision` | Tools | |
| `IToolSource` | Tools | |
| `IStreamingAgentFilter` | Execution | Streaming hook. |
| `AgentInterrupt` / `ResumeInput` | Execution | HITL. |
| `RunBudget` | Execution | Universal convergence. |
| `ITerminationCondition` | Orchestration | |
| `Handoff` / `IHandoff` | Orchestration | |
| `IAgentGraphExecutor` / `IAgentGraphBuilder` | Orchestration | |
| `AgentManifest` + sub-records | Control plane | |
| `IAgentLifecycleManager` / `AgentHandle` / `AgentInvocationRequest` / `AgentInvocationResult` / `AgentSignal` / `AgentStatus` | Control plane | |
| `IAgentRegistry` | Control plane | |
| `IAgentIdentityProvider` / `AgentPrincipal` / `OutboundCredential` | Control plane | |

### Breaking-change accounting for `v0.4.0-preview`

- **Additive, binary-safe**: all 20 new types above.
- **Source-compatible extensions**: new members on `StatefulAgentOptions`, new members on `AgentChatRole` (enum; numeric additions go at the end), new overload on `IAgentRuntime.GetOrCreate`.
- **Source-breaking**: (a) `KnowledgeRetrievalFilter` `[Obsolete]` → remove in `v0.5`; (b) `IAiAgent.History` becomes a forwarder to `Session.History`, still the same type; (c) `IAgentEventBus.Subscribe` delegate must handle new event subtypes — consumers that pattern-match exhaustively need an update.

Everything else (90%+) is additive. No rename dominoes like the `ChatRole` → `AgentChatRole` one we did in 0.3.

---

## 9. Task list (foundation-only; no component impls)

Grouped by pillar + control plane + interop. Each task is **abstractions and their defaults in Core**. Implementations are called out as "deferred impl" tasks that do not block the 0.4 cut.

### 9.1 Session + memory pillar

- [ ] Introduce `IAgentSession` in Abstractions; default `InMemoryAgentSession` in Core.
- [ ] `IAiAgent` gains `Session` property; `History` becomes `Session.History` shim.
- [ ] `IMemoryStore` + `MemoryScope` + `MemoryItem` + `MemoryDurability` in Abstractions.
- [ ] `IHistoryReducer` in Abstractions; `NoopHistoryReducer` default in Core.
- [ ] `StatefulAgentOptions`: add `Session`, `MemoryStore`, `HistoryReducer`.
- [ ] Orleans hosting: `OrleansAgentSession` wrapping `AiAgentGrainState`; session id becomes part of grain identity scheme (design-only — impl deferred to milestone).
- [ ] *Deferred impl*: `RedisMemoryStore`, `PostgresMemoryStore`, `VectorMemoryStore` (each a separate package).

### 9.2 Context pillar

- [ ] `IContextProvider` + `ContextContribution` + `ContextInvocationContext` in Abstractions.
- [ ] `IContextWindowPacker` in Abstractions; `NoopContextWindowPacker` default in Core.
- [ ] `StatefulAgentOptions`: add `ContextProviders`, `ContextWindowPacker`.
- [ ] Migrate `KnowledgeRetrievalFilter` → `KnowledgeRetrievalContextProvider` (keep old shape `[Obsolete]` for 0.4).
- [ ] *Deferred impl*: `TokenBudgetContextWindowPacker` using `tiktoken`-equivalent tokenizer.

### 9.3 Prompt pillar

- [ ] `IPromptTemplate` in Abstractions; `FormatStringPromptTemplate` default in Core.
- [ ] `ISystemPromptComposer` + `ISystemPromptContributor` in Abstractions; `AggregatingSystemPromptComposer` default in Core.
- [ ] `StatefulAgentOptions`: add `PromptTemplate`, `SystemPromptComposer`.
- [ ] *Deferred impl*: `SkPromptTemplateAdapter` (bridges to SK's Handlebars/Liquid engine).

### 9.4 Guardrails pillar

- [ ] `IInputGuardrail` + `IOutputGuardrail` + `IToolGuardrail` + `GuardrailOutcome` + `GuardrailDecision` in Abstractions.
- [ ] `StatefulAgentOptions`: add `InputGuardrails`, `OutputGuardrails`, `ToolGuardrails`.
- [ ] `StatefulAiAgent`: wire guardrail pipeline inside the turn (before request, after response, around tool calls).
- [ ] Event bus: add `GuardrailTriggered` event to the closed hierarchy (requires `AgentEvent` surrogate regeneration in Orleans package).
- [ ] *Deferred impl*: `BudgetEnforcingInputGuardrail`, `ContentSafetyGuardrail` (Azure AI Content Safety binding), `JsonSchemaOutputGuardrail`.

### 9.5 Execution-loop pillar

- [ ] `RunBudget` record in Abstractions.
- [ ] `IToolCallDispatcher` + `ToolCallRequest` + `ToolCallOutcome` in Abstractions; `DefaultToolCallDispatcher` in Core.
- [ ] `IStreamingAgentFilter` in Abstractions; wire into `StatefulAiAgent.StreamAsync`.
- [ ] `AgentInterrupt` + `ResumeInput` records in Abstractions.
- [ ] `StatefulAgentOptions`: add `Budget`, `ToolCallDispatcher`, `StreamingFilters`.
- [ ] Event bus: add `ToolCallStarted`, `ToolCallCompleted`, `InterruptRaised` events.
- [ ] *Deferred impl*: `JournaledToolCallDispatcher` (the durable-execution anchor).

### 9.6 Tools pillar

- [ ] `IToolSource` in Abstractions.
- [ ] `IToolRegistry`: extend to aggregate sources (keep the direct-tool constructor).
- [ ] `IToolApprovalPolicy` + `ToolApprovalDecision` in Abstractions; `AllowAllToolApprovalPolicy` default in Core.
- [ ] Static helper `Tool.FromFunc<TInput, TOutput>(...)` in Core using `JsonSchemaExporter`.
- [ ] *Deferred impl*: MCP and A2A tool-source adapters (§9.9).

### 9.7 Orchestration extensions

- [ ] `ITerminationCondition` in Abstractions; adapter from `TerminationPredicate` delegate.
- [ ] `Handoff` record + `IHandoff` in Abstractions.
- [ ] `IAgentGraphExecutor` + `IAgentGraphBuilder` in Abstractions.
- [ ] Event bus: add `HandoffRequested` event.
- [ ] *Deferred impl*: `HandoffOrchestrator`, `GraphOrchestrator`, LLM-driven speaker selector.

### 9.8 Control plane

- [ ] `AgentManifest` + `AgentHandlerRef` + `ProtocolBinding` + `ToolRef` + `MemoryRef` + `IdentityRef` + `AutoscalingSpec` records in Abstractions.
- [ ] `IAgentLifecycleManager` + `AgentHandle` + `AgentInvocationRequest` + `AgentInvocationResult` + `AgentSignal` + `AgentStatus` in Abstractions.
- [ ] `IAgentRegistry` in Abstractions.
- [ ] `IAgentIdentityProvider` + `AgentPrincipal` + `OutboundCredential` in Abstractions.
- [ ] `Hosting.Orleans`: `OrleansAgentLifecycleManager` + `OrleansAgentRegistry` as the reference implementations (mapping manifest fields to grain placement + storage provider selection).
- [ ] *Deferred impl*: HTTP control-plane API, CRD, policy engine, multi-region federation.

### 9.9 Interop packages

- [x] `Vais2.Agents.Protocols.Mcp` — new package. `McpToolSource` (outbound) shipped against `ModelContextProtocol 0.1.0-preview.10` (local-mirror pin; 1.2+ not mirrored). Inbound `McpAgentServer` deferred — semantic shape unresolved (§9.9 pillar plan).
- [x] `Vais2.Agents.Protocols.A2A` — new package. `A2ARemoteAgentTool` (outbound tool-form) shipped against `A2A 0.3.1-preview`. Inbound `A2AAgentEndpoint` (needs `A2A.AspNetCore`) and `A2ARemoteAgentProvider` deferred to follow-up.
- [ ] Bridge: `OrleansTaskStore : ITaskStore` in `Protocols.A2A` — deferred with inbound endpoint; only useful once we host A2A server-side.

### 9.10 Polishing / consistency

- [x] `AgentChatRole.Tool` enum member for tool-result turns. Shipped in PR 9a.
- [x] `AgentEvent` closed hierarchy regenerated with 5 new subclasses (`ToolCallStarted`, `ToolCallCompleted`, `GuardrailTriggered`, `InterruptRaised`, `HandoffRequested`). Orleans surrogate + 5 per-subclass converters added across PRs 9c + 12 per the M3e-3b finding.
- [x] `PublicAPI.Unshipped.txt` entries shipped via one-shot freeze (`9c73a4b`): Abstractions 594 / Core 76 / Hosting.Orleans 76 / Persistence.VectorData 3 / Protocols.Mcp 6 / Protocols.A2A 11. `*REMOVED*` markers processed (4 stale ChatTurn/CompletionResponse entries).
- [x] Smoketest rewritten against the packaged 0.4.0-preview feed — exercises session/memory/context/prompt/guardrails/RunBudget/dispatcher/IToolSource/termination/handoff/AgentManifest/InMemoryAgentRegistry/MCP/A2A at runtime. 13 .nupkg + 13 .snupkg shipped to `artifacts/packages/`. Annotated tag `v0.4.0-preview` on OSS repo `main` — **not pushed**.

### 9.11 Out of scope (explicit non-goals of this review)

- Python / TS / Go SDK design.
- Manifest YAML serialisation format.
- Checkpoint / journal / replay implementation.
- Policy engine (ABAC, quotas, priorities).
- Multi-region / federation.
- WebAssembly or gVisor agent isolation.
- `net10.0` multi-target.

---

## 10. Recommended next step

**Two choices**, both reasonable — depends on appetite.

### Option A: land the foundation in a single `v0.4.0-preview` (recommended)

Sequence:

1. **Pillar-by-pillar PR series** — one PR per §9 sub-section (9.1 through 9.10). Each PR adds types + defaults + tests + `PublicAPI.Unshipped.txt` entries, and updates the smoketest.
2. **Bundled 0.4 cut** at the end. Breaking-change accounting is linear (§8) so the 0.4 notes are writable up front.
3. **Design-partner feedback** against 0.4 before committing to any component impl. The point of this whole review is to get abstractions right *before* we build guardrails or memory stores.

Estimated size: ~10-12 PRs, each in the size range of Phase B/C from the dependency upgrade. Roughly two weeks of focused work.

### Option B: pillar-by-pillar preview bumps (`v0.3.1`, `v0.3.2`, …)

Same pillar PR series, but cut a numbered preview after each. Gives design partners incremental exposure but fragments the breaking-change story and doubles the pack/test overhead.

Recommendation: **Option A.** The breaking-change budget is small and linear; a single notable 0.4 cut is cleaner.

---

## 11. Source material

- Inventory of current public surface: `oss/agentic/src/**/PublicAPI.Shipped.txt` + the §1 table above.
- Agent-framework survey: archived in parent conversation (SK, MAF, LangGraph, AutoGen, CrewAI, Pydantic-AI, Mastra).
- Agent-runtime survey: archived in parent conversation (Dapr Agents, AgentCore, Restate, Temporal, Inngest, Ray Serve, KubeAI, Dynamo/AIQ, Knative, OpenAI Assistants+Agents SDK).
- Interop-protocols survey: archived in parent conversation (MCP, A2A, IBM ACP, NLIP, AGNTCY, OpenAI, AI Engineer Foundation Agent Protocol).
- Dependency-upgrade companion review: [`actor-agents-oss-dependency-upgrade-review.md`](./actor-agents-oss-dependency-upgrade-review.md).
