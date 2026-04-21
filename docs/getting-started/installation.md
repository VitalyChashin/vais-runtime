# Installation

Vais.Agents ships as **25 packages** under the `Vais.Agents.*` prefix — 24 libraries plus the `Vais.Agents.Cli` dotnet tool. Install only what you use — every package is self-describing via its dependency graph.

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

## Install the CLI

`Vais.Agents.Cli` is a dotnet tool, not a library reference — install it globally so the `vais` command lands on PATH:

```bash
dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview
```

See [install the CLI](install-the-cli.md) for the full bootstrap walkthrough (config-file creation, context switching, first-call verification).

## Minimum install per use case

Pick the row that matches your scenario; install the listed packages.

| Scenario | Packages |
|---|---|
| **Single-process agent** (console app, a web handler, a job) | `Vais.Agents.Abstractions` + `Vais.Agents.Core` + one of `Vais.Agents.Ai.SemanticKernel` / `Vais.Agents.Ai.MicrosoftAgentFramework` |
| **In-memory dev runtime** — address agents by id, wire DI | add `Vais.Agents.Hosting.InMemory` |
| **Orleans virtual-actor host** — durable state across a cluster | add `Vais.Agents.Hosting.Orleans` |
| **Redis backend** — Orleans clustering + grain storage + streams | add `Vais.Agents.Persistence.Redis` |
| **Postgres backend** — Orleans clustering + grain storage (ADO.NET) | add `Vais.Agents.Persistence.Postgres` |
| **RAG** — vector-store-backed knowledge retriever | add `Vais.Agents.Persistence.VectorData` |
| **Observability (OTel)** — traces + metrics to any OTel collector | add `Vais.Agents.Observability.OpenTelemetry` |
| **Observability (Langfuse)** — enrich spans with `langfuse.*` tags | add `Vais.Agents.Observability.Langfuse` (often alongside OTel) |
| **MCP tool-source (outbound)** — pull tools from an MCP server into the registry | add `Vais.Agents.Protocols.Mcp` |
| **A2A remote-agent-as-tool (outbound)** — delegate subtasks to a peer agent | add `Vais.Agents.Protocols.A2A` |
| **Host agents as MCP tools (inbound, v0.7)** — stdio / streamable-HTTP server | add `Vais.Agents.Protocols.Mcp.Server` |
| **Host agents as A2A endpoints (inbound, v0.8)** — ASP.NET Core server with AgentCard auto-derivation | add `Vais.Agents.Protocols.A2A.Server` (+ `Hosting.Orleans` for durable `input-required` tasks) |
| **Declarative graph orchestration (v0.9)** — Pregel/BSP, checkpoint/resume | base + `Vais.Agents.Control.Manifests.Yaml` (+ `AddOrleansGraphCheckpointer` for durable HITL) |
| **Graph on MAF Workflows (v0.9)** — MAF-native executor | above + `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` |
| **HTTP control plane (v0.6 + v0.11 + v0.12)** — idempotent verbs + OpenAPI + SSE streaming | add `Vais.Agents.Control.Http.Server` |
| **HTTP control-plane client** — from another process | add `Vais.Agents.Control.Http.Client` |
| **Kubernetes operator (v0.13)** — `Agent` CRD + reconciler | add `Vais.Agents.Control.KubernetesOperator` + deploy `deploy/helm/vais-agents-operator/` |
| **OPA policy engine (v0.14)** — gate every verb via Rego | add `Vais.Agents.Control.Policy.Opa` + an OPA sidecar |
| **CLI (v0.15)** — `vais` command for CI + interactive ops | `dotnet tool install -g Vais.Agents.Cli` |

The `Abstractions` + `Control.Abstractions` packages are transitive dependencies of everything else; you don't need to reference them directly unless you're authoring your own adapter or custom control plane.

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
- `Microsoft.AspNetCore.OpenApi` 9.0.11 *(v0.11 OpenAPI emission)*
- `System.Net.ServerSentEvents` 10.0.2 *(v0.12 SSE client + server)*
- `KubeOps` 10.3.4 *(v0.13 operator)*
- `Spectre.Console.Cli` 0.55.0 *(v0.15 CLI)*

You can override any of these in your own app's `Directory.Packages.props` or csproj.

## Verify your install

Once referenced, a minimal `Program.cs` that exercises the public surface:

```csharp
using Vais.Agents;
using Vais.Agents.Core;

var agent = new StatefulAiAgent(new FakeProvider());  // supply your real provider
Console.WriteLine(agent.GetType().FullName);

// Minimal IStreamingCompletionProvider stub for compile-only verification.
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

Or, if you installed the CLI, verify without writing any code:

```bash
vais version
# 0.15.0-preview
```

## Next

- [Hello agent](hello-agent.md) — end-to-end walkthrough against a real model.
- [Install the CLI](install-the-cli.md) — `vais` bootstrap + first-run config.
- [Choosing a stack: SK vs MAF](choosing-a-stack.md) — which adapter for which case.
- [Architecture](../concepts/architecture.md) — how the 25 packages fit together.
