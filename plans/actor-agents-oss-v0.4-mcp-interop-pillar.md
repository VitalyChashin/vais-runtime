# v0.4.0-preview — Interop pillar PR A: MCP outbound (§9.9 of the architectural review)

Tactical plan for the first half of the ninth pillar. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.9. Created 2026-04-18. A2A adapter lands as a separate PR per the user's approved split.

---

## Scope

Ship `Vais2.Agents.Protocols.Mcp` — a new package adapting the `ModelContextProtocol` .NET SDK as an `IToolSource`, so agents can pull external MCP-server tools into their registry at startup via `AggregatingToolRegistry.BuildAsync`.

**Design decisions settled 2026-04-18**:

1. **Outbound only in this PR.** The review listed both `McpToolSource` (outbound — pull external tools) and `McpAgentServer` (inbound — expose agent as MCP server). Outbound is the immediately-valuable and unambiguous feature; inbound has unresolved semantic questions ("agent as MCP" maps poorly to MCP's tool/prompt/resource primitives — MCP isn't designed for "call this agent as a unit"). Inbound can land in a follow-up once the shape settles.
2. **SDK version: `ModelContextProtocol 0.1.0-preview.10`** (latest available in our local NuGet mirror at `E:/nugets`). The research spike from 2026-04-18 cited v1.2.0 on nuget.org, but our repo-local `NuGet.config` clears global sources (Syncfusion contamination fix from the dependency upgrade) and the 10.x MCP SDK isn't mirrored locally. Shipping against preview.10 is pragmatic — our own package is `0.4.0-preview` and consumers tracking stable MCP will bump the pin later. Documented as a deliberate tradeoff.
3. **`McpToolSource` wraps `IMcpClient`** at construction — the caller owns connection lifecycle (for stdio / streamable-HTTP transports, the caller builds the client + connects before wrapping). Keeps the adapter stateless and testable.
4. **Tool result serialization**: `CallToolResponse.Content` items are `Text`/`Image`/`Audio`/`Resource`-typed. For v0.4, concatenate all `Text` blocks with `\n` separators and ignore non-text blocks. Document the limitation — image / audio / resource blocks in tool results are deferred. Real consumer impact: tools that return mixed-modal responses lose the non-text part.
5. **`CallToolResponse.IsError = true`** is treated as tool failure — we surface it via a non-null `Error` on the dispatcher's `ToolCallOutcome`, so the loop feeds the error back to the model (matches our `DefaultToolCallDispatcher` convention).

---

## Delivery — single PR

**Packages**: new `Vais2.Agents.Protocols.Mcp`.

Tasks:

- [x] Added `ModelContextProtocol 0.1.0-preview.10` pin to `Directory.Packages.props`.
- [x] Created `src/Vais2.Agents.Protocols.Mcp/Vais2.Agents.Protocols.Mcp.csproj`, solution-added, built clean.
- [x] `McpToolSource : IToolSource` — wraps `IMcpClient`, `DiscoverAsync` uses `McpClientExtensions.EnumerateToolsAsync`.
- [x] Internal `McpBackedTool : ITool` — wraps one `McpClientTool`; `Name` / `Description` / `ParametersSchema` from `ProtocolTool`; `InvokeAsync` converts `JsonElement` → `IReadOnlyDictionary<string, object?>`, calls `CallToolAsync`, concatenates `text`-type content blocks.
- [x] `McpToolInvocationException` — thrown when `CallToolResponse.IsError == true`; `DefaultToolCallDispatcher` captures as `ToolCallOutcome.Error`.
- [x] `tests/Vais2.Agents.Protocols.Mcp.Tests/` project added.
- [x] 2 tests: `McpToolInvocationException` shape + `McpToolSource` null-client rejection.
- [x] `PublicAPI.Shipped.txt` + `Unshipped.txt` for the new package.
- [ ] **Deferred**: end-to-end integration tests of the discover/invoke flow. `IMcpClient` funnels through `IMcpEndpoint.SendRequestAsync(JsonRpcRequest)` — unit-testing the adapter requires a real MCP server or a full JSON-RPC-transport fake. Disproportionate for this PR; lands with the v0.4 smoketest's MCP segment.

Breaking-change ledger: None — new package.

---

## Progress log

- 2026-04-18 — plan created. Five design decisions settled (outbound only; preview.10 SDK; caller-owned client lifecycle; text-only content concatenation; IsError → ToolCallOutcome.Error).
- 2026-04-18 — PR complete on local working tree. New `Vais2.Agents.Protocols.Mcp` package with `McpToolSource`, `McpBackedTool` (internal), `McpToolInvocationException`. 2 unit tests shipped; end-to-end integration coverage deferred. 261/261 non-container green.
- 2026-04-19 — **follow-up: SDK bumped to stable `ModelContextProtocol.Core 1.2.0`** (commit `cf6c883`). The user's removal of Syncfusion from their global NuGet config was the prompt, but the actual blocker was the design-decision #2 pin — no real mirror constraint. Mechanical adapter rewrite against the reshaped SDK:
  - `IMcpClient` interface dropped → concrete `ModelContextProtocol.Client.McpClient` class. `McpToolSource` ctor surface changes (PublicAPI.Shipped swap, not additive).
  - `EnumerateToolsAsync` (IAsyncEnumerable over the wire) → `ListToolsAsync(RequestOptions?, CT)` returning `IList<McpClientTool>` eagerly (auto-paginated by the SDK). `DiscoverAsync` awaits once then yields.
  - `CallToolResponse` → `CallToolResult`; `Content` is `IList<ContentBlock>`. Text filter changes from `item.Type == "text"` to a `item is TextContentBlock tcb` pattern match.
  - `serializerOptions` + `progress` parameters on `CallToolAsync` bundled into a new `RequestOptions` bag. Centralised in `McpToolSource.BuildRequestOptions` (internal helper).
  - Switched package reference from the `ModelContextProtocol` metapackage to `ModelContextProtocol.Core` — MCP 1.2 split hosting/DI helpers into the outer package and we don't use them. Fewer transitive deps.
  - Design decision #2 in this plan is **superseded** by the bump; leave historical context above for the record.
