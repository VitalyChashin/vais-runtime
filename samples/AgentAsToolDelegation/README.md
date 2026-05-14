# AgentAsToolDelegation

Shows `LocalAgentTool` — the `agent:<name>` tool source that wraps a sub-agent running in the same runtime as a first-class `ITool`.

A coordinator receives a math question, delegates it to a `math-specialist` sub-agent via tool call, and returns the specialist's answer as its final reply.

## Key concepts

- **`LocalAgentTool`** wraps any agent in an `IAgentRuntime` as an `ITool` the coordinator model can call.
- **Session lifecycle** — each blocking call creates a deterministic session keyed on `runId + toolName + argHash`; the session is removed after the call so state does not accumulate.
- **Context propagation** — `AgentContext.UserId`, `TenantId`, `WorkspaceId`, and `MaxChainDepth` (decremented by 1) flow into the child agent automatically.
- **`runtimeFactory` avoids the DI cycle** — the factory lambda is captured at construction time and resolved lazily at first invocation.

## Run

```bash
dotnet run
```

## What the output shows

```
Schema the coordinator model sees for the math tool:
  {"type":"object","properties":{"message":{"type":"string",...}},"required":["message"]}

User → coordinator: "What is 42 × 7?"
Coordinator → user: "The math specialist confirmed: 42 × 7 = 294."

Round-trip complete — math-specialist was invoked in-process via LocalAgentTool.
```

## Using the declarative manifest

In production you declare this in YAML instead of wiring it in code:

```yaml
kind: agent
spec:
  id: coordinator
  localAgents:
    - name: math-specialist
      agentId: math-specialist          # resolves in IAgentRegistry
  tools:
    - source: agent:math-specialist
      name: call_math_specialist
      description: Delegate arithmetic to the math-specialist sub-agent.
```

`AgentManifestTranslator` resolves the `agent:` source and creates a `LocalAgentTool` with the same runtime factory wiring shown here.

## See also

- Guide: `docs/guides/delegate-to-a-local-agent.md`
- Sample: `samples/HandoffBetweenAgents/` — event-driven handoff (vs. direct tool call)
- Sample: `samples/A2ARemoteAgentExample/` — cross-runtime peer delegation via A2A
