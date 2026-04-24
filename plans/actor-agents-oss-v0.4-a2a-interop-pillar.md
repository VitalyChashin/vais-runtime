# v0.4.0-preview — Interop pillar PR B: A2A outbound (§9.9 of the architectural review)

Tactical plan for the second half of the ninth pillar. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.9 and the [MCP adapter PR](./actor-agents-oss-v0.4-mcp-interop-pillar.md). Created 2026-04-18.

---

## Scope

Ship `Vais2.Agents.Protocols.A2A` — a new package adapting the `A2A` .NET SDK so a remote agent reachable over the A2A (Agent-to-Agent) protocol can be surfaced to a local agent as an `ITool`. Lets an agent delegate a subtask to a peer agent without a custom HTTP integration.

**Design decisions settled 2026-04-18**:

1. **Outbound only in this PR.** The review listed both `A2ARemoteAgentTool` (outbound — call a remote agent as a tool), `A2ARemoteAgentProvider` (outbound — use a remote agent as a completion provider), and `A2AAgentEndpoint` (inbound — expose our agent as an A2A server). Tool-form outbound is immediately valuable and unambiguous; the other two have unresolved semantic questions (provider-as-agent loses our event stream; server-host needs `A2A.AspNetCore` integration choices). Deferred to follow-up PRs.
2. **SDK version: `A2A 0.3.1-preview`** (only version in our local NuGet mirror at `E:/nugets`). Our repo-local `NuGet.config` clears global sources (Syncfusion contamination fix from the dependency upgrade). Shipping against preview is consistent with MCP's pin; our package is `0.4.0-preview` so downstream consumers pin together. Documented as a deliberate tradeoff.
3. **Static factory `A2ARemoteAgentTool.CreateAsync(Uri, HttpClient?, CT)`** does discovery: resolves the remote `AgentCard` via `A2ACardResolver`, then builds the `A2AClient`. The constructed tool exposes the remote agent's `Name` + `Description` (from the card) on its own `ITool.Name` / `Description`. This keeps the ctor synchronous and well-defined for dependency injection scenarios, while discovery stays at factory time.
4. **Hardcoded input schema**: `{"type":"object", "properties": {"message": {"type":"string"}}, "required": ["message"]}`. A2A's `AgentCard` describes skills but not a single "input schema" shape — a tool-call model just needs one string to send. If the caller wants structured sub-agent I/O, they can wire it manually. Documented.
5. **Tool-name normalisation**: `ITool.Name` has the constraint `[A-Za-z0-9_-]+`; `AgentCard.Name` is free-form. Sanitise by replacing every non-matching char with `_`, collapsing runs, and trimming. Throw if the result is empty. Document.
6. **Response extraction**: `A2AClient.SendMessageAsync` returns `A2AResponse` — polymorphic over `AgentMessage` and `AgentTask`. Extract text by:
   - `AgentMessage` → concatenate all `TextPart.Text` blocks in `Parts`.
   - `AgentTask` → concatenate all `TextPart.Text` blocks across all `Artifacts[*].Parts`.
   - Any other subclass or no text → throw `A2AAgentInvocationException`.
7. **Failure surface**: `A2AAgentInvocationException` (agent name + message) — thrown for empty/unsupported responses. SDK-level `A2AException` bubbles up; `DefaultToolCallDispatcher` already catches any exception as `ToolCallOutcome.Error`.

---

## Delivery — single PR

**Packages**: new `Vais2.Agents.Protocols.A2A`.

Tasks:

- [x] Added `A2A 0.3.1-preview` pin to `Directory.Packages.props`.
- [x] Created `src/Vais2.Agents.Protocols.A2A/Vais2.Agents.Protocols.A2A.csproj`, solution-added.
- [x] `A2ARemoteAgentTool : ITool` — wraps `IA2AClient` + `AgentCard`; `Name`/`Description` from card; fixed `{message:string}` schema; `InvokeAsync` sends `AgentMessage` + extracts text.
- [x] `A2ARemoteAgentTool.CreateAsync(Uri, HttpClient?, CT)` — static factory that runs `A2ACardResolver` + constructs the client.
- [x] `A2AAgentInvocationException` — thrown on empty/unsupported response shapes.
- [x] `tests/Vais2.Agents.Protocols.A2A.Tests/` project added.
- [x] Shape-level tests: exception carries name + message; `CreateAsync` rejects null `Uri`; ctor rejects nulls; tool-name sanitisation behaves.
- [x] `PublicAPI.Shipped.txt` + `Unshipped.txt` for the new package.
- [ ] **Deferred**: end-to-end integration tests against a live A2A server. Same rationale as MCP — `IA2AClient` dispatches JSON-RPC over HTTP; unit-testing it requires a stubbed HTTP fake with protocol-compliant JSON-RPC envelopes. Disproportionate for this PR; lands with the v0.4 smoketest's A2A segment.

