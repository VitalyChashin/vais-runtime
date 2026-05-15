# Dispatch from a graph node with agent-as-tool

You'll declare three specialist agents, a triage coordinator that calls them as tools, and a graph that runs the coordinator before a tone-polishing pass. About 25 minutes. End state: a `support-graph` where node `triage` lets the LLM dispatch dynamically to `billing-specialist`, `tech-support`, or `refund-handler` (no edges needed between specialists), node `respond` rewrites the draft in brand voice, and the same graph trivially upgrades to background fan-out by flipping one field.

## Why this matters

Edges route on state. `compose-a-multi-agent-graph` and `route-graph-edges-with-powerfx` showed two ways to do it: literal-value matchers and inline PowerFx. Both move work between nodes by inspecting graph state.

Some routing is better decided by the model itself. Customer-support triage is the canonical case: the question can touch billing, technical, refunds, account changes, or none of the above; you don't want a 12-edge fan-out off one classifier node, and you definitely don't want to write a `Switch(category, "billing", "tech", ...)` PowerFx formula that the LLM is going to second-guess anyway.

The agent-as-tool pattern — `localAgents` + `tools[].source = agent:<name>` — turns sub-agents into tool calls. The coordinator's LLM picks which one to invoke based on the question. Sub-agents run *inside* the coordinator's super-step; the graph sees one node turn even though the model may have called three specialists. This is the P7 default for sibling delegation: hierarchical, O(k log k), no peer A2A negotiation.

## Prerequisites

- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it (`vais config use-context local`).
- `OPENAI_API_KEY` exported on the runtime side.
- The `compose-a-multi-agent-graph` tutorial finished — you're already comfortable with `vais apply`, state bindings, and `--stream` traces.

## Part 1 — Three specialists

Each specialist is a tiny declarative agent with one job. Keep their system prompts narrow — the coordinator will only see them through the tool description, so any drift between description and behaviour shows up as bad dispatch decisions.

Save as `billing-specialist.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: billing-specialist
  version: "1.0"
  description: Answers questions about invoices, charges, and subscription billing.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You are a billing specialist. Answer billing, invoice, and subscription
      questions in 1-2 sentences. If the question is not billing-related,
      reply exactly: "out of my domain".
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Save as `tech-support.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: tech-support
  version: "1.0"
  description: Answers technical questions about product features and integrations.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You are a technical support engineer. Answer product feature and
      integration questions in 1-2 sentences. If the question is not
      technical, reply exactly: "out of my domain".
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Save as `refund-handler.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: refund-handler
  version: "1.0"
  description: Handles refund eligibility and refund process questions.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You handle refund questions. State refund eligibility and the next step
      in 1-2 sentences. If the question is not about refunds, reply exactly:
      "out of my domain".
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

Apply all three:

```bash
vais apply -f billing-specialist.yaml
vais apply -f tech-support.yaml
vais apply -f refund-handler.yaml
```

Smoke-test one:

```bash
vais invoke billing-specialist --text "Why is my invoice 2x what it was last month?"
# Your invoice likely doubled due to a plan upgrade or added users this cycle...
```

## Part 2 — The triage coordinator

The coordinator declares the specialists as `localAgents` and exposes each one as a tool the LLM can call. The tool description is what the LLM reads when deciding which specialist to dispatch — make it crisp.

Save as `triage.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: triage
  version: "1.0"
  description: Triages a customer question to the right specialist and returns the answer.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You are a customer support coordinator. Read the customer question and
      call exactly one specialist tool to get the answer. If no specialist
      applies, reply "I can't help with that — please contact a human agent."
      Once a specialist responds, return their answer verbatim — do not rewrite.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  localAgents:
    - name: billing
      agentId: billing-specialist
      description: Ask the billing specialist about invoices, charges, or subscription billing.
    - name: tech
      agentId: tech-support
      description: Ask technical support about product features or integrations.
    - name: refund
      agentId: refund-handler
      description: Ask the refund handler about refund eligibility or process.
  tools:
    - source: agent:billing
      name: ask_billing
    - source: agent:tech
      name: ask_tech_support
    - source: agent:refund
      name: ask_refund_handler
