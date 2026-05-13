# Install + package picks (library mode)

You're embedding `Vais.Agents` primitives in your own .NET host instead of running the standalone runtime. Pick the packages that match your scenario; every package is self-describing via its dependency graph.

> Most users want **[Agent developer](../agent-developer/index.md)** instead. This page is for the library path.

## Prerequisites

- **.NET 9 SDK** (or newer).
- An LLM provider key for samples that talk to a live model (OpenAI is the default in `samples/HelloAgent/`).

## NuGet source (pre-release)

Before the first public push, packages live in a local feed under `artifacts/packages/` inside this repo. To consume them from another project, map `Vais.Agents.*` to the local feed via a `NuGet.config` next to your csproj:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="vais-local" value="/path/to/vais.agents/artifacts/packages" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="vais-local">
      <package pattern="Vais.Agents.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Once packages are on nuget.org, the mapping is unnecessary — `<PackageReference Include="Vais.Agents.Core" Version="..." />` is enough.

## Minimum install per use case

Pick the row that matches your scenario; install the listed packages.

| Scenario | Packages |
|---|---|
| **Single-process agent** (console app, web handler, job) | `Vais.Agents.Abstractions` + `Vais.Agents.Core` + one of `Vais.Agents.Ai.SemanticKernel` / `Vais.Agents.Ai.MicrosoftAgentFramework` |
| **In-memory dev host** — address agents by id, wire DI | add `Vais.Agents.Hosting.InMemory` |
| **Orleans virtual-actor host** — durable state across a cluster | add `Vais.Agents.Hosting.Orleans` |
| **Redis backend** — Orleans clustering + grain storage + streams | add `Vais.Agents.Persistence.Redis` |
| **Postgres backend** — Orleans clustering + grain storage | add `Vais.Agents.Persistence.Postgres` |
| **RAG** — vector-store-backed knowledge retriever | add `Vais.Agents.Persistence.VectorData` |
| **Observability (OTel)** — traces + metrics to any OTel collector | add `Vais.Agents.Observability.OpenTelemetry` |
| **Observability (Langfuse)** — enrich spans with `langfuse.*` tags | add `Vais.Agents.Observability.Langfuse` (often alongside OTel) |
| **MCP tool-source (outbound)** — pull tools from an MCP server into the registry | add `Vais.Agents.Protocols.Mcp` |
| **A2A remote-agent-as-tool (outbound)** — delegate subtasks to a peer agent | add `Vais.Agents.Protocols.A2A` |
| **Host agents as MCP tools (inbound)** — stdio / streamable-HTTP server | add `Vais.Agents.Protocols.Mcp.Server` |
| **Host agents as A2A endpoints (inbound)** — ASP.NET Core server with AgentCard auto-derivation | add `Vais.Agents.Protocols.A2A.Server` (+ `Hosting.Orleans` for durable `input-required` tasks) |
| **Declarative graph orchestration** — Pregel/BSP, checkpoint/resume | base + `Vais.Agents.Control.Manifests.Yaml` (+ `AddOrleansGraphCheckpointer` for durable HITL) |
| **Graph on MAF Workflows** — MAF-native executor | above + `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` |
| **HTTP control plane** — idempotent verbs + OpenAPI + SSE streaming | add `Vais.Agents.Control.Http.Server` |
| **HTTP control-plane client** — from another process | add `Vais.Agents.Control.Http.Client` |

The `Abstractions` + `Control.Abstractions` packages are transitive dependencies of everything else; you don't need to reference them directly unless you're authoring your own adapter or custom control plane.

If you want the operator surface (`vais` CLI, Kubernetes operator, OPA policy engine), see the **[DevOps section](../devops/index.md)** — those tools target the runtime, not embedded library usage.

## Dependency pins

The `Directory.Packages.props` in this repo pins:

- `Microsoft.SemanticKernel` 1.74
- `Microsoft.Agents.AI` (MAF) 1.1.0
- `Microsoft.Extensions.AI` 10.5.0
- `Microsoft.Extensions.VectorData.Abstractions` 10.1.0 *(held until SK.Connectors.InMemory catches up to 10.5)*
- `OpenAI` 2.10.0
- `Microsoft.Orleans.*` 10.1.0
- `ModelContextProtocol.Core` 1.2.0
- `A2A` 1.0.0-preview2 *(targets `net8.0` + `net10.0`; consumed under `net9.0` via forward-compat)*
- `OpenTelemetry` 1.15.2

You can override any of these in your own app's `Directory.Packages.props` or csproj.

## Verify your install

```csharp
using Vais.Agents;
using Vais.Agents.Core;

var agent = new StatefulAiAgent(new FakeProvider());  // supply your real provider
Console.WriteLine(agent.GetType().FullName);

sealed class FakeProvider : ICompletionProvider, IStreamingCompletionProvider {
    public string ProviderName => "fake";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest r, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse("ok"));
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest r,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
        yield return new CompletionUpdate("ok");
    }
}
```

If the file compiles, your install is healthy.

## Next

- **[Hello agent (library mode)](hello-agent.md)** — end-to-end walkthrough against a real model.
- **[Choose your stack — MAF or SK](choose-your-stack.md)** — which adapter for which case.
- [Concepts → Architecture](../concepts/architecture.md) — how the 32 packages fit together.
- [Reference → Packages](../reference/packages.md) — per-package install table.
