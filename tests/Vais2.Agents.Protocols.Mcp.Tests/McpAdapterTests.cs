// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using ModelContextProtocol.Client;
using Xunit;

namespace Vais2.Agents.Protocols.Mcp.Tests;

/// <summary>
/// Unit tests for the MCP adapter's public surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Integration scope.</b> <see cref="McpToolSource"/> + <see cref="McpBackedTool"/>
/// exercise <see cref="IMcpClient"/> extension methods (<c>EnumerateToolsAsync</c>,
/// <c>CallToolAsync</c>) which dispatch JSON-RPC requests through the client's
/// transport layer. Unit-testing them would require a real MCP server (stdio /
/// streamable-HTTP) or a full JSON-RPC-transport fake — both disproportionate
/// for this PR. Integration coverage lands with the v0.4 smoketest's MCP segment
/// or a future live-server test harness.
/// </para>
/// </remarks>
public sealed class McpToolInvocationExceptionTests
{
    [Fact]
    public void Carries_Tool_Name_And_Formats_Message()
    {
        var ex = new McpToolInvocationException("get_weather", "service unavailable");

        ex.ToolName.Should().Be("get_weather");
        ex.Message.Should().Contain("get_weather");
        ex.Message.Should().Contain("service unavailable");
    }
}

public sealed class McpToolSourceShapeTests
{
    [Fact]
    public void Ctor_Rejects_Null_Client()
    {
        Action act = () => _ = new McpToolSource(client: null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