```

The tool description the coordinator's LLM sees is `localAgents[].description` (or — when omitted — the target agent's manifest `description`). The `tools[]` entry only carries the dispatch wiring: `name` (what the model calls) and `source` (where it lands). Putting `description:` under `tools[]` is silently ignored by the manifest loader.

```bash
vais apply -f triage.yaml
vais invoke triage --text "How do I get a refund for an unused month?"
# You can request a refund for the unused portion via the billing page; ...
```

That single `vais invoke` call exercised the full agent-as-tool path: the coordinator's LLM saw three tools, picked `ask_refund_handler`, the runtime spun up a fresh `refund-handler` session, returned its reply through the tool result, and the coordinator returned the answer.

**What just happened under the hood:**

- The `AgentManifestTranslator` saw `tools[].source = agent:refund`, looked up `refund` in `localAgents`, found `agentId: refund-handler`, and built a `LocalAgentTool` pointing at `IAgentRuntime`.
- At tool-call time the runtime derived a deterministic session id (`SHA256(runId + toolName + argHash)`), created the session, ran the specialist, and tore the session down — no state accumulation.
- `AgentContext` (`UserId`, `TenantId`, `WorkspaceId`) propagated into the child; `MaxChainDepth` decremented by 1, so a runaway delegation chain dies at zero.

## Part 3 — The polish pass

Add a second agent that takes the triage draft and rewrites it in brand voice. This is the second graph node — the one that makes this a graph rather than a single-node invocation.

Save as `respond.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: respond
  version: "1.0"
  description: Rewrites a specialist draft in warm, brand-friendly tone.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      Rewrite the draft answer in a warm, customer-friendly tone (2-3 sentences).
      Keep every fact intact. Do not add information. Do not apologise.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  tools: []
```

```bash
vais apply -f respond.yaml
```

## Part 4 — Compose the graph

The graph is a straight line: `triage → respond → end`. The interesting routing — picking which specialist — happens inside the `triage` node turn and never reaches the graph edge layer.

Save as `support-graph.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: support-graph
  version: "1.0"
  description: Triage a customer question to a specialist, then rewrite in brand voice.
spec:
  entry: triage
  nodes:
    - id: triage
      kind: Agent
      ref:
        id: triage
        version: "1.0"
      stateBindings:
        input: [question]
        output: [draft]
    - id: respond
      kind: Agent
      ref:
        id: respond
        version: "1.0"
      stateBindings:
        input: [draft]
        output: [answer]
    - id: end
      kind: End
  edges:
    - from: triage
      to: respond
    - from: respond
      to: end
```

```bash
vais apply -f support-graph.yaml
vais get-graphs
# ID              VERSION   ENTRY    NODES   DESCRIPTION                              LABELS
# support-graph   1.0       triage   3       Triage a customer question to a spec...  -
```

## Part 5 — Invoke and observe

```bash
vais invoke-graph support-graph \
  --initial-state '{"question": "How do I get a refund for an unused month?"}' \
  --stream
```

You'll see:

```
▶ graph.started  HH:MM:SS.mmm  support-graph v1.0  run=<runId>
▷ node.started   HH:MM:SS.mmm  step=0  triage (Agent)
~ state.updated  HH:MM:SS.mmm  step=0  [lastAssistantText, messages, draft]
→ edge.traversed HH:MM:SS.mmm  step=0  triage → respond
◁ node.completed HH:MM:SS.mmm  step=0  triage (Nms)
▷ node.started   HH:MM:SS.mmm  step=1  respond (Agent)
~ state.updated  HH:MM:SS.mmm  step=1  [lastAssistantText, messages, answer]
→ edge.traversed HH:MM:SS.mmm  step=1  respond → end
◁ node.completed HH:MM:SS.mmm  step=1  respond (Nms)
▷ node.started   HH:MM:SS.mmm  step=2  end (End)
◁ node.completed HH:MM:SS.mmm  step=2  end (Nms)
■ graph.completed HH:MM:SS.mmm  step=3  final=end (Nms)
```

**Notice what's *not* in the trace.** The graph reports one `node.completed triage` event — not three (one per specialist). The specialist invocation lives inside the coordinator's super-step. The trade-off: graph-level observability stays clean and per-node metrics are meaningful (one node = one team's agent), but to see *which* specialist the coordinator picked you read the Langfuse trace or the agent-level log, not the graph event stream.

Run again with a question that wanders out of all three lanes:

```bash
vais invoke-graph support-graph \
  --initial-state '{"question": "Can you recommend a sci-fi novel?"}' \
  --stream
```

The triage coordinator returns the "contact a human" fallback; the polish pass rewrites it warmly; no specialist tool is called. Same graph shape, different LLM decision inside the `triage` super-step.

## Part 6 — Background mode for slow sub-agents

Blocking is the right default. Switch to background when a sub-agent takes long enough that you'd rather kick it off and check back — a deep-research scan, a long-running scrape, anything you'd want to fan out in parallel.

Update `triage.yaml`, flipping the `refund` binding to background:

```yaml
  localAgents:
    - name: billing
      agentId: billing-specialist
    - name: tech
      agentId: tech-support
    - name: refund
      agentId: refund-handler
      mode: Background        # ← was Blocking (default)
