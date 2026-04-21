# Graph as a first-class deployable

Shipped in v0.19. An `AgentGraph` can be registered with, and managed by, the vais-agents runtime in exactly the same way as a single `Agent` — via the HTTP control plane, `vais apply`, or the Kubernetes operator.

Before v0.19, graphs existed only as in-process constructs: you compiled the graph definition into the host process and wired it up manually. v0.19 adds a *declarative graph manifest* and an end-to-end management path for it.

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
    - id: end
      kind: End
  edges:
    - from: start
      to: end
```

The `metadata` block carries identity (`id`, `version`) and optional `labels` / `annotations`. The `spec` block carries the graph topology. The same YAML is accepted by `vais apply`, the REST API (`POST /v1/graphs`), and the `AgentGraph` Kubernetes CRD (after stripping the vais-specific envelope and reformatting into K8s spec fields).

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

The v0.19 layer sits above the runtime: it gives you a stable address (`id + version`) for any graph, separates the manifest from the host process, and makes graphs compatible with GitOps workflows.

## See also

- [Graph orchestration concept](graph-orchestration.md) — node kinds, edge predicates, state bindings.
- [Resumable graphs on Orleans guide](../guides/run-resumable-graphs-on-orleans.md) — durable state, interrupt / resume.
- [Deploy a graph to the runtime](../guides/deploy-a-graph-to-the-runtime.md) — step-by-step walkthrough.
- [Kubernetes operator concept](kubernetes-operator.md) — `AgentGraph` CRD reference.
- [CLI subcommands reference](../reference/cli-subcommands.md) — full flag tables for `invoke-graph`, `graph-logs`, `get-graphs`, `delete-graph`.
