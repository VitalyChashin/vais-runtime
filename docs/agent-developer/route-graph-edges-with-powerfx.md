# Route graph edges with PowerFx

You'll declare a graph that loops on its own output until a quality bar is met, with the loop edge gated by an inline [PowerFx](https://learn.microsoft.com/en-us/power-platform/power-fx/overview) expression. End state: a `quality-loop` graph where a drafter writes an answer, a grader scores it, and a `=And(Local.quality < Local.qualityTarget, Local.retryCount < Local.maxRetries)` edge sends the graph back to the drafter — with retry budget and quality target supplied at invoke time, not baked into the manifest.

## Why this matters

The `PropertyMatcher` vocabulary (`Eq`, `Gt`, `Contains`, …) compares one state property against one literal value. Plenty of routing fits that shape, but some doesn't:

- Comparing **two state properties** against each other (`quality` against `qualityTarget`).
- Combining conditions that touch **different keys** with arithmetic.
- String predicates like `StartsWith(Local.intent, "BILLING_")` or `Len(Local.answer) > 200`.

For those, edges accept an inline PowerFx expression — a string starting with `=`, evaluated against `Local.<state-key>`. No `IGraphEdgePredicate` class, no rebuild. The runtime container wires the PowerFx evaluator automatically; nothing to install.

## Prerequisites

- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it (`vais config use-context local`).
- `OPENAI_API_KEY` exported on the runtime side.

## Step 1 — Declare the drafter and grader

Two single-purpose agents — one writes, one scores.

Save as `drafter.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: drafter
  version: "1.0"
  description: Drafts a short answer on a given topic.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      Write a short, factual paragraph (3-4 sentences) answering the user's topic.
      Respond with plain prose only — no preamble, no headings.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Save as `grader.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: grader
  version: "1.0"
  description: Scores answer quality 0..1 and returns a one-sentence critique.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
    responseFormat: json
  systemPrompt:
    inline: |
      You grade a draft answer. Reply with a JSON object only:
        {"quality": <number 0..1>, "feedback": "<one sentence critique>"}
      Be strict: 0.9+ only for crisp, specific, factual answers.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Apply both:

```bash
vais apply -f drafter.yaml
vais apply -f grader.yaml
```

`model.responseFormat: json` on the grader makes the structured output reliable: the graph orchestrator parses the reply as JSON and lifts the `quality` and `feedback` fields straight into graph state when the grade node binds them as output keys.

## Step 2 — Compose the loop

Save as `quality-loop.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: quality-loop
  version: "1.0"
  description: Draft, grade, loop until quality target is met or retry budget exhausted.
spec:
  entry: draft
  nodes:
    - id: draft
      kind: Agent
      ref:
        id: drafter
        version: "1.0"
      stateBindings:
        input: [topic, feedback]
        output: [answer]
    - id: grade
      kind: Agent
      ref:
        id: grader
        version: "1.0"
      stateBindings:
        input: [answer]
        output: [quality, feedback]
    - id: end
      kind: End
  edges:
    - from: draft
      to: grade
    - from: grade
      to: draft
      when: "=And(Local.quality < Local.qualityTarget, Local.retryCount < Local.maxRetries)"
      onTraverse:
        increment: { property: retryCount }
    - from: grade
      to: end
      when: always
  maxSteps: 20
```

```bash
vais apply -f quality-loop.yaml
```

**What's going on:**

- `when: "=And(Local.quality < Local.qualityTarget, Local.retryCount < Local.maxRetries)"` — the literal string starting with `=` is parsed as `GraphEdgePredicate.Expression`. `Local.<key>` exposes every state key in the bag; hyphens in key names are normalised to underscores (so `retry-count` would surface as `Local.retry_count`).
- `onTraverse: increment: { property: retryCount }` — bumps `retryCount` by 1 each time the loop edge is taken. The three built-in effects are `set`, `increment`, `append`.
- Edges from `grade` are tried **in manifest order** — the loop edge is checked first; the catch-all `when: always` to `end` is the safety net.
- `maxSteps: 20` is required because the graph contains a cycle. Validation rejects cyclic graphs that don't declare it.

PowerFx evaluation is **not** type-checked at apply time — the manifest loader accepts any `=...` string. A bad formula surfaces as `InvalidOperationException` the first time the edge is evaluated.

## Step 3 — Invoke with thresholds in state

Thresholds aren't hard-coded in the manifest. They're in the initial state, supplied per invocation:

```bash
vais invoke-graph quality-loop \
  --initial-state '{"topic": "the photoelectric effect", "qualityTarget": 0.85, "maxRetries": 2, "retryCount": 0}' \
  --stream
```

The graph will either complete on the first draft (when the grader is happy) or fire the loop edge once or twice until either quality clears the bar or the retry budget runs out. A trace where the first draft falls short and one retry rescues it:

