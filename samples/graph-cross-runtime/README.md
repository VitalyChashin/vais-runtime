# graph-cross-runtime

Deploy a graph where one node executes on a remote runtime via `ref.runtimeUrl` (v0.20 cross-runtime refs). Runtime A orchestrates the graph; a node annotated with `runtimeUrl` is dispatched to Runtime B over HTTP.

**Concepts:** [cross-runtime graphs](../../docs/concepts/cross-runtime-graphs.md), [graph orchestration](../../docs/concepts/graph-orchestration.md).
**Needs API key:** yes — `OPENAI_API_KEY` on both runtimes.
**Code:** 0 lines — YAML manifests only.

---

## What this shows

- `ref.runtimeUrl` on a `kind: Agent` node — instructs the orchestrator to invoke that agent via `IAgentRemoteInvoker` (HTTP POST) instead of the local registry.
- Two independent runtime containers on different ports (8080 = runtime A, 8081 = runtime B).
- The `enricher` agent lives exclusively on Runtime B; the `summarizer` lives on Runtime A.
- The `enrich-then-summarize` graph is applied to Runtime A only; it transparently fans out one node.
- Observing `node.completed` events that include `payload.sourceRuntime` to distinguish local vs remote execution.

---

## Quickstart

### 1. Start two runtime containers

```bash
cd oss/agentic
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .

# Runtime A — port 8080 (orchestrator)
docker run -d --name runtime-a -p 8080:8080 \
  -e OPENAI_API_KEY="$OPENAI_API_KEY" \
  vais-agents-runtime:local

# Runtime B — port 8081 (remote enricher host)
docker run -d --name runtime-b -p 8081:8080 \
  -e OPENAI_API_KEY="$OPENAI_API_KEY" \
  vais-agents-runtime:local
```

Verify both are up:

```bash
curl http://localhost:8080/healthz && curl http://localhost:8081/healthz
# ok  ok
```

### 2. Configure CLI contexts

```bash
vais config set-context runtime-a --server http://localhost:8080
vais config set-context runtime-b --server http://localhost:8081
```

### 3. Deploy the enricher to Runtime B

```bash
vais config use-context runtime-b
vais apply -f samples/graph-cross-runtime/enricher.yaml
vais invoke enricher --text "Paris"
# Enriched: Paris is the capital of France…
```

### 4. Deploy the summarizer and the cross-runtime graph to Runtime A

```bash
vais config use-context runtime-a
vais apply -f samples/graph-cross-runtime/summarizer.yaml
vais apply -f samples/graph-cross-runtime/enrich-then-summarize.yaml
vais get-graphs
# NAME                    VERSION  STATUS  AGE
# enrich-then-summarize   1.0      Active  1s
```

### 5. Invoke the graph from Runtime A

```bash
vais invoke-graph enrich-then-summarize \
  --initial-state '{"input": "Paris"}' \
  --version 1.0
```

The graph:
1. Sends the `input` to the `enrich` node → Runtime A forwards the call to Runtime B (port 8081).
2. Runtime B runs the `enricher` agent and returns the enriched text.
3. Runtime A continues to the `summarize` node → local `summarizer` agent condenses the result.
4. Returns the final summary.

### 6. Streaming — observe cross-runtime events

```bash
vais invoke-graph enrich-then-summarize \
  --initial-state '{"input": "Tokyo"}' \
  --stream
```

```
graph.started   enrich-then-summarize
node.started    enrich   (runtime=http://localhost:8081)
node.completed  enrich   (runtime=http://localhost:8081)
edge.traversed  enrich → summarize
node.started    summarize
node.completed  summarize
edge.traversed  summarize → end
graph.completed enrich-then-summarize
```

### 7. Clean up

```bash
vais config use-context runtime-a
vais delete-graph enrich-then-summarize
vais delete summarizer
vais config use-context runtime-b
vais delete enricher
docker rm -f runtime-a runtime-b
```

---

## Manifests in this sample

### `enricher.yaml` — applied to Runtime B

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: enricher
  version: "1.0"
  description: Enriches a short entity name with contextual information.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      You are a knowledge enrichment assistant. Given an entity name (city, person,
      concept), respond with 2-3 sentences of factual context. Nothing else.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

### `summarizer.yaml` — applied to Runtime A

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: summarizer
  version: "1.0"
  description: Condenses enriched text to a single sentence.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      You are a summarization assistant. Condense the user's text to a single sentence.
      Return only the sentence, no preamble.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

### `enrich-then-summarize.yaml` — applied to Runtime A

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: enrich-then-summarize
  version: "1.0"
  description: Enrich an entity on a remote runtime, then summarize locally.
spec:
  entry: enrich
  nodes:
    - id: enrich
      kind: Agent
      ref:
        id: enricher
        version: "1.0"
        # runtimeUrl tells the orchestrator to forward this node to Runtime B.
        runtimeUrl: http://localhost:8081
      stateBindings:
        input: [input]
        output: [enriched]
    - id: summarize
      kind: Agent
      ref:
        id: summarizer
        version: "1.0"
        # No runtimeUrl — runs locally on Runtime A.
      stateBindings:
        input: [enriched]
    - id: end
      kind: End
  edges:
    - from: enrich
      to: summarize
    - from: summarize
      to: end
```

---

## Environment variables

| Variable | Required on | Purpose |
|---|---|---|
| `OPENAI_API_KEY` | Both runtimes | Forwarded to the OpenAI model provider |

---

## Security note

In this sample both runtimes are anonymous (no bearer token). For production, set `VAIS_REMOTE_BEARER_TOKEN` on the calling runtime; the orchestrator forwards it in the `Authorization` header when invoking remote nodes. See [cross-runtime graphs concept](../../docs/concepts/cross-runtime-graphs.md#security) for details.

---

## See also

- [docs/concepts/cross-runtime-graphs.md](../../docs/concepts/cross-runtime-graphs.md)
- [docs/guides/compose-a-graph-across-runtimes.md](../../docs/guides/compose-a-graph-across-runtimes.md)
- [samples/graph-yaml-authored](../graph-yaml-authored) — single-runtime version of this pattern
