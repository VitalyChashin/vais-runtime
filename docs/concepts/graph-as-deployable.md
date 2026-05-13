# Graph as a first-class deployable

An `AgentGraph` can be registered with, and managed by, the runtime in exactly the same way as a single `Agent` — via the HTTP control plane, `vais apply`, or the Kubernetes operator. Graphs are first-class deployables: durable in the registry, invocable through the standard verbs, streamable over SSE. A *declarative graph manifest* drives the end-to-end management path; in-process construction (compile the graph in the host and wire it manually) remains supported for embedded scenarios.

## The `AgentGraph` manifest format

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: my-pipeline
  version: "1.0"
  description: Optional human-readable description.
spec:
  entry: start
  nodes:
    - id: start
      kind: Agent
      ref:
        id: classifier
        version: "1.0"
        # runtimeUrl: "https://other-runtime.svc"  # optional — see Cross-runtime graphs
    - id: end
      kind: End
  edges:
    - from: start
      to: end
```

The `metadata` block carries identity (`id`, `version`) and optional `labels` / `annotations`. The `spec` block carries the graph topology.

### `ref.runtimeUrl` (v0.20)

Each `kind: Agent` node's `ref` object accepts an optional `runtimeUrl` field — an absolute `http` or `https` URI pointing at a **different** vais-agents runtime. When set, the orchestrator routes that node's invocation to the remote runtime instead of resolving the agent locally. See [Cross-runtime graphs](cross-runtime-graphs.md) for the full contract, bearer-token forwarding, and retry semantics. The same YAML is accepted by `vais apply`, the REST API (`POST /v1/graphs`), and the `AgentGraph` Kubernetes CRD (after stripping the vais-specific envelope and reformatting into K8s spec fields).

## Management surface

| Operation | CLI | HTTP | K8s operator |
|---|---|---|---|
| Register / create | `vais apply` | `POST /v1/graphs` | `kubectl apply -f graph.yaml` |
| Inspect | `vais get-graphs [id]` | `GET /v1/graphs[/{id}]` | `kubectl get agentgraphs` |
| Update | `vais apply` | `PATCH /v1/graphs/{id}` | edit + `kubectl apply` |
| Evict | `vais delete-graph <id>` | `DELETE /v1/graphs/{id}` | `kubectl delete agentgraph <name>` |
| Invoke (sync) | `vais invoke-graph <id>` | `POST /v1/graphs/{id}/invoke` | — |
| Invoke (stream) | `vais invoke-graph <id> --stream` | `POST /v1/graphs/{id}/invoke/stream` | — |
| Resume (sync) | `vais invoke-graph <id> --resume-from <int-id>` | `POST /v1/graphs/{id}/runs/{runId}/resume` | — |
| Resume (stream) | `vais invoke-graph <id> --resume-from … --stream` | `POST /v1/graphs/{id}/runs/{runId}/resume/stream` | — |
| Stream events | `vais graph-logs <id>` | `POST /v1/graphs/{id}/invoke/stream` | — |

## Mixed-kind YAML files

`vais apply -f multi.yaml` accepts files that contain both `kind: Agent` and `kind: AgentGraph` documents separated by `---`. Each document is dispatched to the appropriate endpoint:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: classifier
  version: "1.0"
spec:
  handler:
    typeName: MyApp.ClassifierAgent
  protocols:
    - kind: Http
  tools: []
---
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: my-pipeline
  version: "1.0"
spec:
  entry: start
  nodes:
    - id: start
      kind: Agent
      ref:
        id: classifier
        version: "1.0"
    - id: end
      kind: End
  edges:
    - from: start
      to: end
```

## Relationship to prior graph features

| Feature | Pillar | What it added |
|---|---|---|
| Graph orchestration | v0.10 | `IAgentGraphRunner`, in-process graph execution, node kinds, edge predicates |
| Resumable graphs on Orleans | v0.12 | Orleans-backed durable state, interrupt / resume protocol |
| Kubernetes operator (agent) | v0.13 | `Agent` CRD, `AgentEntityController` |
| **Graph as deployable** | **v0.19** | **`AgentGraph` control-plane API, `AgentGraph` CRD, `vais graph-*` CLI commands** |
| **Cross-runtime graph refs** | **v0.20** | **`ref.runtimeUrl` field on `kind: Agent` nodes; `IAgentRemoteInvoker`; bearer forwarding** |

The v0.19 layer sits above the runtime: it gives you a stable address (`id + version`) for any graph, separates the manifest from the host process, and makes graphs compatible with GitOps workflows.

## See also

- [Graph orchestration concept](graph-orchestration.md) — node kinds, edge predicates, state bindings.
- [Cross-runtime graphs concept](cross-runtime-graphs.md) — `ref.runtimeUrl`, bearer forwarding, retry semantics.
- [Resumable graphs on Orleans guide](../guides/run-resumable-graphs-on-orleans.md) — durable state, interrupt / resume.
- [Deploy a graph to the runtime](../guides/deploy-a-graph-to-the-runtime.md) — step-by-step walkthrough.
- [Compose a graph across runtimes](../guides/compose-a-graph-across-runtimes.md) — cross-runtime deployment walkthrough.
- [Kubernetes operator concept](kubernetes-operator.md) — `AgentGraph` CRD reference.
- [CLI subcommands reference](../reference/cli-subcommands.md) — full flag tables for `invoke-graph`, `graph-logs`, `get-graphs`, `delete-graph`.
