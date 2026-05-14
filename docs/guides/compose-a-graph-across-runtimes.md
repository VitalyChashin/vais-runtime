# Compose a graph across runtimes

This guide shows how to run a graph on runtime A where one node invokes an agent deployed on runtime B. By the end you will have applied a cross-runtime `AgentGraph` manifest, invoked it, and observed the remote node completing.

## Prerequisites

- Two running vais-agents runtimes. We call them `runtime-a` (at `http://localhost:5000`) and `runtime-b` (at `http://localhost:5001`).
- `vais` CLI contexts configured for both:

  ```bash
  vais config set-context local-a --server http://localhost:5000
  vais config set-context local-b --server http://localhost:5001
  ```

- An agent (`enricher`) registered on runtime B. If you haven't got one yet:

  ```bash
  vais --context local-b apply -f enricher.yaml
  ```

  Where `enricher.yaml` is a minimal declarative agent:

  ```yaml
  apiVersion: vais.agents/v1
  kind: Agent
  metadata:
    id: enricher
    version: "1.0"
  spec:
    model:
      provider: openai
      id: gpt-4o-mini
    systemPrompt:
      inline: "Enrich the user's input with one concrete fact."
    handler:
      typeName: declarative
    protocols:
      - kind: Http
    tools: []
  ```

## Step 1 — Write the cross-runtime graph manifest

Save the following as `cross-pipeline.yaml` on your workstation:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: cross-pipeline
  version: "1.0"
  description: Classify locally, enrich on runtime B.
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref:
        id: classifier          # runs on runtime A (local — no runtimeUrl)
        version: "1.0"
    - id: enrich
      kind: Agent
      ref:
        id: enricher            # runs on runtime B
        version: "1.0"
        runtimeUrl: "http://localhost:5001"
    - id: end
      kind: End
  edges:
    - from: classify
      to: enrich
    - from: enrich
      to: end
maxSteps: 10
```

Key points:

- The `classify` node has no `runtimeUrl` — the orchestrator on runtime A resolves it locally.
- The `enrich` node has `runtimeUrl: "http://localhost:5001"` — the orchestrator calls runtime B's invoke endpoint at `/v1/agents/enricher/invoke`.
- `maxSteps: 10` is required because the graph has no pure-DAG guarantee (the validator requires `maxSteps` on any cycle; linear graphs like this one don't strictly need it but it is good practice).

## Step 2 — Register the graph on runtime A

```bash
vais --context local-a apply -f cross-pipeline.yaml
```

Expected output:

```
applied AgentGraph cross-pipeline@1.0
```

If runtime B is not yet reachable, the `apply` still succeeds — `runtimeUrl` is validated syntactically (absolute http/https), not by live-pinging the remote host.

## Step 3 — Register the classifier agent on runtime A

The `classify` node must resolve on runtime A. If you haven't registered it yet:

```bash
vais --context local-a apply -f classifier.yaml
```

Where `classifier.yaml` is any valid `kind: Agent` manifest with `id: classifier, version: "1.0"`.

Or register both the agent and the graph in one apply:

```bash
vais --context local-a apply -f classifier.yaml -f cross-pipeline.yaml
```

(`vais apply` accepts multiple `-f` flags.)

## Step 4 — Invoke the graph

```bash
vais --context local-a invoke-graph cross-pipeline \
  --initial-state '{"input": "What is the capital of France?"}' \
  --stream
```

You should see events like:

```
graph.started   cross-pipeline  run-abc123
node.started    classify        …
node.completed  classify        …
node.started    enrich          …         ← this call goes to runtime B
node.completed  enrich          …
edge.traversed  enrich → end
graph.completed cross-pipeline  run-abc123
```

The `node.started` / `node.completed` events for the `enrich` node are emitted by runtime A's orchestrator — the remote call is transparent to the event stream.

## Step 5 — Observe the remote call

To confirm the HTTP call actually reached runtime B, tail its logs:

```bash
vais --context local-b logs enricher
```

You should see a `turn.started` / `turn.completed` pair corresponding to the graph run.

## Failure scenarios

### Remote agent not registered (404)

If `enricher` is not registered on runtime B, the invoke returns `404 Not Found`. The orchestrator throws `RemoteAgentInvocationException` with `IsRetryable: false`. The run transitions to phase `Error`:

```
node.started    enrich
graph.failed    cross-pipeline  RemoteAgentInvocationException: Remote agent at 'http://localhost:5001' returned HTTP 404.
```

Register the agent on runtime B and re-invoke:

```bash
vais --context local-b apply -f enricher.yaml
vais --context local-a invoke-graph cross-pipeline --initial-state '{"input":"…"}'
```

### Runtime B temporarily unavailable (503)

The invoker retries 503 / 504 / 429 responses twice (3 total attempts, 500 ms / 1 000 ms back-off). If all retries are exhausted, the run fails with `IsRetryable: true` in the exception detail. Bring runtime B back up and re-invoke; there is no automatic resume for a failed run.

### No bearer token (anonymous remote call)

If the graph is invoked without an `Authorization` header (e.g. from a background job or a CLI call without `--token`), no `Authorization` header is forwarded to runtime B. Whether runtime B accepts the anonymous call depends on its OPA policy configuration. A closed-fail-mode OPA policy will reject it with `403 Forbidden`, which the invoker treats as non-retryable.

To forward a token explicitly:

```bash
vais --context local-a --token "$(cat my-token.txt)" \
  invoke-graph cross-pipeline --initial-state '{…}'
```

## Multi-runtime manifests with `vais apply`

You can apply agents and graphs targeting multiple runtimes from a single shell session:

```bash
vais --context local-b apply -f enricher.yaml
vais --context local-a apply -f classifier.yaml -f cross-pipeline.yaml
```

The `--context` flag (or `--server`) scopes each apply call to the desired runtime. Manifests do not embed the target runtime URL — only nodes that invoke other runtimes carry `runtimeUrl`. The graph itself is always registered on the runtime you `apply` it to.

## See also

- [Cross-runtime graphs concept](../concepts/cross-runtime-graphs.md) — `runtimeUrl` field, bearer forwarding, retry semantics, limitations.
- [Graph as a first-class deployable](../concepts/graph-as-deployable.md) — management surface, manifest format.
- [Deploy a graph to the runtime](deploy-a-graph-to-the-runtime.md) — basic (single-runtime) graph deployment.
- [CLI subcommands reference](../reference/cli-subcommands.md) — full flag tables for `invoke-graph`, `graph-logs`, `apply`.