```

```bash
vais apply -f triage.yaml
```

Now the `ask_refund_handler` tool returns a handle to the **coordinator's LLM** immediately instead of blocking on the specialist:

```json
{"handle": "run-abc__ask_refund_handler__a1b2c3d4", "status": "pending"}
```

That JSON is the **tool result the coordinator sees as a message**, not the final reply the user gets back from `vais invoke`. What the user sees depends on what the coordinator's LLM does next — by default it will synthesise a plausible-sounding answer from the handle (often a generic acknowledgement, since the actual specialist hasn't finished). To get useful behaviour out of background mode you need either an inline polling instruction in the system prompt or a downstream node that aggregates the handle — both options are sketched below.

Three management tools appear on the coordinator's tool list automatically (injected once per manifest, idempotent across multiple background bindings):

| Tool | Args | Returns |
|---|---|---|
| `list_background_agents` | none | All run records for the current coordinator run |
| `view_background_agent` | `{"handle": "..."}` | Single record (status: `pending` / `running` / `completed` / `failed`) |
| `cancel_background_agent` | `{"handle": "..."}` | `{"handle": "...", "cancelled": true|false}` |

Two ways to wire the rest:

1. **Coordinator polls inline.** Add to the triage system prompt: *"After calling a background tool, poll `view_background_agent` until status is `completed`, then return the result."* The same `triage` node still produces a final answer; the trade-off is more LLM turns and a longer super-step.
2. **Downstream node aggregates.** The triage node returns the handle in `draft`; a new graph node (`aggregate`) reads the handle and waits / polls via its own tool calls. Useful when you want fan-out from one node (kick off N specialists) and a join in another.

For the demo we'll stop at "the tool returns a handle" — wiring the polling shape end-to-end is the [delegate-to-a-local-agent guide](../guides/delegate-to-a-local-agent.md)'s territory. The point of switching modes here is the P5 scaling contract:

| Property | Blocking | Background (Orleans) | Background (InMemory, dev only) |
|---|---|---|---|
| Survives silo restart | ✅ child grain durable; parent re-executes; tool calls replay via journal | ✅ run grain persists; reactivates and resumes | ❌ lost on process restart |
| Status / cancel from any silo | n/a (synchronous) | ✅ grain calls route across cluster | ❌ process-local only |
| Cross-silo visibility | n/a | ✅ index grain keyed by `parentRunId` | ❌ process-local only |

The runtime container picks Orleans automatically when it's running on a silo — you don't configure the tracker per agent. For local dev with `InMemoryAgentRuntime`, swap `InMemoryBackgroundAgentTracker` in code (the sample at `samples/AgentAsToolDelegation/` shows it).

## Agent-as-tool vs explicit graph edges

Don't reach for agent-as-tool when an edge would do. Cheat sheet:

| Routing shape | Use |
|---|---|
| Linear pipeline (`A → B → C`) | Graph edges. |
| Conditional on a state value (`if quality < 0.7 retry`) | Edge predicate (`PropertyMatcher` or PowerFx). |
| One coordinator picks among N sibling specialists by reading the question | **agent-as-tool** — LLM dispatch beats edge fan-out. |
| Fan-out + join across long-running sub-agents | **agent-as-tool, `mode: Background`** with a downstream aggregate node. |
| Re-home the conversation (no return value) | `handoffs[]` — the conversation continues in the target agent, not as a tool result. |
| Sub-agent is a separate deployment / different team | `a2a:<url>` instead of `agent:<name>`. |

A useful test: if you'd have to name the routing keys in advance for `PropertyMatcher` to fire, and the LLM is going to set those keys anyway by reading the question, skip the round-trip and let the LLM dispatch directly.

## Clean up

```bash
vais delete-graph support-graph
vais delete triage
vais delete respond
vais delete billing-specialist
vais delete tech-support
vais delete refund-handler
```

## What you built

- Three single-purpose specialist agents and a coordinator that exposes each one as a named tool via `localAgents` + `tools[].source = agent:<name>`.
- A graph where dispatch (which specialist) is decided by the LLM inside one super-step, and graph orchestration (next node, state bindings) is decided by manifest edges around it.
- A one-field switch (`mode: Background`) that converts the same coordinator into fire-and-forget mode with three auto-injected management tools.

## Next

- **[Connect OpenWebUI](connect-openwebui.md)** — point a browser UI at the runtime and chat with this graph end-to-end.
- [Delegate to a local agent](../guides/delegate-to-a-local-agent.md) — depth guard, persistent sessions, `runtimeFactory` DI cycle, full background-aggregation wiring in code.
- [Delegate to an A2A remote agent](../guides/delegate-to-a2a-remote-agent.md) — when the sub-agent is owned by another team or runs in another runtime.
- [Concepts → Graph orchestration](../concepts/graph-orchestration.md) — where node turns and tool calls sit in the super-step model.
- [Reference → Manifest schema](../reference/manifest-schema.md#speclocalagents--localagentref) — `localAgents` field reference.
- `samples/AgentAsToolDelegation/` — in-process variant with a scripted provider (no API key).
