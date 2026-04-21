# From zero to graph in 20 minutes

This tutorial walks you through the full Phase 3 picture: start the runtime, deploy two agents, compose them into a graph, invoke it, and observe the node events. Estimated time: 20 minutes.

By the end you will have:

- A running `vais-agents-runtime` container (docker-compose, localhost mode).
- A `classifier` agent and a `responder` agent registered via YAML manifests.
- A `qa-pipeline` graph that runs `classifier → responder → end`.
- Invoked the graph and seen structured `node.started` / `node.completed` events.

## Prerequisites

- Docker Engine 24+ / Docker Desktop 4.30+.
- `vais` CLI installed. Check: `vais version`.
- OpenAI API key: `export OPENAI_API_KEY=sk-...`.

---

## Part 1 — Start the runtime (3 min)

```bash
cd oss/agentic

# Build the image (first time ~2 min; cached ~20s)
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .

# Start localhost mode
docker compose -f deploy/compose/docker-compose.base.localhost.yaml up -d

# Verify
curl -s http://localhost:8080/healthz  # → ok
```

Set up the CLI context:

```bash
vais config set-context tutorial --server http://localhost:8080
vais config use-context tutorial
```

---

## Part 2 — Deploy two agents (5 min)

### 2a. The classifier

Save as `classifier.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: classifier
  version: "1.0"
  description: Classifies user intent into one of three categories.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      You are an intent classifier. Given a user message, respond with exactly one word:
      FACTUAL, CREATIVE, or OPINION. Nothing else.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Apply it:

```bash
vais apply -f classifier.yaml
vais invoke classifier --text "What is the capital of France?"
# FACTUAL
```

### 2b. The responder

Save as `responder.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: responder
  version: "1.0"
  description: Generates the final answer for the user.
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      You are a helpful assistant. Answer the user's question in 2-3 sentences.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Apply it:

```bash
vais apply -f responder.yaml
vais invoke responder --text "What is the capital of France?"
# The capital of France is Paris...
```

Verify both are registered:

```bash
vais get
# NAME          VERSION   STATUS   AGE
# classifier    1.0       Active   …
# responder     1.0       Active   …
```

---

## Part 3 — Compose a graph (5 min)

Save as `qa-pipeline.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: qa-pipeline
  version: "1.0"
  description: Classify intent, then generate the final answer.
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref:
        id: classifier
        version: "1.0"
    - id: respond
      kind: Agent
      ref:
        id: responder
        version: "1.0"
    - id: end
      kind: End
  edges:
    - from: classify
      to: respond
    - from: respond
      to: end
```

Apply it:

```bash
vais apply -f qa-pipeline.yaml
vais get-graphs
# NAME          VERSION   STATUS   AGE
# qa-pipeline   1.0       Active   …
```

---

## Part 4 — Invoke and observe (5 min)

### Unary invoke

```bash
vais invoke-graph qa-pipeline \
  --initial-state '{"input": "What is the tallest mountain on Earth?"}' \
  --version 1.0
```

Expected output (plain text, last assistant turn):

```
Mount Everest is the tallest mountain on Earth at 8,849 metres (29,032 feet) above sea level...
```

### Streaming invoke with event log

```bash
vais invoke-graph qa-pipeline \
  --initial-state '{"input": "What is the tallest mountain on Earth?"}' \
  --stream
```

You will see a sequence of events:

```
graph.started   qa-pipeline
node.started    classify
node.completed  classify
edge.traversed  classify → respond
node.started    respond
node.completed  respond
edge.traversed  respond → end
graph.completed qa-pipeline
```

Each `node.completed` event carries the agent's response text in `payload.lastAssistantText`.

### Tail graph events without invoking

```bash
vais graph-logs qa-pipeline \
  --initial-state '{"input": "Recommend a sci-fi novel."}' \
  --only node.started,node.completed
```

---

## Part 5 — Update the graph (2 min)

Add state bindings so the classifier's output is visible as a separate state key:

```yaml
    - id: classify
      kind: Agent
      ref:
        id: classifier
        version: "1.0"
      stateBindings:
        input: [input]
        output: [classification]
```

Re-apply:

```bash
vais apply -f qa-pipeline.yaml
```

Invoke again and notice `state.updated` events now include a `classification` key alongside `lastAssistantText`.

---

## Clean up

```bash
vais delete-graph qa-pipeline
vais delete classifier
vais delete responder
docker compose -f deploy/compose/docker-compose.base.localhost.yaml down
```

---

## What you learned

| Concept | Where |
|---|---|
| Runtime localhost mode | `deploy/compose/docker-compose.base.localhost.yaml` |
| YAML agent manifest | `spec.model`, `spec.systemPrompt`, `spec.handler.typeName: declarative` |
| YAML graph manifest | `spec.nodes`, `spec.edges`, `spec.entry` |
| Graph management | `vais apply`, `vais get-graphs`, `vais delete-graph` |
| Graph invocation | `vais invoke-graph`, `--stream`, `vais graph-logs` |
| State bindings | `stateBindings.input`, `stateBindings.output` |

## Next steps

- [Graph orchestration concept](../concepts/graph-orchestration.md) — node kinds, edge predicates (conditional routing), effects.
- [Graph as a first-class deployable](../concepts/graph-as-deployable.md) — management surface, mixed-kind YAML files.
- [Cross-runtime graphs concept](../concepts/cross-runtime-graphs.md) — invoke agents on a different runtime from the same graph.
- [Deploy a graph to the runtime](../guides/deploy-a-graph-to-the-runtime.md) — full guide with all management operations.
- [Resumable graphs on Orleans](../guides/run-resumable-graphs-on-orleans.md) — durable checkpoints, human-in-the-loop interrupts.
