# OrleansSilo

Single-process Orleans silo + client, memory providers only (no Docker). Addresses an agent session by `(agentId, sessionId)`; two turns confirm history survives between `StatefulAiAgent` instances pointing at the same session.

**Concepts:** [session + memory](../../docs/concepts/session.md), [persistence](../../docs/concepts/persistence.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.Orleans`.
**Needs API key:** no.

```bash
dotnet run --project samples/OrleansSilo
```

Swap the memory grain storage for Redis / Postgres via the `OrleansRedisPersistence` / `OrleansPostgresPersistence` sister samples.