Breaking-change ledger: None — new package.

---

## Progress log

- 2026-04-18 — plan created. Seven design decisions settled (outbound tool-form only; preview SDK; async factory for card discovery; hardcoded single-string input schema; tool-name sanitisation; response extraction via switch on `A2AResponse`; custom invocation exception).
- 2026-04-19 — **follow-up: SDK bumped to `A2A 1.0.0-preview2`** (nuget.org). Same framing correction as the MCP bump — the design-decision-#2 "local mirror only has 0.3.1-preview" claim was imprecise; the repo-local `NuGet.config` `<clear/>` + nuget.org whitelist never blocked 1.x. The bump is a substantially bigger rewrite than MCP's because A2A 1.0 reshaped the wire types (looks protobuf-generated now):
  - **`AgentMessage` → `Message`** (type rename; same property shape — `Role`, `Parts`, `MessageId`, `ContextId`, `TaskId`, `ReferenceTaskIds`, `Extensions`, `Metadata`).
  - **`MessageRole` → `Role`** enum. Values now `User` / `Agent` / `Unspecified`.
  - **`TextPart` / `DataPart` / `FilePart` hierarchy collapsed into a single `Part`** type with a `PartContentCase` discriminator (`Text` / `Data` / `Raw` / `Url` / `None`). Creation moves from `new TextPart { Text = t }` / `new DataPart { ... }` etc. to factory methods `Part.FromText(text)`, `Part.FromData(json)`, `Part.FromRaw(bytes, mediaType, filename)`, `Part.FromUrl(url, mediaType, filename)`. Pattern matching `part is TextPart tp` swaps to `part.ContentCase == PartContentCase.Text` + `part.Text`. `Message.Parts` is an `IList<Part>` (no initialiser-list support for the collection itself; `.Add(...)` after construction).
  - **`MessageSendParams` → `SendMessageRequest`** (now the universal per-method request object; every `IA2AClient` method takes its own `*Request` record).
  - **`A2AResponse` polymorphic base → `SendMessageResponse` discriminated union.** New `SendMessageResponseCase` enum (`Message` / `Task` / `None`). Switch pattern moves from `case AgentMessage m:` / `case AgentTask t:` over `A2AResponse` to `switch (response.PayloadCase)` with `response.Message` / `response.Task` getters.
  - **`IA2AClient` surface expanded 7 → 12 methods.** Renames: `SendMessageStreamingAsync` → `SendStreamingMessageAsync`; push-notification methods split into Create/Get/List/Delete. New methods: `ListTasksAsync`, `GetExtendedAgentCardAsync`. Every method takes a `*Request` record — even `GetTaskAsync(string)` and `CancelTaskAsync(TaskIdParams)` became `GetTaskRequest` / `CancelTaskRequest`. Our stub `StubA2AClient` now implements 12 methods.
  - **Streaming return type: `IAsyncEnumerable<SseItem<A2AEvent>>` → `IAsyncEnumerable<StreamResponse>`.** SSE parsing is now absorbed into the SDK; callers get a plain `StreamResponse` stream. Dropped `using System.Net.ServerSentEvents;` from the test stub.
  - PublicAPI `Shipped` ctor entry referencing `A2A.IA2AClient` stays unchanged — external type name survived, only the signatures behind it moved.
- 2026-04-19 — adapter + test rewrite complete on local working tree. `A2ARemoteAgentTool.cs` rewritten (~30-LOC delta in the core InvokeAsync / ExtractText paths), `StubA2AClient` in tests extended to the 12-method surface. 12/12 A2A tests still green; 273/273 non-container tests across the whole solution still green. Single A2A package repacked at `0.4.0-preview` into `artifacts/packages/`; smoketest re-ran cleanly. `v0.4.0-preview` tag not moved (same deferral as MCP — still points at API-freeze commit `9c73a4b`, pending decision on tag-move vs. `v0.4.1-preview`). Design decision #2 in this plan is **superseded** by the bump; historical context above kept for the record.
