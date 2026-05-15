# Shape output with schema-guided reasoning

You'll build a `ticket-triage` graph whose classifier emits a JSON object whose **schema dictates the reasoning order** — observations first, decision next, confidence last — and a PowerFx edge routes low-confidence cases to a `human-review` node. End state: every classifier reply is gateway-validated JSON; the orchestrator lifts each field into graph state; one edge predicate decides whether the run answers automatically or escalates. About 25 minutes.

## Why this matters

[Schema-guided reasoning](https://abdullin.com/schema-guided-reasoning/) (SGR) is the technique of using a JSON schema to *order the model's reasoning*, not just shape its output. The model produces JSON token-by-token left-to-right; listing `observations` before `category` forces the model to write evidence before committing to a label. The category is then conditioned on the observations the model itself wrote — a cheap, durable form of chain-of-thought that downstream nodes can inspect.

Two distinct things matter for SGR in a Vais.Agents runtime:

- The **schema is in the system prompt**, not just on the manifest — the model has to *read* the schema for the ordering to bite. The `outputSchema` field on the manifest is currently intent documentation and graph-validator input; it does not yet flow to providers as `response_format`.
- The **structured fields are first-class state** — `stateBindings.output` lifts each top-level JSON property into the graph's state bag, so a downstream node can `stateBindings.input` only the fields it cares about, and a PowerFx edge can route on any of them.

> *"we are not replacing the entire prompt with structured output. We just don't rely only on prompt in order to force LLM to follow a certain reasoning process precisely."*
> — Rinat Abdullin

## Prerequisites

- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it (`vais config use-context local`).
- `OPENAI_API_KEY` exported on the runtime side.
- Completed [Compose a multi-agent graph](compose-a-multi-agent-graph.md) — this tutorial leans on `stateBindings.input/output`.

## Step 1 — Define the SGR schema

The schema is the reasoning template. Each field is one step the model must commit to in order. Save as `ticket-classifier.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: ticket-classifier
  version: "1.0"
  description: Classifies a support ticket using schema-guided reasoning.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You triage support tickets. Reply with a JSON object only — no preamble,
      no code fences, no trailing prose. The object MUST contain these fields
      in this exact order:

        observations: 2-5 short strings naming concrete signals from the
                      ticket text (product mentioned, error code, tone,
                      time pressure).
        category:     one of "BILLING", "TECHNICAL", "OTHER".
        urgency:      one of "LOW", "MEDIUM", "HIGH".
        confidence:   number 0..1 — your confidence in the category. If the
                      ticket is fewer than 10 words, confidence MUST NOT
                      exceed 0.5.
        next_action:  one sentence describing what the human responder
                      should do next.

      List the observations first; choose the category only after you have
      written them.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  llmGatewayRef: sgr-gateway
  outputSchema:
    type: object
    required: [observations, category, urgency, confidence, next_action]
    properties:
      observations:
        type: array
        items: { type: string }
        minItems: 2
        maxItems: 5
      category: { type: string, enum: [BILLING, TECHNICAL, OTHER] }
      urgency:  { type: string, enum: [LOW, MEDIUM, HIGH] }
      confidence: { type: number, minimum: 0, maximum: 1 }
      next_action: { type: string }
  tools: []
```

**What the schema is doing — and what it isn't:**

- The **field order in the system prompt** is load-bearing. The model writes the JSON left-to-right, so `observations` lands in the response before `category` does. By the time the model commits to a category, it has already written the evidence in its own output — a forced rationale.
- The `confidence` constraint on short tickets is defensive: ambiguous inputs should produce ambiguous answers. Without a guard like this, models tend to assert high confidence on anything.
- `outputSchema` on the manifest is **not** propagated to the provider as `response_format` today. Its current jobs are (a) documenting the contract for human readers, and (b) feeding the graph validator, which checks that every `stateBindings.output` key appears in `outputSchema.properties`. The model complies with the schema because the system prompt asks it to.

## Step 2 — Wire post-facto validation

The `StructuredOutput` middleware fails the call if the model's reply isn't parseable JSON. That guards against the most common LLM failure mode: a leading "Sure, here's the JSON:" or a wrapping code fence.

Save as `sgr-gateway.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: LlmGatewayConfig
metadata:
  id: sgr-gateway
  version: "1.0"
  description: Logging + OTel + post-facto JSON validation for SGR agents.
spec:
  middleware:
    - name: LlmLogging
    - name: LlmOtel
    - name: StructuredOutput
```

Apply both — gateway first, agent second (the agent's `llmGatewayRef` is validated eagerly):

```bash
vais apply -f sgr-gateway.yaml
vais apply -f ticket-classifier.yaml
```

Invoke once to confirm:

```bash
vais invoke ticket-classifier --text "I was charged twice for plan Pro last Tuesday."
```

```json
{
  "observations": ["billing issue", "duplicate charge", "plan Pro mentioned", "specific date reference"],
  "category": "BILLING",
  "urgency": "MEDIUM",
  "confidence": 0.95,
  "next_action": "Verify the duplicate charge in the billing system and issue a refund if confirmed."
}
```

If the model emits anything that isn't well-formed JSON, the middleware throws an `AgentGuardrailDeniedException` at the output layer and the call fails — the caller never sees a half-parsed reply. The named `StructuredOutput` middleware validates well-formedness only (parseable JSON, any shape). A strongly-typed `LlmJsonOutputMiddleware<T>` is available for C# consumers in `agentic/src/Vais.Agents.Gateways.StructuredOutput/` but isn't named-resolvable from YAML today.

## Step 3 — Cascade: feed structured fields to a downstream node

Each top-level property in the classifier's JSON reply is lifted into graph state by name, so the responder can consume only the fields it cares about. Save as `ticket-responder.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: ticket-responder
  version: "1.0"
  description: Drafts a customer-facing reply from the classifier's structured fields.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You draft a 2-3 sentence reply to a customer. You receive the ticket
      plus a triage decision: category, urgency, and a recommended next action.
      Acknowledge the issue, mirror the urgency, and propose the next action.
      Reply with plain prose only — no JSON, no headings.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Compose them as `ticket-triage.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: ticket-triage
  version: "1.0"
  description: Schema-guided triage with cascade to a responder.
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref: { id: ticket-classifier, version: "1.0" }
      stateBindings:
        input:  [ticket]
        output: [observations, category, urgency, confidence, next_action]
    - id: respond
      kind: Agent
      ref: { id: ticket-responder, version: "1.0" }
      stateBindings:
        input:  [ticket, category, urgency, next_action]
    - id: end
      kind: End
  edges:
    - from: classify
      to: respond
    - from: respond
      to: end
```

```bash
vais apply -f ticket-responder.yaml
vais apply -f ticket-triage.yaml
```

The graph validator silently accepts this because every key in `classify.stateBindings.output` appears in the classifier's `outputSchema.properties`. Invoke with `--stream`:

```bash
vais invoke-graph ticket-triage \
  --initial-state '{"ticket": "I was charged twice for plan Pro last Tuesday."}' \
  --stream
```

```
▶ graph.started   HH:MM:SS.mmm  ticket-triage v1.0  run=<runId>
▷ node.started    HH:MM:SS.mmm  step=0  classify (Agent)
~ state.updated   HH:MM:SS.mmm  step=0  [lastAssistantText, messages, observations, category, urgency, confidence, next_action]
→ edge.traversed  HH:MM:SS.mmm  step=0  classify → respond
◁ node.completed  HH:MM:SS.mmm  step=0  classify (Nms)
▷ node.started    HH:MM:SS.mmm  step=1  respond (Agent)
~ state.updated   HH:MM:SS.mmm  step=1  [lastAssistantText, messages]
→ edge.traversed  HH:MM:SS.mmm  step=1  respond → end
◁ node.completed  HH:MM:SS.mmm  step=1  respond (Nms)
▷ node.started    HH:MM:SS.mmm  step=2  end (End)
◁ node.completed  HH:MM:SS.mmm  step=2  end (Nms)
■ graph.completed HH:MM:SS.mmm  step=3  final=end (Nms)
```

The `state.updated` event after `classify` carries every key the classifier's schema declared. The orchestrator parses the agent's last message as JSON and lifts each top-level property into state by name; `category`, `urgency`, and `next_action` are then available to the responder's `stateBindings.input`. If the model had emitted unparseable text, the gateway middleware would have rejected the call before this step; if no `output` binding had matched, the entire text would fall on the first declared output key.

## Step 4 — Route on a structured field

Cascade is one SGR pattern; routing is another. The classifier's `confidence` field is now a normal state key, so a PowerFx edge can branch on it. Add a `human-review` node and a guarded edge — update `ticket-triage.yaml`:

```yaml
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref: { id: ticket-classifier, version: "1.0" }
      stateBindings:
        input:  [ticket]
        output: [observations, category, urgency, confidence, next_action]
    - id: respond
      kind: Agent
      ref: { id: ticket-responder, version: "1.0" }
      stateBindings:
        input:  [ticket, category, urgency, next_action]
    - id: human-review
      kind: Agent
      ref: { id: ticket-responder, version: "1.0" }
      stateBindings:
        input:  [ticket, observations, category, urgency, confidence, next_action]
    - id: end
      kind: End
  edges:
    - from: classify
      to: human-review
      when: "=Local.confidence < 0.7"
    - from: classify
      to: respond
      when: always
    - from: respond
      to: end
    - from: human-review
      to: end
```

`human-review` here is the responder agent reused with a wider input binding — enough to demonstrate the routing edge fires. In production it would be a HITL node or a dedicated escalation agent.

Re-apply and force the low-confidence path with an ambiguous ticket:

```bash
vais apply -f ticket-triage.yaml
vais invoke-graph ticket-triage --initial-state '{"ticket": "hi"}' --stream
```

```
▶ graph.started   HH:MM:SS.mmm  ticket-triage v1.0  run=<runId>
▷ node.started    HH:MM:SS.mmm  step=0  classify (Agent)
~ state.updated   HH:MM:SS.mmm  step=0  [lastAssistantText, messages, observations, category, urgency, confidence, next_action]
→ edge.traversed  HH:MM:SS.mmm  step=0  classify → human-review
◁ node.completed  HH:MM:SS.mmm  step=0  classify (Nms)
▷ node.started    HH:MM:SS.mmm  step=1  human-review (Agent)
…
```

Edges are evaluated **in manifest order**, so the guarded edge to `human-review` is checked first; the `when: always` edge to `respond` is the catch-all. PowerFx mechanics (`Local.*` namespace, function vocabulary, `maxSteps` for cycles) are covered in [Route graph edges with PowerFx](route-graph-edges-with-powerfx.md) — what this step demonstrates is the SGR-specific point: **routing only exists because `confidence` was lifted into state as its own key**. Without the schema, the model would still be emitting confidence somewhere in its reply, but no edge predicate could see it.

## Step 5 — The cycle pattern

The third SGR pattern, *cycle* (judge → retry until quality clears a bar), is exactly the `quality-loop` graph built in [Route graph edges with PowerFx](route-graph-edges-with-powerfx.md). Combine it with SGR by giving the grader an `outputSchema` whose first field is `critique` (the observations-equivalent) followed by `quality` (the decision); the model writes the critique before committing to the number, and the loop edge gates on `quality`. The two tutorials compose without modification.

## When SGR vs. plain JSON-mode

Default to plain JSON output (one decision field, no observations). Reach for SGR when the decision benefits from being conditioned on evidence the model writes down first.

| Output shape | Use | Why |
|---|---|---|
| Single yes/no or single label | Plain JSON — one field | No reasoning to scaffold; SGR's `observations` field is overhead. |
| Decision conditioned on multiple signals | **SGR** — observations + decision + confidence | Forces the model to write evidence before committing; the observations become a low-cost audit trail. |
| Decision the operator must audit weeks later | **SGR** | The observations field is the audit log; persisted alongside the decision in graph state. |
| Need provider-side schema enforcement (`response_format: { type: "json_schema" }`) | Not implemented today | `outputSchema` on the manifest documents intent and feeds the graph validator; it does not yet flow to provider APIs. Track this gap before relying on it. |

## Clean up

```bash
vais delete-graph ticket-triage
vais delete ticket-classifier
vais delete ticket-responder
vais delete llm-gateways/sgr-gateway
```

## What you built

- A classifier agent whose schema **prescribes the reasoning order** (observations → category → urgency → confidence → next action) — the model writes evidence before committing to a label.
- A gateway-validated JSON path: malformed replies fail at the middleware, never reach downstream nodes.
- A cascade across nodes: each top-level field in the classifier's JSON is lifted into graph state and consumable by downstream nodes via `stateBindings.input`.
- A PowerFx routing edge on a structured field (`confidence`) — escalates ambiguous cases to a different branch.
- A composable cycle pattern by combining SGR with the `quality-loop` from the PowerFx tutorial.

## Next

- **[Dispatch from a graph node with agent-as-tool](dispatch-from-a-graph-node.md)** — let the coordinator's LLM pick a specialist agent instead of a static edge.
- **[Connect OpenWebUI](connect-openwebui.md)** — point a browser UI at the runtime.
- [Rinat Abdullin — Schema-Guided Reasoning](https://abdullin.com/schema-guided-reasoning/) — the original article that named the pattern.
- [Reference → Manifest schema](../reference/manifest-schema.md) — `outputSchema` field on agents.
- [Plug-in gateway middleware](../guides/plug-in-gateway-middleware.md) — the full middleware catalog, including structured-output validation.
