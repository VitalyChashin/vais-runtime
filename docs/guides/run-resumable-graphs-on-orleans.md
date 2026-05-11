# Guide: run resumable graphs on Orleans

Wire `OrleansCheckpointer` against your existing silo, place an `Interrupt`-kind node in the middle of a graph, and resume execution after silo restart with the approval payload the human supplied hours — or days — later.

Shipped in v0.9 as `OrleansCheckpointer` in `Vais.Agents.Hosting.Orleans`. Same split shape as v0.8's `InMemoryTaskStore` / `OrleansTaskStore` pair — `InMemoryCheckpointer` ships in `Vais.Agents.Core` for dev/tests; the Orleans-grain implementation ships in the hosting package so the Orleans dependency stays out of Core.

## Prerequisites

Read [run on Orleans locally](run-on-orleans-locally.md) first — this guide assumes a silo (or client) is already running, with an `IGrainFactory` registered in DI.

## Packages

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Core" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Hosting.Orleans" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Control.Manifests.Yaml" Version="0.15.0-preview" />
```

## A graph with a human-approval interrupt

Same three-node triage we built in [compose-an-agent-graph-yaml](compose-an-agent-graph-yaml.md), but this time the billing path pauses for explicit manager approval before refunding:

```yaml
# graphs/approval-triage.yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: approval-triage
  version: "1.0"
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref: { id: classifier-agent, version: "1.0" }
      stateBindings:
        input:  [user_query]
        output: [category, amount]
    - id: wait-for-approval
      kind: Interrupt
      interruptReason: "Refund over $500 — needs manager sign-off."
    - id: issue-refund
      kind: Agent
      ref: { id: refund-agent, version: "1.0" }
    - id: done
      kind: End
  edges:
    - from: classify
      to: wait-for-approval
      when:
        allOf:
          - { property: category, operator: Eq, value: billing }
          - { property: amount,   operator: Gt, value: 500 }
    - from: classify
      to: issue-refund
      when: { property: category, operator: Eq, value: billing }
    - from: classify
      to: done
      when: always
    - from: wait-for-approval
      to: issue-refund
      when: { property: resume.payload.approved, operator: Eq, value: true }
    - from: wait-for-approval
      to: done
      when: always
    - from: issue-refund
      to: done
```

The `wait-for-approval` node pauses the graph; the orchestrator writes a `GraphCheckpoint` + emits `GraphInterrupted` + returns. The resume-path edge reads from the well-known `resume.payload.approved` dotted state path — that's where `IResumableAgentGraph<TState>.ResumeAsync(checkpoint, resumePayload, …)` lands the caller-supplied payload.

## Wire the Orleans checkpointer

`AddOrleansGraphCheckpointer` uses `TryAddSingleton<IGraphCheckpointer>`, so it must run **before** anything else registers a default. The orchestrator itself is constructed explicitly — pass the checkpointer resolved from DI:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Control.Manifests;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage("Default");   // swap for Redis/Postgres in production
});

builder.Services.AddAgenticInMemoryHosting();
builder.Services.AddOrleansGraphCheckpointer();   // must precede orchestrator wiring

// Register your classifier + refund agents here (see compose-an-agent-graph-yaml).

builder.Services.AddSingleton<AgentGraphManifest>(sp =>
{
    var loader = new YamlAgentGraphManifestLoader();
    return loader.LoadFromFileAsync("graphs/approval-triage.yaml").AsTask().GetAwaiter().GetResult().Single();
});

builder.Services.AddSingleton<InProcessGraphOrchestrator<IDictionary<string, JsonElement>>>(sp =>
    new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
        manifest:    sp.GetRequiredService<AgentGraphManifest>(),
        registry:    sp.GetRequiredService<IAgentRegistry>(),
        lifecycle:   sp.GetRequiredService<IAgentLifecycleManager>(),
        checkpointer: sp.GetRequiredService<IGraphCheckpointer>()));

var app = builder.Build();
```

