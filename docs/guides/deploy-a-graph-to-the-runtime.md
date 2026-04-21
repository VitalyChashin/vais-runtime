# Deploy a graph to the runtime

This guide walks through the full lifecycle of an `AgentGraph` manifest: write → register → invoke → observe → evict. It uses the CLI (`vais`) and assumes a running vais-agents runtime reachable at `http://localhost:5000`.

## Prerequisites

- `vais` CLI installed and a context pointing at your runtime (`vais config set-context local --server http://localhost:5000`).
- At least one `Agent` registered that you want to wire into the graph.

If you don't have an agent yet, run `vais init my-agent | vais apply -f -` to scaffold and register a minimal one.

## Step 1 — Write the manifest

Save the following as `pipeline.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: my-pipeline
  version: "1.0"
  description: Two-node linear graph.
spec:
  entry: step-a
  nodes:
    - id: step-a
      kind: Agent
      ref:
        id: my-agent
        version: "1.0"
    - id: done
      kind: End
  edges:
    - from: step-a
      to: done
```

See [graph orchestration](../concepts/graph-orchestration.md) for the full node-kind catalogue (`Agent`, `End`, `Interrupt`, handler refs) and edge predicate syntax.

## Step 2 — Register the graph

```bash
vais apply -f pipeline.yaml
```

Expected output:

```
applied AgentGraph my-pipeline@1.0
```

On success the graph is registered in the runtime's `IAgentGraphRegistry` and the lifecycle manager tracks its state.

To verify:

```bash
vais get-graphs my-pipeline
```

## Step 3 — Invoke the graph

```bash
vais invoke-graph my-pipeline --initial-state '{"user_query": "hello"}'
```

Expected output:

```
run=run-abc123  complete=True
```

For streaming output, add `--stream`:

```bash
vais invoke-graph my-pipeline --initial-state '{"user_query":"hello"}' --stream
```

This opens an SSE stream and renders each `AgentGraphEvent` as it fires:

```
12:00:00.123  graph.started    run=run-abc123 graph=my-pipeline@1.0
12:00:00.140  node.started     run=run-abc123 node=step-a kind=Agent
12:00:01.420  node.completed   run=run-abc123 node=step-a 1282ms
12:00:01.421  edge.traversed   run=run-abc123 from=step-a to=done
12:00:01.422  graph.completed  run=run-abc123 via=done 1299ms
```

## Step 4 — Observe an existing run

Use `vais graph-logs` to replay events for any graph:

```bash
vais graph-logs my-pipeline
```

Filter by event kind with `--only`:

```bash
vais graph-logs my-pipeline --only "graph.started,graph.completed,graph.failed"
```

## Step 5 — Resume an interrupted run

If a graph node is of kind `Interrupt`, the run pauses and emits `graph.interrupted`. Resume it with:

```bash
vais invoke-graph my-pipeline \
  --run-id run-abc123 \
  --resume-from interrupt-id-xyz \
  --resume-payload '{"approved": true}'
```

Stream the resumed run:

```bash
vais invoke-graph my-pipeline \
  --run-id run-abc123 \
  --resume-from interrupt-id-xyz \
  --stream
```

## Step 6 — Update the manifest

Edit `pipeline.yaml`, bump the version, then re-apply:

```bash
vais apply -f pipeline.yaml
```

The runtime routes this to `PATCH /v1/graphs/my-pipeline` when the graph already exists (409 fallback).

## Step 7 — Evict the graph

```bash
vais delete-graph my-pipeline
```

Prompts for confirmation on a TTY. Pass `--force` for CI:

```bash
vais delete-graph my-pipeline --force
```

## Deploying to Kubernetes

If you're running the vais-agents Kubernetes operator, use the `AgentGraph` CRD instead:

```yaml
apiVersion: vais.io/v1alpha1
kind: AgentGraph
metadata:
  name: my-pipeline
  namespace: default
spec:
  graphId: my-pipeline
  version: "1.0"
  entry: step-a
  nodes:
    - id: step-a
      kind: Agent
      ref:
        id: my-agent
        version: "1.0"
    - id: done
      kind: End
  edges:
    - from: step-a
      to: done
```

```bash
kubectl apply -f pipeline-crd.yaml
kubectl get agentgraphs
```

The operator reconciles the CR against the HTTP control plane using the same idempotency and spec-hash diff logic as the `Agent` CRD. See [Kubernetes operator](../concepts/kubernetes-operator.md) for full CRD spec.

## See also

- [Graph as a first-class deployable](../concepts/graph-as-deployable.md) — design overview + full management surface table.
- [Graph orchestration concept](../concepts/graph-orchestration.md) — node kinds, edge predicates, state bindings.
- [Resumable graphs on Orleans](run-resumable-graphs-on-orleans.md) — durable state + interrupt/resume protocol.
- [CLI subcommands reference](../reference/cli-subcommands.md) — full flag tables for `invoke-graph`, `graph-logs`, `get-graphs`, `delete-graph`.
