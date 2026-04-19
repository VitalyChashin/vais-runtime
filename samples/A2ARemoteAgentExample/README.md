# A2ARemoteAgentExample

Wraps a stubbed `IA2AClient` via `A2ARemoteAgentTool`, exposes it in the agent's registry, drives a turn through a scripted provider that decides to delegate. Real consumers use `A2ARemoteAgentTool.CreateAsync(uri)` to resolve the remote `AgentCard` + build a live `A2AClient`; the stub here lets the sample run offline.

**Concepts:** [interop](../../docs/concepts/interop.md), [tools](../../docs/concepts/tools.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Protocols.A2A`, `A2A`.
**Needs API key:** no.

```bash
dotnet run --project samples/A2ARemoteAgentExample
```
