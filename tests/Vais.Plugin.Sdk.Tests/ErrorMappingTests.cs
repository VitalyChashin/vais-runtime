// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents;
using Xunit;

namespace Vais.Plugin.Sdk.Tests;

// ---------------------------------------------------------------------------
// Test agents for the 502 / 503 / 504 paths
// ---------------------------------------------------------------------------

internal sealed class LlmFailAgent : ContainerPluginAgent
{
    public override Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default) =>
        throw new LlmGatewayException("gateway boom");
}

internal sealed class ToolFailAgent : ContainerPluginAgent
{
    public override Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default) =>
        throw new ToolException("tool boom");
}

internal sealed class ManualTimeoutAgent : ContainerPluginAgent
{
    public override Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default) =>
        throw new PluginTimeoutException("manual timeout");
}

/// <summary>Sleeps past any sane budget so the SDK's self-timeout fires.</summary>
internal sealed class SlowAgent : ContainerPluginAgent
{
    public override async Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        return new InvokeResponse { AssistantMessage = "never" };
    }
}

// ---------------------------------------------------------------------------
// Invoke status mapping (EC-9 / EC-11)
// ---------------------------------------------------------------------------

public sealed class ErrorMappingInvokeTests
{
    private static object RequestWithTimeout(int timeoutSeconds) => new
    {
        agentId = "test-agent",
        sessionId = "00000000-0000-0000-0000-000000000001",
        messages = new[] { new { role = "user", content = "hi" } },
        llmGatewayUrl = "http://mock/v1/llm",
        toolGatewayUrl = "http://mock/v1/tools",
        timeoutSeconds,
        context = new { callToken = "test-token" },
    };

    private static async Task<(HttpStatusCode Status, string ErrorType)> InvokeAsync<TAgent>(object body)
        where TAgent : ContainerPluginAgent, new()
    {
        await using var harness = await SdkTestHarness<TAgent>.CreateAsync();
        var resp = await harness.Client.PostAsJsonAsync("/v1/invoke", body);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (resp.StatusCode, json.TryGetProperty("errorType", out var et) ? et.GetString() ?? "" : "");
    }

    [Fact]
    public async Task LlmGatewayException_Returns502()
    {
        var (status, errorType) = await InvokeAsync<LlmFailAgent>(Requests.Basic());
        status.Should().Be(HttpStatusCode.BadGateway);
        errorType.Should().Be("LlmGatewayError");
    }

    [Fact]
    public async Task ToolException_Returns503()
    {
        var (status, errorType) = await InvokeAsync<ToolFailAgent>(Requests.Basic());
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        errorType.Should().Be("ToolError");
    }

    [Fact]
    public async Task PluginTimeoutException_Returns504()
    {
        var (status, errorType) = await InvokeAsync<ManualTimeoutAgent>(Requests.Basic());
        status.Should().Be(HttpStatusCode.GatewayTimeout);
        errorType.Should().Be("Timeout");
    }

    [Fact]
    public async Task SelfTimeout_Returns504()
    {
        var (status, errorType) = await InvokeAsync<SlowAgent>(RequestWithTimeout(1));
        status.Should().Be(HttpStatusCode.GatewayTimeout);
        errorType.Should().Be("Timeout");
    }
}

// ---------------------------------------------------------------------------
// Stream error events (EC-9 / EC-11)
// ---------------------------------------------------------------------------

public sealed class ErrorMappingStreamTests
{
    private static string? SseErrorType(string raw)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "event: error") continue;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (!lines[j].StartsWith("data: ")) continue;
                using var doc = JsonDocument.Parse(lines[j]["data: ".Length..].Trim());
                return doc.RootElement.GetProperty("errorType").GetString();
            }
        }
        return null;
    }

    [Fact]
    public async Task Stream_LlmGatewayException_EmitsErrorEvent()
    {
        await using var harness = await SdkTestHarness<LlmFailAgent>.CreateAsync();
        var resp = await harness.Client.PostAsJsonAsync("/v1/stream", Requests.Basic());
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        SseErrorType(raw).Should().Be("LlmGatewayError");
        raw.Should().Contain("event: done");
    }

    [Fact]
    public async Task Stream_SelfTimeout_EmitsTimeoutErrorEvent()
    {
        await using var harness = await SdkTestHarness<SlowAgent>.CreateAsync();
        var resp = await harness.Client.PostAsJsonAsync("/v1/stream", new
        {
            agentId = "a",
            sessionId = "00000000-0000-0000-0000-000000000001",
            messages = new[] { new { role = "user", content = "hi" } },
            llmGatewayUrl = "http://mock/v1/llm",
            toolGatewayUrl = "http://mock/v1/tools",
            timeoutSeconds = 1,
            context = new { callToken = "test-token" },
        });
        var raw = await resp.Content.ReadAsStringAsync();
        SseErrorType(raw).Should().Be("Timeout");
        raw.Should().Contain("event: done");
    }
}

// ---------------------------------------------------------------------------
// Gateway client auto-emission (EC-10 + D1)
// ---------------------------------------------------------------------------

public sealed class GatewayClientAutoEmitTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;
        public StubHandler(HttpStatusCode status, string json) { _status = status; _json = json; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private static HttpClient Http(HttpStatusCode status, string json = "{}") =>
        new(new StubHandler(status, json)) { BaseAddress = new Uri("http://gw/") };

    [Fact]
    public async Task LlmClient_Throws_LlmGatewayException_On5xx()
    {
        var client = new DefaultLlmGatewayClient(Http(HttpStatusCode.BadGateway), new RequestContext(), "a");
        var act = () => client.CompleteAsync([]);
        await act.Should().ThrowAsync<LlmGatewayException>();
    }

    [Fact]
    public async Task ToolClient_Throws_ToolException_OnNon2xx()
    {
        var client = new DefaultToolGatewayClient(Http(HttpStatusCode.ServiceUnavailable), new RequestContext(), "a");
        var act = () => client.InvokeAsync(new ToolCallRequest("t", JsonSerializer.SerializeToElement(new { }), "1"));
        await act.Should().ThrowAsync<ToolException>();
    }

    [Fact]
    public async Task ToolClient_Returns_ErrorResult_On2xx()
    {
        // D1: a tool that ran and returned an error *result* (HTTP 200) is returned, not raised.
        var json = """{"toolCallId":"1","content":"failed internally","isError":true}""";
        var client = new DefaultToolGatewayClient(Http(HttpStatusCode.OK, json), new RequestContext(), "a");
        var result = await client.InvokeAsync(new ToolCallRequest("t", JsonSerializer.SerializeToElement(new { }), "1"));
        result.IsError.Should().BeTrue();
        result.Content.Should().Be("failed internally");
    }
}
