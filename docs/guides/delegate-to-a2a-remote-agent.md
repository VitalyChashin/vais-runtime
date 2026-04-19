# Guide: delegate to an A2A remote agent

Wrap a remote A2A agent as a local `ITool` so your agent can delegate subtasks to a peer. Useful for multi-agent deployments where specialist agents live behind separate A2A endpoints.

## Packages

```xml
<PackageReference Include="Vais.Agents.Protocols.A2A" Version="0.4.0-preview" />
<PackageReference Include="A2A" Version="1.0.0-preview2" />
```

The A2A SDK targets `net8.0` + `net10.0` only; we consume it under `net9.0` via forward-compat (standard BCL compat rule). Restore is clean.

## Create the wrapper

Static factory resolves the remote's `AgentCard` first — discovers its name, description, and capabilities — then builds the `A2AClient` and constructs the tool.

```csharp
using Vais.Agents.Protocols.A2A;

var remote = await A2ARemoteAgentTool.CreateAsync(
    agentUrl: new Uri("https://peer-agent.example.com/a2a"),
    httpClient: null,              // let the SDK own its HttpClient (or pass a shared one)
    cancellationToken: ct);
```

The resulting `A2ARemoteAgentTool`:

- **Name** — `AgentCard.Name` sanitised to match `[A-Za-z0-9_-]+` (non-matching chars → `_`, runs collapsed, trimmed).
- **Description** — `AgentCard.Description` verbatim.
- **Input schema** — fixed `{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}`. A2A doesn't describe a single per-agent input shape, so we normalise to "one string in".

`AgentName` property preserves the unsanitised card name for display.

## Register

```csharp
sealed class InMemoryRegistry(params ITool[] tools) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools { get; } = tools;
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions { ToolRegistry = new InMemoryRegistry(remote) });

var reply = await agent.AskAsync("Ask the weather-bot peer for Paris forecast.");
```

## What the invocation does per call

1. Extracts `arguments.message` (string) from the tool-call args.
2. Wraps in an A2A `Message` with `Role.User` + a `Part.FromText(message)` content.
3. Calls `_client.SendMessageAsync(new SendMessageRequest { Message = message }, ct)`.
4. Inspects `SendMessageResponse.PayloadCase`:
   - `Message` — concatenate all `TextContentBlock`s from the returned message's `Parts`.
   - `Task` — concatenate text blocks across every `AgentTask.Artifacts[].Parts`.
   - Anything else → throw `A2AAgentInvocationException`.
5. Returns the concatenated text as the tool result.

The agent's outer loop receives it as a normal `ToolCallOutcome.Result` and continues.

## Composing multiple peers

For multi-peer deployments, keep a lookup of `AgentCard → A2ARemoteAgentTool` and register them all:

```csharp
var peers = new Dictionary<string, Uri>
{
    ["weather-bot"] = new("https://weather.example.com/a2a"),
    ["calendar-bot"] = new("https://calendar.example.com/a2a"),
    ["email-bot"] = new("https://email.example.com/a2a"),
};

var remoteTools = new List<ITool>();
foreach (var (_, url) in peers)
    remoteTools.Add(await A2ARemoteAgentTool.CreateAsync(url));

var registry = new InMemoryRegistry(remoteTools.ToArray());
```

The model picks which peer to call by tool name; the agent's outer loop dispatches uniformly.

## v0.4 limitations

- **Outbound only.** No `A2AAgentEndpoint` to expose your agent as an A2A server. Needs `A2A.AspNetCore` integration + `ITaskStore` wiring — deferred to a follow-up.
- **No `A2ARemoteAgentProvider : ICompletionProvider`.** Using a remote A2A agent as the *completion stack* (instead of as a tool) loses our event stream + session semantics. Revisit post-inbound.
- **Fixed input schema.** `{message:string}` only. If you need structured sub-agent I/O, write a custom `ITool` wrapper that calls the A2A client directly with your own schema.
- **Streaming response not surfaced to the outer agent.** `A2AClient.SendStreamingMessageAsync` returns `IAsyncEnumerable<StreamResponse>`; the wrapper uses `SendMessageAsync` for a single-shot result. Streaming pass-through is a future add.
- **Text-only extraction.** Data / file / URL `Part.ContentCase`s in the response are ignored.

## Things that catch people

- **Name sanitisation can change.** `AgentCard.Name = "Weather Bot 🌤️"` sanitises to `Weather_Bot__`. If you rely on specific tool names in your prompts, reference `A2ARemoteAgentTool.Name` after construction rather than assuming it matches `AgentCard.Name`.
- **`SendStreamingMessageAsync` returns `IAsyncEnumerable<StreamResponse>`** in A2A 1.0 (SSE parsing absorbed into the SDK). Previous `IAsyncEnumerable<SseItem<A2AEvent>>` is gone. Matters if you implement `IA2AClient` for tests.
- **`IA2AClient` has 12 methods in A2A 1.0.** Test stubs implementing the interface need the full surface — `SendMessageAsync`, `SendStreamingMessageAsync`, `GetTaskAsync`, `ListTasksAsync`, `CancelTaskAsync`, `SubscribeToTaskAsync`, plus four push-notification methods, plus `GetExtendedAgentCardAsync`.

## See also

- [Interop concept](../concepts/interop.md)
- [Tools concept](../concepts/tools.md)
- [Expose MCP tools to an agent](expose-mcp-tools-to-an-agent.md) — sibling pattern for MCP.
- Sample: `samples/A2ARemoteAgentExample/` (per samples plan)
