# ToolFromFunc

Typed-shortcut tool creation via `Tool.FromFunc<TInput, TOutput>` + no-arg `Tool.FromFunc<TOutput>`. Composes static tools + an `IToolSource` through `AggregatingToolRegistry.BuildAsync`. Prints the STJ-generated JSON schema for each.

**Concepts:** [tools](../../docs/concepts/tools.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
**Needs API key:** no.

```bash
dotnet run --project samples/ToolFromFunc
```
