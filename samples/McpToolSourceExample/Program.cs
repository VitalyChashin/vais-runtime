// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using ModelContextProtocol.Client;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Protocols.Mcp;

// -----------------------------------------------------------------------------
// McpToolSourceExample — shows how McpToolSource wraps a connected McpClient
// and feeds its tool catalogue into AggregatingToolRegistry.BuildAsync.
//
// The adapter wraps a pre-connected McpClient — the caller owns the transport
// setup (stdio / streamable-HTTP). This sample outlines the shape of a real
// consumer; invocation against a live MCP server is deliberately out of scope
// because transport setup is server-specific. To actually hit a server:
//
//   1. npm install -g @modelcontextprotocol/server-filesystem
//   2. Construct an McpClient pointing at `mcp-server-filesystem`'s stdio
//      transport — see ModelContextProtocol.Core README.
//   3. Pass it into McpToolSource like this sample does.
// -----------------------------------------------------------------------------

Console.WriteLine("McpToolSource — wrapping shape demo");
Console.WriteLine();
Console.WriteLine("In a real consumer you'd connect an McpClient first, e.g.:");
Console.WriteLine();
Console.WriteLine("  await using var client = await McpClient.CreateAsync(transport);");
Console.WriteLine("  IToolSource mcpSource = new McpToolSource(client);");
Console.WriteLine();
Console.WriteLine("Then compose with static tools via AggregatingToolRegistry:");
Console.WriteLine();
Console.WriteLine("  var registry = await AggregatingToolRegistry.BuildAsync(");
Console.WriteLine("      staticTools: Array.Empty<ITool>(),");
Console.WriteLine("      sources: new[] { mcpSource });");
Console.WriteLine();
Console.WriteLine("  var agent = new StatefulAiAgent(provider,");
Console.WriteLine("      new StatefulAgentOptions { ToolRegistry = registry });");
Console.WriteLine();
Console.WriteLine("The MCP server's tools surface in registry.Tools and the agent's");
Console.WriteLine("outer tool-call loop dispatches them uniformly via IToolCallDispatcher.");
Console.WriteLine();
Console.WriteLine("Type-presence check (shows the adapter types resolve):");
Console.WriteLine($"  McpToolSource: {typeof(McpToolSource).FullName}");
Console.WriteLine($"  McpClient:     {typeof(McpClient).FullName}");

// Compile-time ref proving the wrapping shape typechecks (even though we don't invoke).
static IToolSource WrappingShape(McpClient client) => new McpToolSource(client);
_ = (Func<McpClient, IToolSource>)WrappingShape;
