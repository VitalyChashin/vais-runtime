// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.ScriptRuntime.Tests;

/// <summary>CM-3 — the run_code tool: request shaping, ambient context + token minting, P9 error mapping.</summary>
public sealed class RunCodeToolTests
{
    private static RunCodeTool Build(IScriptRuntimeClient client, ICallTokenService tokens) =>
        new(
            agentId: "agent-1",
            prelude: "var tools={};",
            limits: new CodeModeLimits(),
            client: client,
            callTokens: tokens,
            options: new ScriptRuntimeOptions { GatewayBaseUrl = "http://gw.local" },
            logger: NullLogger<RunCodeTool>.Instance);

    private static JsonElement Code(string code)
    {
        using var doc = JsonDocument.Parse($"{{\"code\":{JsonSerializer.Serialize(code)}}}");
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Success_ReturnsResult_AndBuildsGatewayBoundRequest()
    {
        ScriptRunRequest? captured = null;
        var client = Substitute.For<IScriptRuntimeClient>();
        client.RunAsync(Arg.Do<ScriptRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new ScriptRunResponse { Result = "42" });

        var tokens = Substitute.For<ICallTokenService>();
        tokens.Generate("run-1", "agent-1", Arg.Any<AgentContextClaims>(), Arg.Any<int>()).Returns("tok");

        var tool = Build(client, tokens);
        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext(AgentName: "agent-1") { RunId = "run-1" });

        var result = await tool.InvokeAsync(Code("return 42;"));

        result.Should().Be("42");
        captured.Should().NotBeNull();
        captured!.RunId.Should().Be("run-1");
        captured.AgentId.Should().Be("agent-1");
        captured.CallToken.Should().Be("tok");
        captured.Prelude.Should().Be("var tools={};");
        captured.ToolGatewayUrl.Should().Be("http://gw.local/v1/container-gateway/tools/invoke");
    }

    [Fact]
    public async Task SidecarError_ThrowsCodeModeExecutionException_WithErrorType()
    {
        var client = Substitute.For<IScriptRuntimeClient>();
        client.RunAsync(Arg.Any<ScriptRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScriptRunResponse { Error = new ScriptRunError("ScriptError", "boom") });
        var tokens = Substitute.For<ICallTokenService>();
        tokens.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentContextClaims>(), Arg.Any<int>()).Returns("tok");

        var tool = Build(client, tokens);
        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { RunId = "run-1" });

        var act = async () => await tool.InvokeAsync(Code("boom();"));

        (await act.Should().ThrowAsync<CodeModeExecutionException>())
            .Which.ErrorType.Should().Be("ScriptError");
    }

    [Fact]
    public async Task EmptyCode_Throws_AndNeverCallsSidecar()
    {
        var client = Substitute.For<IScriptRuntimeClient>();
        var tool = Build(client, Substitute.For<ICallTokenService>());
        using var doc = JsonDocument.Parse("{}");

        var act = async () => await tool.InvokeAsync(doc.RootElement.Clone());

        await act.Should().ThrowAsync<CodeModeExecutionException>();
        await client.DidNotReceive().RunAsync(Arg.Any<ScriptRunRequest>(), Arg.Any<CancellationToken>());
    }
}
