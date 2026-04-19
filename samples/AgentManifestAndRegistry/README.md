# AgentManifestAndRegistry

Builds an `AgentManifest` with every optional field, registers multiple versions via `InMemoryAgentRegistry`, fetches latest-lexicographic version, lists filtered by label prefix.

**Concepts:** [control plane](../../docs/concepts/control-plane.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
**Needs API key:** no.

```bash
dotnet run --project samples/AgentManifestAndRegistry
```
