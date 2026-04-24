# A2AUrl on GraphAgentRef — Cross-Runtime A2A Protocol Support (v0.21)

## Context

v0.20 shipped `GraphAgentRef.RuntimeUrl` for cross-runtime graph refs via the Vais HTTP control plane. The deferred backlog calls for an `A2AUrl` field enabling graph nodes to invoke agents via the A2A (Agent-to-Agent) protocol. This allows graphs to orchestrate across A2A-compatible agents from different vendors/runtimes (LangGraph, MAF, etc.) without requiring them to run the Vais control plane.

The v0.20 spike (Q10) explicitly deferred this: "Adding a field that can't be wired to a structurally-equivalent invoker today is misleading. `A2AUrl` belongs in v0.21."

**Scope**: A2A URL as a graph node transport. A2A structured output (beyond text) is a follow-up.

## Design Summary

Add `A2AUrl` as a 4th positional parameter on `GraphAgentRef`. Introduce `IA2AGraphNodeInvoker` (parallel to `IAgentRemoteInvoker`) with an `A2AGraphNodeInvoker` implementation in the A2A protocols project. Both orchestrators (InProcess + MAF) gain a new `else if (A2AUrl)` branch. Manifest loaders parse and validate `a2aUrl` with mutual-exclusivity against `runtimeUrl`.

A2A is text-in/text-out; the orchestrator's existing JSON state projection handles structured output extraction when the response text is JSON.

## New Files

| File | Purpose |
|------|---------|
| `src/Vais.Agents.Abstractions/IA2AGraphNodeInvoker.cs` | Interface: `InvokeAsync(a2aUrl, message, bearerToken, ct)` → `string` |
| `src/Vais.Agents.Protocols.A2A/A2AGraphNodeInvoker.cs` | Implementation using A2AClient + A2ACardResolver |
| `src/Vais.Agents.Protocols.A2A/A2AGraphNodeInvokerServiceCollectionExtensions.cs` | DI extension `AddA2AGraphNodeInvoker()` |
| `tests/Vais.Agents.Core.Tests/InProcessGraphOrchestrator_A2ABranchTests.cs` | 8 orchestrator A2A-branch tests |
| `tests/Vais.Agents.Protocols.A2A.Tests/A2AGraphNodeInvokerTests.cs` | Invoker implementation tests with mock STS |

## Modified Files

| File | Change |
|------|--------|
| `src/Vais.Agents.Abstractions/AgentGraphManifest.cs` | Add `string? A2AUrl = null` to `GraphAgentRef` positional params |
| `src/Vais.Agents.Abstractions/PublicAPI.Shipped.txt` | Remove old 3-param constructor + Deconstruct from GraphAgentRef |
| `src/Vais.Agents.Abstractions/PublicAPI.Unshipped.txt` | Add 4-param constructor, 4-param Deconstruct, A2AUrl get/init, IA2AGraphNodeInvoker |
| `src/Vais.Agents.Control.Manifests.Json/JsonAgentGraphManifestLoader.cs` | Parse `a2aUrl` from JSON, validate URI, enforce mutual exclusivity with `runtimeUrl` |
| `src/Vais.Agents.Protocols.A2A/PublicAPI.Unshipped.txt` | Declare A2AGraphNodeInvoker + extension method |
| `src/Vais.Agents.Core/InProcessGraphOrchestrator.cs` | Add `IA2AGraphNodeInvoker?` field + ctor param, A2A branch in `ExecuteNodeAsync` |
| `src/Vais.Agents.Core/PublicAPI.Unshipped.txt` | New ctor overload entries |
| `src/Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework/GraphNodeExecutor.cs` | Same: field + ctor param + A2A branch |
| `src/Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework/MafGraphBuilder.cs` | Add `IA2AGraphNodeInvoker?` param, pass to executor |
| `src/Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework/PublicAPI.Shipped.txt` or `Unshipped.txt` | New entries |
| `src/Vais.Agents.Control.InProcess/AgentGraphLifecycleManager.cs` | Add `IA2AGraphNodeInvoker?` field + ctor param, pass to `BuildOrchestrator` |
| `src/Vais.Agents.Control.InProcess/PublicAPI.Unshipped.txt` | New ctor entries |
| `src/Vais.Agents.Runtime.Host/CompositionRoot.cs` | Register `AddA2AGraphNodeInvoker()`, pass to lifecycle manager |
| `tests/Vais.Agents.Core.Tests/InProcessGraphOrchestrator_RemoteBranchTests.cs` | No changes needed (existing tests unaffected) |

## Key Interface Designs

### `IA2AGraphNodeInvoker`

```csharp
// src/Vais.Agents.Abstractions/IA2AGraphNodeInvoker.cs
public interface IA2AGraphNodeInvoker
{
    ValueTask<string> InvokeAsync(
        string a2aUrl,
        string message,
        string? bearerToken,
        CancellationToken cancellationToken = default);
}
```

Returns `string` (not `AgentInvocationResult`) because A2A is text-in/text-out. The orchestrator wraps the response: `result = new AgentInvocationResult(responseText)`.

