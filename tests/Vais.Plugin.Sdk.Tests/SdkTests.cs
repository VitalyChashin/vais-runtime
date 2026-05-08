// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;
using Vais.Plugin.Sdk;
using Xunit;

namespace Vais.Plugin.Sdk.Tests;

// ---------------------------------------------------------------------------
// Minimal test agents
// ---------------------------------------------------------------------------

/// <summary>Echoes the last user message as the assistant reply.</summary>
internal sealed class EchoAgent : ContainerPluginAgent
{
    public override Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default)
    {
        var last = request.Messages.LastOrDefault(m => m.Role == AgentChatRole.User);
        return Task.FromResult(new InvokeResponse { AssistantMessage = last?.Text ?? "?" });
    }
}

/// <summary>Always throws <see cref="OpaqueStateDeserializationException"/> to test the 422 path.</summary>
internal sealed class BrokenStateAgent : ContainerPluginAgent
{
    public override Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default)
    {
        throw new OpaqueStateDeserializationException("state schema mismatch", new Exception("inner"));
    }
}

/// <summary>Yields two deltas then done.</summary>
internal sealed class StreamingEchoAgent : ContainerPluginAgent
{
    public override Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InvokeResponse { AssistantMessage = "streamed" });

    public override async IAsyncEnumerable<SseEvent> StreamAsync(InvokeRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new SseEvent("delta", new DeltaPayload("hello "));
        yield return new SseEvent("delta", new DeltaPayload("world"));
        yield return new SseEvent("done", new InvokeResponse { AssistantMessage = "hello world" });
        await Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Test request helpers
// ---------------------------------------------------------------------------

internal static class Requests
{
    internal static object Basic(string userText = "hello") => new
    {
        agentId = "test-agent",
        sessionId = "00000000-0000-0000-0000-000000000001",
        messages = new[] { new { role = "user", content = userText } },
        llmGatewayUrl = "http://mock/v1/llm",
        toolGatewayUrl = "http://mock/v1/tools",
        timeoutSeconds = 30,
        context = new { callToken = "test-token" },
    };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class HealthTests : IAsyncLifetime
{
    private SdkTestHarness<EchoAgent> _harness = null!;

    public async Task InitializeAsync() => _harness = await SdkTestHarness<EchoAgent>.CreateAsync();
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task GetHealth_Returns200_WithReadyStatus()
    {
        var resp = await _harness.Client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ready");
    }
}

public sealed class MetadataTests : IAsyncLifetime
{
    private SdkTestHarness<EchoAgent> _harness = null!;

    public async Task InitializeAsync() => _harness = await SdkTestHarness<EchoAgent>.CreateAsync();
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task GetMetadata_Returns_TargetApiVersion()
    {
        var resp = await _harness.Client.GetAsync("/v1/metadata");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("targetApiVersion").GetString().Should().Be("0.24");
        body.GetProperty("handlerTypeName").GetString().Should().Contain(nameof(EchoAgent));
        body.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()).Should().Contain("stream");
    }
}

public sealed class InvokeTests : IAsyncLifetime
{
    private SdkTestHarness<EchoAgent> _harness = null!;

    public async Task InitializeAsync() => _harness = await SdkTestHarness<EchoAgent>.CreateAsync();
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task PostInvoke_EchoesUserMessage()
    {
        var resp = await _harness.Client.PostAsJsonAsync("/v1/invoke", Requests.Basic("hello from test"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<InvokeResponse>();
        body.Should().NotBeNull();
        body!.AssistantMessage.Should().Be("hello from test");
    }

    [Fact]
    public async Task PostInvoke_Returns422_OnOpaqueStateError()
    {
        await using var harness = await SdkTestHarness<BrokenStateAgent>.CreateAsync();
        var resp = await harness.Client.PostAsJsonAsync("/v1/invoke", Requests.Basic());
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorType").GetString().Should().Be("OpaqueStateDeserializationError");
    }

    [Fact]
    public async Task PostInvoke_UsesInjectedLlmGatewayClient()
    {
        var mockLlm = new StubLlmGatewayClient("from-mock");
        await using var harness = await SdkTestHarness<LlmCallingAgent>.CreateAsync(
            s => s.AddSingleton<ILlmGatewayClient>(mockLlm));
        var resp = await harness.Client.PostAsJsonAsync("/v1/invoke", Requests.Basic("ping"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<InvokeResponse>();
        body!.AssistantMessage.Should().Be("from-mock");
    }
}

public sealed class StreamTests : IAsyncLifetime
{
    private SdkTestHarness<StreamingEchoAgent> _harness = null!;

    public async Task InitializeAsync() => _harness = await SdkTestHarness<StreamingEchoAgent>.CreateAsync();
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task PostStream_EmitsDeltasThenDone()
    {
        var resp = await _harness.Client.PostAsJsonAsync("/v1/stream", Requests.Basic());
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var rawSse = await resp.Content.ReadAsStringAsync();
        var events = ParseSseEvents(rawSse);
        events.Should().HaveCount(3);
        events[0].EventType.Should().Be("delta");
        events[1].EventType.Should().Be("delta");
        events[2].EventType.Should().Be("done");
    }

    [Fact]
    public async Task PostStream_DefaultImpl_EmitsSingleDoneEvent()
    {
        await using var harness = await SdkTestHarness<EchoAgent>.CreateAsync();
        var resp = await harness.Client.PostAsJsonAsync("/v1/stream", Requests.Basic("hi"));
        var rawSse = await resp.Content.ReadAsStringAsync();
        var events = ParseSseEvents(rawSse);
        events.Should().ContainSingle(e => e.EventType == "done");
    }

    private static List<(string EventType, string Data)> ParseSseEvents(string raw)
    {
        var events = new List<(string, string)>();
        string? currentEvent = null;
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("event: ")) currentEvent = line["event: ".Length..].Trim();
            else if (line.StartsWith("data: ") && currentEvent is not null)
            {
                events.Add((currentEvent, line["data: ".Length..].Trim()));
                currentEvent = null;
            }
        }
        return events;
    }
}

// ---------------------------------------------------------------------------
// Stub helpers used by InvokeTests
// ---------------------------------------------------------------------------

internal sealed class LlmCallingAgent : ContainerPluginAgent
{
    public override async Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default)
    {
        var response = await request.Llm.CompleteAsync(request.Messages, cancellationToken: cancellationToken);
        return new InvokeResponse { AssistantMessage = response.Content ?? "" };
    }
}

internal sealed class StubLlmGatewayClient : ILlmGatewayClient
{
    private readonly string _content;
    public StubLlmGatewayClient(string content) => _content = content;

    public Task<LlmResponse> CompleteAsync(IReadOnlyList<ChatTurn> messages, CompletionOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new LlmResponse { Content = _content });

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages, CompletionOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return _content;
        await Task.CompletedTask;
    }
}
