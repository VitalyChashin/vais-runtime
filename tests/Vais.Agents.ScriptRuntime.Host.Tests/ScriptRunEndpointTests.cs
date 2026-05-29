// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Vais.Agents.ScriptRuntime.Host.Tests;

/// <summary>SR-3 — the sidecar HTTP surface: <c>GET /health</c> and <c>POST /v1/script/run</c>.</summary>
public sealed class ScriptRunEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        var resp = await factory.CreateClient().GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ScriptRun_NoTool_ReturnsResult()
    {
        var request = new ScriptRunRequest
        {
            RunId = "run-1",
            AgentId = "agent-1",
            Prelude = "",
            Script = "return 6 * 7;",
            ToolGatewayUrl = "http://unused.local/v1/container-gateway/tools/invoke",
            CallToken = "unused",
        };

        var resp = await factory.CreateClient().PostAsJsonAsync("/v1/script/run", request);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ScriptRunResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().BeNull();
        body.Result.Should().Be("42");
    }
}