### `GraphAgentRef` Change

```csharp
// BEFORE
public sealed record GraphAgentRef(string Id, string? Version = null, string? RuntimeUrl = null);

// AFTER
public sealed record GraphAgentRef(string Id, string? Version = null, string? RuntimeUrl = null, string? A2AUrl = null);
```

4th positional param with default = binary compatible. No positional deconstruction of `GraphAgentRef` exists in source.

### Orchestrator Branching (InProcessGraphOrchestrator.ExecuteNodeAsync)

```csharp
if (node.Ref.RuntimeUrl is { } runtimeUrl)
{
    // v0.20 HTTP control plane path (unchanged)
}
else if (node.Ref.A2AUrl is { } a2aUrl)
{
    if (_a2aInvoker is null)
        throw new InvalidOperationException(
            $"Node '{node.Id}' has A2AUrl '{a2aUrl}' but no IA2AGraphNodeInvoker was supplied.");
    var responseText = await _a2aInvoker.InvokeAsync(a2aUrl, text, _bearerToken, cancellationToken);
    result = new AgentInvocationResult(responseText);
}
else
{
    // Local path (unchanged)
}
```

Same pattern in `GraphNodeExecutor` (MAF).

### `A2AGraphNodeInvoker` Implementation

Creates a fresh `A2AClient` per invocation (from `IHttpClientFactory`) to avoid stale bearer tokens on cached clients. Reuses the text-extraction logic from `A2ARemoteAgentTool.ExtractText`.

### Manifest Schema

```yaml
spec:
  nodes:
    - id: enrich
      kind: Agent
      ref:
        id: external-enricher
        a2aUrl: https://enricher.vendor-a.com   # mutually exclusive with runtimeUrl
```

Validation: `a2aUrl` must be absolute http/https URI. Both `runtimeUrl` and `a2aUrl` set → error.

### DI Registration

```csharp
// CompositionRoot.cs
services.AddA2AGraphNodeInvoker();  // TryAddSingleton<IA2AGraphNodeInvoker>
```

Passed to `AgentGraphLifecycleManager` constructor alongside `remoteInvoker`.

## Implementation Order

### Phase 1: Abstractions (additive, non-breaking)

1. Add `A2AUrl` to `GraphAgentRef` record
2. Create `IA2AGraphNodeInvoker` interface
3. Update `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` for Abstractions

### Phase 2: Manifest loader

4. Parse `a2aUrl` in `JsonAgentGraphManifestLoader.ParseNodes`
5. Validate URI + mutual exclusivity with `runtimeUrl`
6. Add loader tests

### Phase 3: A2A invoker implementation

7. Create `A2AGraphNodeInvoker` in `Vais.Agents.Protocols.A2A`
8. Create DI extension `AddA2AGraphNodeInvoker`
9. Update PublicAPI for A2A project
10. Add invoker tests

### Phase 4: Orchestrator integration

11. Update `InProcessGraphOrchestrator` — field + ctor + A2A branch
12. Update `GraphNodeExecutor` (MAF) — same
13. Update `MafGraphBuilder.Build` — pass `a2aInvoker`
14. Update `AgentGraphLifecycleManager` — field + ctor + `BuildOrchestrator`
15. Update `CompositionRoot` — register + wire
16. Update PublicAPI for Core, MAF, InProcess
17. Add `InProcessGraphOrchestrator_A2ABranchTests` (8 tests)

### Phase 5: Update deferred-backlog.md

18. Remove A2AUrl entry from deferred backlog

## Existing Code to Reuse

| Component | Location | Reuse |
|-----------|----------|-------|
| `A2ARemoteAgentTool.ExtractText` | `src/Vais.Agents.Protocols.A2A/A2ARemoteAgentTool.cs:135` | Same text-extraction logic for Message/Task response |
| `A2AClient` / `A2ACardResolver` | `A2A` NuGet package | HTTP client for A2A protocol |
| `StubRemoteInvoker` pattern | `tests/Vais.Agents.Core.Tests/InProcessGraphOrchestrator_RemoteBranchTests.cs` | Test pattern: stub `IA2AGraphNodeInvoker` |
| `IHttpClientFactory` pattern | `HttpAgentRemoteInvoker.cs` | Per-invocation client from factory for bearer-token isolation |
| JSON state projection | `InProcessGraphOrchestrator.cs:429-447` | Handles JSON extraction from `result.Text` — works for A2A text responses |

## Verification

1. `dotnet build` — all projects compile, 0 warnings
2. `dotnet test` — all new + existing tests pass
3. Manifest round-trip: YAML with `a2aUrl` → JSON loader → `GraphAgentRef.A2AUrl` populated
4. Mutual exclusivity: YAML with both `runtimeUrl` and `a2aUrl` → validation error
5. Orchestrator: node with `A2AUrl` → `IA2AGraphNodeInvoker` called, result in state
6. Orchestrator: node with neither → local resolution (unchanged)
