# Ontology-interceptor substrate (SEP-1763)

The substrate is the **one engine** behind two cartridges:

- **North** — the design-tools MCP server at `/design-mcp` (`vais.list`, `vais.describe`, `vais.validate`, …) shapes its responses through substrate interceptors that read the resource-model ontology in `IOntologyCatalog`.
- **South** — every virtual MCP server with `McpServerManifest.OntologyRef` exposes its tools through substrate interceptors that read a deployment-supplied `IDomainOntologyCatalog`.

This page is the architectural reference. The how-tos for each cartridge live separately: see [attach-a-domain-ontology.md](../guides/attach-a-domain-ontology.md) for south, and the design-tools tutorials under `docs/agent-developer/` for north.

## What the substrate provides

A transport-agnostic interceptor abstraction in `Vais.Agents.Abstractions`:

| Type | Role |
|---|---|
| `OntologyInterceptor` (non-generic) | Metadata base — carries `InterceptorKind` and `InterceptorPhase` so the system can reason about an interceptor without knowing its transport. |
| `OntologyInterceptor<TContext, TOutcome>` | Typed pipeline base — concrete subclasses pick a transport-specific context derivative and outcome type. |
| `OntologyInterceptorChain.Compose` | Outer-to-inner chain primitive with short-circuit support. |
| `InterceptionContext` (+ `OntologyOperation { List, Call }`) | Transport-agnostic carrier; concrete contexts subclass with their typed payload. |
| `IOntologyBinding` | Cross-cutting seam — `OntologyVersion`, `ConceptNames`, `TryGetConcept`. Both `IOntologyCatalog` (north) and `IDomainOntologyCatalog` (south) satisfy it. |
| `IInterceptorTee` + `NullInterceptorTee` | Producer seam for observability — a future trajectory tee plugs in here without source changes to interceptors. |

The discriminators come from SEP-1763:

- **`InterceptorKind`** — `Validation` (inspects + short-circuits, no mutation), `Mutation` (rewrites request and/or response), `Observability` (records without altering; default).
- **`InterceptorPhase`** — `Request` (before `await next()`), `Response` (after), `Both` (default). Declarative metadata; the pipeline still runs the same lifecycle.

## Why "one engine, two cartridges"

Both north and south have the same shape: a typed interceptor chain over a bound ontology that filters / annotates / validates a stream of operations. They differ in the *content* of the ontology:

| | North | South |
|---|---|---|
| Bound ontology | Resource-model ontology over the manifest kinds (Agent, McpServer, …) | Deployment-supplied tool ontology over a virtual server's projection |
| Concept = | A manifest kind | A projected tool name |
| Cross-refs = | Field paths into the manifest spec | Field paths into the tool arguments |
| Tags = | Capability / risk markers per kind | Per-tool markers (e.g. `risk:Destructive`) |

Because both expose the same `IOntologyBinding` seam, an interceptor written against the seam is **transport-agnostic** — the same code can shape an MCP `tools/list` for the design server or a virtual server's tool surface.

## The compatibility seam: `ToolGatewayMiddleware`

`ToolGatewayMiddleware` (the existing south tool-dispatch base) was re-based onto `OntologyInterceptor` without any signature change. Every concrete middleware in `Vais.Agents.Gateways.*` compiles unchanged; the dispatcher loop in `DefaultToolCallDispatcher` is untouched. The base type now carries the SEP-1763 metadata so south interceptors participate in the same ontology-aware lookup as new substrate consumers.

This is the **P6 extension-over-replacement** invariant in practice — the substrate is the new base, the existing data plane is the adapter that keeps working.

## Where the pieces live

| Concern | Project | Notes |
|---|---|---|
| Substrate types + binding seam | `Vais.Agents.Abstractions` | No new package, no cycle — lowest tier reachable by both north and south. |
| North catalog (resource model) | `Vais.Agents.Control.Manifests.Json` | `IOntologyCatalog`, `OntologyCatalog` over the embedded base ontology + a deployment-local overlay. |
| North read-role interceptors | `Vais.Agents.Control.Mcp.Server` | `DesignToolsScopeFilterInterceptor` (list-role), `ManifestValidatorInterceptor` (validate-role). |
| South domain ontology | `Vais.Agents.Control.Manifests.Json` | Artifact + loader + registry + catalog + shaper + retrievers + call-time middleware. |

The south cartridge currently lives alongside the north ontology code for symmetry; if the south side grows further (a real production corpus, additional retrieval backends), it has room to move into a dedicated `Vais.Agents.Gateways.McpOntology` package without source-incompatible changes — the seam is in `Abstractions`.

## Adding a substrate consumer

Write an `OntologyInterceptor<TYourContext, TYourOutcome>`. Compose your chain with `OntologyInterceptorChain.Compose`. Read the bound `IOntologyBinding` from the context to make decisions. That's the entire contract.

The plan that introduced the substrate is `plans/ontology-substrate-south-impl-2026-05-25.md`. Plan D (trajectory tee + induced authoring recipes) plugs into the producer seam (`IInterceptorTee`) defined here without further substrate changes.
