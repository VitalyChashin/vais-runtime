# Compose a multi-agent graph

You'll deploy two agents, compose them into an `AgentGraph` manifest, invoke the graph end-to-end, and observe its node-level events. About 20 minutes. End state: a `qa-pipeline` graph that classifies user intent on the first node and answers on the second, with `vais invoke-graph --stream` emitting a structured `graph.started → node.started → node.completed → graph.completed` event stream.

## Why graphs?

A single agent is one model + one prompt + tools. Most real systems need more than one — a classifier in front of a responder, a planner before a researcher, a verifier after a generator. Graphs make composition declarative: nodes are agents, edges describe routing, state threads through. The same operator surface (`vais apply`, `vais get-graphs`, `vais invoke-graph --stream`) covers single agents and graphs uniformly.

## Prerequisites

- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it (`vais config use-context local`).
- `OPENAI_API_KEY` exported on the runtime side.

## Part 1 — Deploy two agents

### 1a. The classifier

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

```bash
vais apply -f classifier.yaml
vais invoke classifier --text "What is the capital of France?"
# FACTUAL
```

### 1b. The responder

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

## Part 2 — Compose the graph

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

```bash
vais apply -f qa-pipeline.yaml
vais get-graphs
# NAME          VERSION   STATUS   AGE
# qa-pipeline   1.0       Active   …
```

## Part 3 — Invoke and observe

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

You'll see the structured event sequence:

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

## Part 4 — Add state bindings

State bindings make a node's input and output explicit in the graph's state bag — useful when downstream nodes need to read upstream output by a clean key.

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

Invoke again — `state.updated` events now include a `classification` key alongside `lastAssistantText`.

## Clean up

```bash
vais delete-graph qa-pipeline
vais delete classifier
vais delete responder
```

## What you built

- Two agents and a graph, all stored declaratively in the runtime's registry.
- A unary invocation path (`vais invoke-graph`) and a streaming one (`--stream`).
- A live event stream that exposes graph and node lifecycle without reading container logs.
- A state-binding pattern that makes the wire format between nodes explicit.

## Next

- **[Deep agent development](../deep-development/index.md)** — author plugin code (C#, LangGraph, Go) for nodes that need more than declarative YAML.
- **[Extensions](../extensions/index.md)** — customize gateways and other extension seams.
- [Concepts → Graph orchestration](../concepts/graph-orchestration.md) — node kinds, edge predicates (conditional routing), effects.
- [Concepts → Graph as a deployable](../concepts/graph-as-deployable.md) — management surface, mixed-kind YAML files.
- [Run resumable graphs on Orleans](../guides/run-resumable-graphs-on-orleans.md) — durable checkpoints + HITL interrupts.
- [Cross-runtime graphs](../concepts/cross-runtime-graphs.md) — invoke agents on a different runtime from the same graph.
