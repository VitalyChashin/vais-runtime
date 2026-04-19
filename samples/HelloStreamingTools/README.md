# HelloStreamingTools

v0.4.1 tool-using streaming. Scripted provider emits preamble text → terminal tool-call update → continuation text. Outer loop dispatches the tool between streams; the consumer sees one continuous `IAsyncEnumerable<string>` of deltas.

**Concepts:** [execution loop](../../docs/concepts/execution-loop.md), [tools](../../docs/concepts/tools.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.InMemory`.
**Needs API key:** no.

```bash
dotnet run --project samples/HelloStreamingTools
```
