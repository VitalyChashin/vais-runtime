# A2AServerBasics

Host a `StatefulAiAgent` as an A2A endpoint. Boots an ASP.NET Core server, resolves the auto-derived `AgentCard`, and runs a message round-trip with a co-located `A2AClient`.

## Run

```bash
dotnet run --project samples/A2AServerBasics
```

## Expected output

```
Server: http://127.0.0.1:PORT/agents/greeter

agent-card: greeter — A friendly greeter agent.

reply: "Hello! I'm the greeter agent. How can I help?"
```

*(Port number varies.)*

## What it demonstrates

- `AddA2AAgentServer()` — registers the A2A task store (`InMemoryTaskStore` by default) + server options.
- `MapA2AAgentServer(baseUrl)` — mounts one endpoint pair per registered agent:
  - `POST /agents/greeter` — A2A JSON-RPC (send/receive messages).
  - `GET /agents/greeter/.well-known/agent-card.json` — auto-derived `AgentCard`.
- `AgentCardBuilder` — derives `Name`, `Description`, `SupportedInterfaces`, and `Capabilities` from the `AgentManifest` automatically; no hand-authored card needed.
- `A2ACardResolver` — client-side card discovery at the well-known URL.
- `A2AClient(Uri)` — constructs the client from the agent's endpoint URL.
- `SendMessageResponse.PayloadCase` — discriminated union: `Message` (direct reply) or `Task` (task-based).

## Production extension

- Swap `InMemoryTaskStore` for `OrleansTaskStore` (see `A2AInterruptResumeOrleans`) to make task state durable across restarts.
- Add `AddA2AAgentServerJwtAuth(o => ...)` + `UseAuthentication()` / `UseAuthorization()` for token-protected endpoints.

## Docs

- [Host agents as A2A endpoints](../../docs/guides/host-agents-as-an-a2a-endpoint.md)
- [`A2AInterruptResumeOrleans`](../A2AInterruptResumeOrleans) — adds interrupt/resume + durable task store