Ordering note: `AddOrleansGraphCheckpointer()` is idempotent via `TryAddSingleton`; calling it after anything else that registers `IGraphCheckpointer` is a no-op. Register it first.

## Invoke the graph + capture the checkpoint

```csharp
app.MapPost("/triage", async (
    TriageRequest request,
    InProcessGraphOrchestrator<IDictionary<string, JsonElement>> graph,
    IGraphCheckpointer checkpointer) =>
{
    var initial = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        ["user_query"] = JsonSerializer.SerializeToElement(request.Query),
    };

    string? pendingRunId = null;
    string? pendingInterruptId = null;

    await foreach (var evt in graph.StreamAsync(initial, AgentContext.Default))
    {
        if (evt is GraphInterrupted interrupt)
        {
            pendingRunId = interrupt.RunId;
            pendingInterruptId = interrupt.InterruptId;
            break;
        }
        if (evt is GraphCompleted)
        {
            return Results.Ok(new { status = "completed" });
        }
    }

    return Results.Ok(new
    {
        status = "awaiting-approval",
        runId = pendingRunId,
        interruptId = pendingInterruptId,
    });
});
```

The caller stashes the `runId` somewhere humans can act on — a Slack notification, a dashboard row, an email to the on-call manager. The checkpoint lives in the `IGraphCheckpointGrain` keyed by that `runId`; grain persistence flows through the Redis / Postgres provider you configured in `UseOrleans`.

## Resume after silo restart

Days later, the approver clicks `Approve`. The resume endpoint:

```csharp
app.MapPost("/triage/{runId}/approve", async (
    string runId,
    ApprovalRequest approval,
    InProcessGraphOrchestrator<IDictionary<string, JsonElement>> graph,
    IGraphCheckpointer checkpointer,
    CancellationToken ct) =>
{
    var checkpoint = await checkpointer.LoadAsync(runId, ct);
    if (checkpoint is null)
    {
        return Results.NotFound(new { error = $"No checkpoint for run '{runId}' — may have expired or never existed." });
    }
    if (checkpoint.IsComplete)
    {
        return Results.Conflict(new { error = "Run already completed; nothing to resume." });
    }

    var resumePayload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        ["approved"]       = JsonSerializer.SerializeToElement(approval.Approved),
        ["approver_email"] = JsonSerializer.SerializeToElement(approval.ApproverEmail),
    };

    IResumableAgentGraph<IDictionary<string, JsonElement>> resumable = graph;
    var final = await resumable.ResumeAsync(
        checkpoint:    checkpoint,
        resumePayload: resumePayload,
        context:       AgentContext.Default,
        cancellationToken: ct);

    return Results.Ok(new
    {
        status = "resumed",
        refundIssued = final.ContainsKey("refund_id"),
    });
});
```

Two points of interest:

- **The interrupt node itself does not re-fire on resume.** The orchestrator walks the `wait-for-approval → …` outgoing edges against rehydrated-plus-merged state. `resume.payload.approved` is the first thing they probe.
- **Silo restart is transparent.** `OrleansCheckpointer.SaveAsync` commits to the grain's persistence store (which the silo flushes to disk on each save); on restart, `LoadAsync` finds the checkpoint exactly as written, including the super-step counter, the `NextNodeId`, and the full state bag.

## What the event stream looks like across the pause

First request (`POST /triage`):

```
[start]  run=c8d9f7a4… entry=classify
[node→]  classify (super-step 0)
[state]  category, amount
[node✓]  classify in 412ms
[edge]   classify → wait-for-approval
[node→]  wait-for-approval (super-step 1)
[halt]   node=wait-for-approval reason=Refund over $500 — needs manager sign-off.
```

Silo restart happens here. No events land; the process dies; the grain is still on disk.

Second request (`POST /triage/{runId}/approve`), possibly on a different silo instance:

