# Guide: compose an agent graph in YAML

Author a `kind: AgentGraph` manifest, register its constituent agents, load the YAML with `YamlAgentGraphManifestLoader`, and invoke the graph in-process with `InProcessGraphOrchestrator` — observing the full `AgentGraphEvent` stream.

Shipped in v0.9 as `Vais.Agents.Control.Manifests.Yaml` (YAML → JSON normaliser) + `Vais.Agents.Control.Manifests.Json` (envelope + validator) + `Vais.Agents.Core` (orchestrator). The YAML envelope shape parallels the v0.6 `kind: Agent` envelope — same `apiVersion` / `kind` / `metadata` / `spec` split, one loader that handles mixed streams.

## Packages

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Core" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Hosting.InMemory" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Control.Manifests.Yaml" Version="0.15.0-preview" />
```

## The three-node graph

We'll build a `support-triage` graph:

1. `classify` — an LLM-backed agent that reads `user_query` and writes `category` (`billing` | `technical`).
2. `handle-billing` or `handle-technical` — specialist agents selected by edge predicate on `category`.
3. `done` — an `End` node.

```yaml
# graphs/support-triage.yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: support-triage
  version: "1.0"
  description: Route a customer query to the right specialist agent.
spec:
  entry: classify
  state:
    schema:
      type: object
      properties:
        user_query: { type: string }
        category:   { type: string }
  nodes:
    - id: classify
      kind: Agent
      ref: { id: classifier-agent, version: "1.0" }
      stateBindings:
        input:  [user_query]
        output: [category]
    - id: handle-billing
      kind: Agent
      ref: { id: billing-agent, version: "1.0" }
    - id: handle-technical
      kind: Agent
      ref: { id: technical-agent, version: "1.0" }
    - id: done
      kind: End
  edges:
    - from: classify
      to: handle-billing
      when: { property: category, operator: Eq, value: billing }
    - from: classify
      to: handle-technical
      when: { property: category, operator: Eq, value: technical }
    - from: classify
      to: done
      when: always                                                # safety net
    - from: handle-billing
      to: done
    - from: handle-technical
      to: done
  maxSteps: 20
```

Notes:

- `metadata.id` + `metadata.version` are required. `description`, `labels`, `annotations` are optional.
- `spec.state.schema` is an optional JSON Schema — it's stored as-is on the manifest (`AgentGraphManifest.StateSchema`) and surfaced to authoring tools. The runtime does **not** validate state against it in v0.9; it's a contract document.
- `stateBindings.input` names the state keys passed to the agent as request metadata. `stateBindings.output` names the keys extracted from the agent's structured output back into state.
- Edges from the same source node are tried **in manifest order** — the `classify → done when: always` edge is the catch-all. Reorder at your peril.
- `maxSteps` caps the super-step count. If omitted and the graph has a cycle, validation will throw; cycles must declare an explicit `maxSteps`.

## Register the agents + host services

Graphs invoke agents via `IAgentLifecycleManager`, so every node's `ref.id` must resolve to a registered `AgentManifest`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;
using Vais.Agents.Hosting.InMemory;

var services = new ServiceCollection();
services.AddAgenticInMemoryHosting();

services.AddSingleton<IAgentRegistry>(sp =>
{
    var registry = new InMemoryAgentRegistry();
    void RegisterAgent(string id, string handlerType) => registry.Register(new AgentManifest(
        Id: id,
        Version: "1.0",
        Handler: new AgentHandlerRef(handlerType),
        Protocols: Array.Empty<ProtocolBinding>(),
        Tools: Array.Empty<ToolRef>()));

    RegisterAgent("classifier-agent", "MyApp.ClassifierAgent");
    RegisterAgent("billing-agent",    "MyApp.BillingAgent");
    RegisterAgent("technical-agent",  "MyApp.TechnicalAgent");
    return registry;
});

services.AddTransient<ClassifierAgent>();
services.AddTransient<BillingAgent>();
services.AddTransient<TechnicalAgent>();

var provider = services.BuildServiceProvider();
```

The classifier writes `category` as part of its structured output; `stateBindings.output: [category]` on the `classify` node lifts that key back into the shared bag.

## Load the YAML

```csharp
using Vais.Agents.Control.Manifests;

var loader = new YamlAgentGraphManifestLoader();
var graphs = await loader.LoadFromFileAsync("graphs/support-triage.yaml");
var manifest = graphs.Single();        // one AgentGraph per file in this example
```

Validation runs eagerly:

