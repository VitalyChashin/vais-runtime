# McpToolSourceExample

Shape of how `McpToolSource` wraps a connected `McpClient`. Adapter invocation against a real MCP server is deliberately out of scope — transport setup (stdio / streamable-HTTP) is server-specific. The sample prints the wrapping pattern so a reader can paste it into a real-transport setup.

**Concepts:** [interop](../../docs/concepts/interop.md), [tools](../../docs/concepts/tools.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Protocols.Mcp`, `ModelContextProtocol.Core`.
**Needs API key:** no.
**Needs real MCP server for actual invocation:** yes (out of sample scope).

```bash
dotnet run --project samples/McpToolSourceExample
```

For an end-to-end MCP server setup, follow the ModelContextProtocol.Core README.
