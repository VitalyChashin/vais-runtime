# graph-yaml-authored

Compose a multi-agent graph using a YAML manifest and deploy it to the runtime with `vais apply`. No C# required.

**Concepts:** [graph orchestration](../../docs/concepts/graph-orchestration.md), [graph as a first-class deployable](../../docs/concepts/graph-as-deployable.md), [compose a graph in YAML](../../docs/guides/compose-an-agent-graph-yaml.md).
**Needs API key:** yes — `OPENAI_API_KEY` (both agents call OpenAI).
**Code:** 0 lines — YAML manifests only.

---

## What this shows

- A `kind: AgentGraph` manifest that wires two declarative agents into a linear pipeline.
- `spec.entry` — the first node to execute when the graph is invoked.
- `spec.nodes` — a mix of `kind: Agent` refs and a terminal `kind: End` node.
- `spec.edges` — directed connections between nodes.
- `stateBindings` — mapping between the shared graph state and agent input/output keys.
- Unary invoke (`vais invoke-graph`) and streaming invoke (`--stream`).

---

## Quickstart

### 1. Start the runtime

```bash
cd oss/agentic
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .
docker compose -f deploy/compose/docker-compose.base.localhost.yaml up -d
vais config set-context local --server http://localhost:8080
vais config use-context local
```

### 2. Deploy the agents

```bash
vais apply -f samples/graph-yaml-authored/classifier.yaml
vais apply -f samples/graph-yaml-authored/responder.yaml
vais get
# NAME          VERSION  STATUS  AGE
# classifier    1.0      Active  3s
# responder     1.0      Active  2s
```

### 3. Deploy the graph

```bash
vais apply -f samples/graph-yaml-authored/qa-pipeline.yaml
vais get-graphs
# NAME          VERSION  STATUS  AGE
# qa-pipeline   1.0      Active  1s
```

### 4. Invoke

```bash
vais invoke-graph qa-pipeline \
  --initial-state '{"input": "What is the tallest mountain on Earth?"}' \
  --version 1.0
```

Expected output:

```
Mount Everest is the tallest mountain on Earth at 8,849 metres (29,032 feet)...
```

### 5. Streaming invoke

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

### 6. Clean up

```bash
vais delete-graph qa-pipeline
vais delete classifier
vais delete responder
docker compose -f deploy/compose/docker-compose.base.localhost.yaml down
```

---

## Manifests in this sample

### `classifier.yaml`

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: classifier
  version: "1.0"
  description: Classifies user intent.
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

### `responder.yaml`

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: responder
  version: "1.0"
  description: Generates the final answer.
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

### `qa-pipeline.yaml`

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
      stateBindings:
        input: [input]
        output: [classification]
    - id: respond
      kind: Agent
      ref:
        id: responder
        version: "1.0"
      stateBindings:
        input: [input]
    - id: end
      kind: End
  edges:
    - from: classify
      to: respond
    - from: respond
      to: end
```

---

## Environment variables

| Variable | Purpose |
|---|---|
| `OPENAI_API_KEY` | Required — forwarded to the OpenAI model provider |

---

## See also

- [docs/concepts/graph-orchestration.md](../../docs/concepts/graph-orchestration.md) — node kinds, edge predicates, effects
- [docs/concepts/graph-as-deployable.md](../../docs/concepts/graph-as-deployable.md) — management surface
- [docs/guides/compose-an-agent-graph-yaml.md](../../docs/guides/compose-an-agent-graph-yaml.md) — full YAML field reference
- [docs/agent-developer/compose-a-multi-agent-graph.md](../../docs/agent-developer/compose-a-multi-agent-graph.md) — end-to-end tutorial
- [samples/graph-cross-runtime](../graph-cross-runtime) — same pattern with a remote node ref