```
[resumed] from=wait-for-approval interruptId=int-8c72…
[state]  resume.payload
[edge]   wait-for-approval → issue-refund
[node→]  issue-refund (super-step 2)
[state]  refund_id
[node✓]  issue-refund in 623ms
[edge]   issue-refund → done
[node→]  done (super-step 3)
[done]   final=done steps=3
```

Super-step counts are continuous across the pause — the checkpoint carries `SuperStep = 1` and `NextNodeId = "wait-for-approval"` (the node whose edges we're about to evaluate on resume).

## Retention + cleanup

Checkpoints live forever unless you delete them. Call `IGraphCheckpointer.DeleteAsync(runId)` from whichever path terminates the workflow — typically inside the `/approve` handler after `ResumeAsync` returns `GraphCompleted`, or from a batched janitor job that trims runs older than your SLA. Expired interrupts that never got approved don't self-clean; if that's a concern, walk your checkpoint keyspace (via a custom grain index) and delete on TTL.

## Testing the resume path

`InMemoryCheckpointer` in `Vais.Agents.Core` has the same contract — swap `AddOrleansGraphCheckpointer()` for `services.AddSingleton<IGraphCheckpointer>(new InMemoryCheckpointer())` in integration tests, then invoke → break → resume in-process without spinning up Orleans. The orchestrator behaviour is identical.

## MAF orchestrator durable resume (v0.36)

`MafGraphOrchestrator` (in `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`) now implements `IResumableAgentGraph<TState>` — identical `ResumeAsync` / `ResumeStreamAsync` contract to `InProcessGraphOrchestrator`.

**Build a resumable MAF graph:**

```csharp
var checkpointer = serviceProvider.GetRequiredService<IGraphCheckpointer>();

IResumableAgentGraph<MyState> graph = MafGraphBuilder.Build(
    manifest: graphManifest,
    agentRegistry: agentRegistry,
    startNodeId: "wait-for-approval",   // rebuild workflow from this interrupt node on resume
    checkpointer: checkpointer);
```

`startNodeId` is the interrupt node id. On resume, `MafGraphBuilder.Build` reconstructs the Microsoft Agent Framework workflow starting from that node so `InProcessExecution` delivers the resume payload there directly — no MAF `CheckpointManager` bridging required.

**Resume:**

```csharp
GraphResult<MyState> result = await graph.ResumeAsync(
    runId: existingRunId,
    interruptId: interruptId,
    resumePayload: approvalPayload,
    cancellationToken: ct);
```

The `ResumeAsync` signature and checkpoint validation logic are identical to `InProcessGraphOrchestrator`. Cross-host resume is supported: checkpoint on one silo with `InProcessGraphOrchestrator`, resume on a different silo (or a different host) with `MafGraphOrchestrator`, using the same `IGraphCheckpointer` backend (e.g. `OrleansCheckpointer`).

**Testing MAF resume:**

Swap the concrete graph implementation in tests without changing the resume logic:

```csharp
// In tests — InProcess (fast, no MAF dependency):
IResumableAgentGraph<MyState> graph = new InProcessGraphOrchestrator<MyState>(manifest, registry, checkpointer);

// In production — MAF:
IResumableAgentGraph<MyState> graph = MafGraphBuilder.Build(manifest, registry, startNodeId, checkpointer);
```

Both share the same `InMemoryCheckpointer` swap for unit tests as described in "Testing the resume path" above.

## See also

- [Compose an agent graph (YAML)](compose-an-agent-graph-yaml.md) — the baseline graph-in-YAML flow this guide builds on.
- [Run on Orleans locally](run-on-orleans-locally.md) — silo wiring + persistence-provider configuration prerequisites.
- [Graph orchestration concept](../concepts/graph-orchestration.md) — checkpoint + resume semantics, orchestrator decision table.
- [Graph predicate operators reference](../reference/graph-predicate-operators.md) — the `allOf` combinator + `Gt` operator used in the approval edge.
- [`samples/AgentGraphResumeOnOrleans`](../../samples/AgentGraphResumeOnOrleans) — runnable walkthrough.
