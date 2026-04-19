# HandoffBetweenAgents

Consumer-driven handoff. Triage agent routes a billing question to a billing agent; `HandoffRequested` event visible on the shared bus.

**Concepts:** [orchestration](../../docs/concepts/orchestration.md), [events](../../docs/reference/events.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.InMemory`.
**Needs API key:** no.

```bash
dotnet run --project samples/HandoffBetweenAgents
```
