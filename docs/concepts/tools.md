# Tools

Tools are the agent's side-effects. Each is an `ITool` — a named handler with a JSON-schema-described argument shape and a string result. Consumers register tools directly or surface them through an `IToolSource` for dynamic discovery.

## Core types

```csharp
namespace Vais.Agents;

public interface ITool
{
    string Name { get; }             // must match [A-Za-z0-9_-]+
    string Description { get; }      // shown to the model as the tool's purpose
    JsonElement ParametersSchema { get; }  // JSON Schema describing arguments
    Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }
    ITool? GetByName(string name);
}

public interface IToolSource
{
    IAsyncEnumerable<ITool> DiscoverAsync(CancellationToken cancellationToken = default);
}
```

## Wiring a static set

`IToolRegistry` is the runtime surface the agent sees. Consumers build it once, inject it via `StatefulAgentOptions.ToolRegistry`.

```csharp
sealed class InMemoryToolRegistry(params ITool[] tools) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools { get; } = tools;
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        ToolRegistry = new InMemoryToolRegistry(new RollDiceTool(), new LookupWeatherTool()),
    });
```

The agent advertises these tools to the model on every turn's `CompletionRequest.Tools`, and dispatches requested tool calls through `IToolCallDispatcher`.

## `Tool.FromFunc<TInput, TOutput>` — typed shortcut

Hand-writing `ITool` for every function is busywork. `Tool.FromFunc` generates the schema from `TInput` via `System.Text.Json.Schema.JsonSchemaExporter` and handles argument deserialisation + output serialisation.

```csharp
using Vais.Agents.Core;

var echo = Tool.FromFunc<EchoRequest, string>(
    name: "echo",
    description: "Echo the text back to the caller.",
    handler: (input, ct) => Task.FromResult(input.Text));

sealed record EchoRequest(string Text);
```

A no-arg variant exists for tools that take nothing:

```csharp
var now = Tool.FromFunc<string>(
    name: "current_time",
    description: "Return the current UTC time.",
    handler: ct => Task.FromResult(DateTimeOffset.UtcNow.ToString("o")));
```

Handler returns `null`? The tool emits an empty string. Handler returns a non-string `TOutput`? It's JSON-serialised. String `TOutput`? Passed through verbatim.

### Schema caveat

STJ's `JsonSchemaExporter` emits nullability-aware union types like `"type": ["string", "null"]`. Most chat APIs accept this, but OpenAI's strict mode doesn't — if you hit a "tool schema invalid" error, post-process the emitted schema at the adapter layer. That's a consumer concern; Core doesn't normalise dialects.

## `IToolSource` — dynamic discovery

Static registries don't cover every case: MCP servers, A2A agents, plugin catalogues — those discover tools at runtime. `IToolSource.DiscoverAsync` yields `ITool`s; `AggregatingToolRegistry.BuildAsync(staticTools, sources)` composes static tools + discovered ones into a single registry frozen at build time (so `IToolRegistry.Tools` stays sync-accessible).

```csharp
using Vais.Agents.Core;

IToolSource mcpSource = new McpToolSource(mcpClient);  // from Vais.Agents.Protocols.Mcp
IToolSource customSource = new MyCatalogSource(catalog);

var registry = await AggregatingToolRegistry.BuildAsync(
    staticTools: new[] { new RollDiceTool() },
    sources: new[] { mcpSource, customSource });

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { ToolRegistry = registry });
```

`BuildAsync` is a **factory**, not lazy-discovery-on-first-access. That keeps `IToolRegistry.Tools` honest (sync contract, no sync-over-async blocking). Re-build if your sources change.

## Dispatch

`DefaultToolCallDispatcher` is the built-in dispatcher. Each `DispatchAsync`:

1. Runs `IToolGuardrail`s in order. Deny → throw `AgentGuardrailDeniedException`. Interrupt → throw `AgentInterruptedException`.
2. Publishes `ToolCallStarted` event.
3. Resolves via `IToolRegistry.GetByName(name)`; unknown tool name → `ToolCallOutcome` with `Error = "unknown tool"`.
4. Calls `ITool.InvokeAsync(args, ct)`. Thrown exceptions are caught into `ToolCallOutcome.Error`.
5. Publishes `ToolCallCompleted` event with `Succeeded` + `Duration`.
6. Returns `ToolCallOutcome`.

Consumers can inject `StatefulAgentOptions.ToolCallDispatcher` to override — useful for bespoke retry / circuit-breaker logic around tool calls. Any custom dispatcher should emit the same event envelope for observability parity.

## Extension points

- **Custom `ITool`** — implement the four members. Name must match `[A-Za-z0-9_-]+`; adapters validate this on registration.
- **Custom `IToolSource`** — useful for discovery mechanisms we haven't built (plugin DLLs, HTTP catalogues).
- **Custom `IToolCallDispatcher`** — for retry, circuit-break, timeout-per-tool, or audit logging.
- **`IToolGuardrail`** — see [guardrails](guardrails.md); runs inside `DefaultToolCallDispatcher`.

## MCP + A2A adapters

Two protocol packages expose outside tools and agents:

- **`Vais.Agents.Protocols.Mcp.McpToolSource`** — wraps a connected `McpClient`; exposes the remote MCP server's tools as `ITool`s. Outbound only.
- **`Vais.Agents.Protocols.A2A.A2ARemoteAgentTool`** — wraps a remote A2A agent as a single `ITool`, so a local agent can delegate subtasks. Static `CreateAsync(Uri)` factory resolves the remote's `AgentCard`.

See [interop](interop.md) for details.

## Observability

- `ToolCallStarted(callId, toolName)` on each dispatch.
- `ToolCallCompleted(callId, toolName, succeeded, error?, duration)` after each dispatch.
- `GuardrailTriggered(layer: Tool, decision, reason?)` when a tool guardrail denies or interrupts.

## Limitations / known gaps

- **`IToolApprovalPolicy`** from the architectural review was skipped — overlaps with `IToolGuardrail` (both return Pass / Deny / Interrupt).
- **No streaming tool results.** `ITool.InvokeAsync` returns `Task<string>` once. A streaming result shape would need a separate interface.
- **Schema dialect normalisation is a consumer concern.** STJ emits nullable union types; strict adapter modes need post-processing.
- **`AggregatingToolRegistry.BuildAsync` snapshots at build time.** Sources that change mid-run require a rebuild + agent restart.

## See also

- [Architecture](architecture.md)
- [Execution loop](execution-loop.md) — where dispatch fits.
- [Guardrails](guardrails.md) — tool-layer semantics.
- [Interop](interop.md) — MCP + A2A adapters.
- [Wire a custom tool guide](../guides/wire-a-custom-tool.md)