```
▶ graph.started   HH:MM:SS.mmm  quality-loop v1.0  run=<runId>
▷ node.started    HH:MM:SS.mmm  step=0  draft (Agent)
~ state.updated   HH:MM:SS.mmm  step=0  [lastAssistantText, messages, answer]
→ edge.traversed  HH:MM:SS.mmm  step=0  draft → grade
◁ node.completed  HH:MM:SS.mmm  step=0  draft (Nms)
▷ node.started    HH:MM:SS.mmm  step=1  grade (Agent)
~ state.updated   HH:MM:SS.mmm  step=1  [lastAssistantText, messages, quality, feedback]
~ state.updated   HH:MM:SS.mmm  step=1  [retryCount]                  # ← increment effect fired
→ edge.traversed  HH:MM:SS.mmm  step=1  grade → draft                 # quality below target
◁ node.completed  HH:MM:SS.mmm  step=1  grade (Nms)
▷ node.started    HH:MM:SS.mmm  step=2  draft (Agent)                 # retryCount=1, feedback in input binding
~ state.updated   HH:MM:SS.mmm  step=2  [lastAssistantText, messages, answer]
→ edge.traversed  HH:MM:SS.mmm  step=2  draft → grade
◁ node.completed  HH:MM:SS.mmm  step=2  draft (Nms)
▷ node.started    HH:MM:SS.mmm  step=3  grade (Agent)
~ state.updated   HH:MM:SS.mmm  step=3  [lastAssistantText, messages, quality, feedback]
→ edge.traversed  HH:MM:SS.mmm  step=3  grade → end                   # quality cleared, OR retry budget hit
◁ node.completed  HH:MM:SS.mmm  step=3  grade (Nms)
▷ node.started    HH:MM:SS.mmm  step=4  end (End)
◁ node.completed  HH:MM:SS.mmm  step=4  end (Nms)
■ graph.completed HH:MM:SS.mmm  step=5  final=end (Nms)
```

Two `state.updated` events fire after the first `grade` step. The first carries the grader's JSON-decoded output (`quality`, `feedback`) lifted into state by the `output: [quality, feedback]` binding. The second carries the `retryCount` change emitted by the `increment` effect when the loop edge is taken — that's the visible signal that the side-effect ran. On the next `draft` super-step, `feedback` is in the drafter's input binding, so it sees the critique and can revise.

Run again with a tighter target to force the retry budget to exhaust:

```bash
vais invoke-graph quality-loop \
  --initial-state '{"topic": "the photoelectric effect", "qualityTarget": 0.99, "maxRetries": 1, "retryCount": 0}' \
  --stream
```

The loop runs until `retryCount` reaches `maxRetries`; the PowerFx guard then evaluates `false` and the catch-all edge sends the run to `end` with the best draft so far.

## Step 4 — When to reach for PowerFx vs. `PropertyMatcher`

Default to `PropertyMatcher`. Reach for PowerFx only when the matcher vocabulary genuinely can't express the condition.

| Condition shape | Preferred predicate | Why |
|---|---|---|
| `category = "billing"` | `PropertyMatcher` (`Eq`) | Single property, literal value — exactly what matchers are for. |
| `quality >= 0.7` | `PropertyMatcher` (`Gte`) | Literal threshold; no PowerFx needed. |
| `quality < 0.7 AND retryCount < 3` (literal thresholds) | `AllOf` of two `PropertyMatcher`s | Composable without a formula. |
| `quality < qualityTarget` (two state properties) | **PowerFx** `=Local.quality < Local.qualityTarget` | Matchers compare against literals, not other properties. |
| `Len(answer) < 200` | **PowerFx** | No matcher operator for string length. |
| `StartsWith(intent, "BILLING_")` | **PowerFx** | No matcher operator for prefix match. |
| Regex, fuzzy match, external config lookup | `HandlerRef` | Code escape hatch — the runtime resolves a DI-registered `IGraphEdgePredicate`. |

A practical rule: if you'd need to plumb a threshold into the manifest *and* the manifest already wants to be reusable, the thresholds belong in state and the comparison belongs in PowerFx.

## Clean up

```bash
vais delete-graph quality-loop
vais delete drafter
vais delete grader
```

## What you built

- A graph that loops on its own output, with both the quality bar and the retry budget supplied at invoke time.
- An inline PowerFx edge predicate that compares two state properties — a shape `PropertyMatcher` can't express.
- An `increment` edge effect that updates a counter as the loop iterates, with no node code.

## Next

- **[Connect OpenWebUI](connect-openwebui.md)** — point a browser UI at the runtime and chat with agents and graphs.
- [Graph predicate operators reference](../reference/graph-predicate-operators.md) — the full PowerFx contract: `Local.*` namespace, `Local.lastMessage` shortcut, supported PowerFx functions, validation behaviour, `HandlerRef` escape hatch.
- [Concepts → Graph orchestration](../concepts/graph-orchestration.md) — where predicates fit in the super-step loop.
- [`samples/GraphPowerFxPredicates`](../../samples/GraphPowerFxPredicates) — the in-process variant of this pattern with a scripted provider (no API key).