- Entry node must exist.
- Every edge `from` / `to` must point at a declared node.
- Every `Agent`-kind node needs a `ref`; every `Code`-kind node needs a `handlerRef`; `End` takes neither.
- Graphs with cycles **must** declare `maxSteps` explicitly.
- Unknown predicate operators fail parse.

On violation the loader throws `AgentManifestValidationException` with every error in `.Errors` — exposed so host tooling can surface the whole list, not just the first.

Mixed streams (any combination of `kind: Agent` + `kind: AgentGraph` documents separated by `---`) load in one call:

```csharp
var resources = await loader.LoadAllResourcesFromStringAsync(multiDocYaml);
foreach (var r in resources)
{
    switch (r)
    {
        case ManifestResource.AgentCase a:      Console.WriteLine($"Agent: {a.Manifest.Id}"); break;
        case ManifestResource.AgentGraphCase g: Console.WriteLine($"Graph: {g.Graph.Id}"); break;
    }
}
```

## Invoke the graph in-process

`IAgentGraph` is the bag-state specialisation (`TState` = `IDictionary<string, JsonElement>`) — the right choice for YAML-authored graphs where state shape lives in the manifest, not in a C# POCO:

```csharp
using Vais.Agents;
using Vais.Agents.Core;

var registry   = provider.GetRequiredService<IAgentRegistry>();
var lifecycle  = provider.GetRequiredService<IAgentLifecycleManager>();

IAgentGraph graph = new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
    manifest:    manifest,
    registry:    registry,
    lifecycle:   lifecycle,
    checkpointer: new InMemoryCheckpointer());

var initial = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
{
    ["user_query"] = JsonSerializer.SerializeToElement("Please refund my last invoice."),
};

var final = await graph.InvokeAsync(initial, AgentContext.Default);
Console.WriteLine($"Route: {final["category"].GetString()}");
```

## Watch the event stream

`StreamAsync` yields the full nine-subtype `AgentGraphEvent` hierarchy. A triage run produces this sequence for the billing path:

```csharp
await foreach (var evt in graph.StreamAsync(initial, AgentContext.Default))
{
    switch (evt)
    {
        case GraphStarted s:    Console.WriteLine($"[start]  run={s.RunId} entry={s.EntryNodeId}"); break;
        case NodeStarted s:     Console.WriteLine($"[node→]  {s.NodeId} (super-step {s.SuperStep})"); break;
        case NodeCompleted c:   Console.WriteLine($"[node✓]  {c.NodeId} in {c.Duration.TotalMilliseconds:0}ms"); break;
        case StateUpdated u:    Console.WriteLine($"[state]  {string.Join(", ", u.ChangedKeys)}"); break;
        case EdgeTraversed e:   Console.WriteLine($"[edge]   {e.From} → {e.To}"); break;
        case GraphInterrupted i:Console.WriteLine($"[halt]   node={i.NodeId} reason={i.Reason}"); break;
        case GraphCompleted c:  Console.WriteLine($"[done]   final={c.FinalNodeId} steps={c.SuperStep}"); break;
        case GraphFailed f:     Console.WriteLine($"[fail]   {f.ErrorType}: {f.ErrorMessage}"); break;
    }
}
```

Typical billing-path output:

```
[start]  run=c8d9f7a4… entry=classify
[node→]  classify (super-step 0)
[state]  category
[node✓]  classify in 412ms
[edge]   classify → handle-billing
[node→]  handle-billing (super-step 1)
[node✓]  handle-billing in 287ms
[edge]   handle-billing → done
[node→]  done (super-step 2)
[done]   final=done steps=2
```

`RunId` + `SuperStep` appear on every event — correlating live events against the checkpoint timeline is straightforward.

## Where to from here

- [Run resumable graphs on Orleans](run-resumable-graphs-on-orleans.md) — persist checkpoints across silo restart with `OrleansCheckpointer` + wire the `Interrupt` kind for durable HITL.
- [Graph predicate operators reference](../reference/graph-predicate-operators.md) — the full ten-operator vocabulary + `allOf` / `anyOf` / `not` combinators + the `HandlerRef` escape.
- [Graph orchestration concept](../concepts/graph-orchestration.md) — node + edge + predicate model, orchestrator trade-offs, choose-your-orchestrator decision table.
- [Events reference](../reference/events.md) — `AgentGraphEvent` closed hierarchy + SSE wire-event-name mapping.
- `samples/AgentGraphInProcess` + `samples/AgentGraphYamlLoader` — runnable walkthroughs (pending — see [samples plan](../../plans/actor-agents-oss-housekeeping-samples-plan.md)).
