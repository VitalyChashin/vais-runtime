// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.ScriptRuntime.Tests;

/// <summary>CM-2 — the HTTP client to the ScriptRuntime sidecar.</summary>
public sealed class HttpScriptRuntimeClientTests
{
    [Fact]
    public async Task RunAsync_PostsToScriptRunEndpoint_AndDeserializesResponse()
    {
        string? path = null;
        var handler = new StubHandler(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"result\":\"hi\",\"toolCallCount\":2,\"wallMs\":7}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var client = new HttpScriptRuntimeClient(new HttpClient(handler) { BaseAddress = new Uri("http://sidecar.local/") });

        var response = await client.RunAsync(new ScriptRunRequest
        {
            RunId = "r",
            AgentId = "a",
            Prelude = "",
            Script = "return 1;",
            ToolGatewayUrl = "http://gw.local/v1/container-gateway/tools/invoke",
            CallToken = "tok",
        });

        path.Should().Be("/v1/script/run");
        response.Result.Should().Be("hi");
        response.ToolCallCount.Should().Be(2);
        response.WallMs.Should().Be(7);
        response.Error.Should().BeNull();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
