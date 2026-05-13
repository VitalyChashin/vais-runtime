// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

public sealed class HttpAgentRemoteInvokerTests
{
    // ─── helpers ──────────────────────────────────────────────────────────

    private static HttpAgentRemoteInvoker BuildInvoker(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string baseUrl = "https://runtime-b.svc")
    {
        var stub = new StubHttpMessageHandler(handler);
        var client = new HttpClient(stub) { BaseAddress = new Uri(baseUrl) };
        return new HttpAgentRemoteInvoker(httpClientOverride: client);
    }

    private static AgentInvocationResult OkResult(string text = "pong") =>
        new(text, "sess-1");

    private static HttpResponseMessage JsonOk(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };

    private static HttpResponseMessage StatusResponse(HttpStatusCode code) =>
        new(code) { Content = new StringContent(code.ToString()) };

    // ─── success ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_SuccessResponse_ReturnsResult()
    {
        var invoker = BuildInvoker(_ => JsonOk(OkResult("hello from remote")));

        var result = await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        result.Text.Should().Be("hello from remote");
    }

    [Fact]
    public async Task InvokeAsync_ForwardsBearerToken()
    {
        string? capturedAuth = null;
        var invoker = BuildInvoker(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return JsonOk(OkResult());
        });

        await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: "tok-abc");

        capturedAuth.Should().Be("Bearer tok-abc");
    }

    [Fact]
    public async Task InvokeAsync_NullBearerToken_NoAuthHeader()
    {
        string? capturedAuth = null;
        var invoker = BuildInvoker(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return JsonOk(OkResult());
        });

        await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        capturedAuth.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_BuildsCorrectPath_WithVersion()
    {
        string? capturedPath = null;
        var invoker = BuildInvoker(req =>
        {
            capturedPath = req.RequestUri?.PathAndQuery;
            return JsonOk(OkResult());
        });

        await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "2.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        capturedPath.Should().Contain("/v1/agents/agent-1/invoke");
        capturedPath.Should().Contain("version=2.0");
    }

    [Fact]
    public async Task InvokeAsync_BuildsCorrectPath_NoVersion()
    {
        string? capturedPath = null;
        var invoker = BuildInvoker(req =>
        {
            capturedPath = req.RequestUri?.PathAndQuery;
            return JsonOk(OkResult());
        });

        await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", string.Empty),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        capturedPath.Should().Be("/v1/agents/agent-1/invoke");
    }

    // ─── failure / retry ──────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_404_ThrowsNonRetryableException()
    {
        var invoker = BuildInvoker(_ => StatusResponse(HttpStatusCode.NotFound));

        var act = async () => await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        var ex = await act.Should().ThrowAsync<RemoteAgentInvocationException>();
        ex.Which.Status.Should().Be(HttpStatusCode.NotFound);
        ex.Which.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_503_ExhaustsRetries_Throws()
    {
        var callCount = 0;
        var invoker = BuildInvoker(_ =>
        {
            callCount++;
            return StatusResponse(HttpStatusCode.ServiceUnavailable);
        });

        var act = async () => await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        var ex = await act.Should().ThrowAsync<RemoteAgentInvocationException>();
        ex.Which.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        ex.Which.IsRetryable.Should().BeTrue();
        callCount.Should().Be(3); // 1 initial + 2 retries
    }

    [Fact]
    public async Task InvokeAsync_503_ThenSuccess_ReturnsResult()
    {
        var callCount = 0;
        var invoker = BuildInvoker(_ =>
        {
            callCount++;
            if (callCount < 2) return StatusResponse(HttpStatusCode.ServiceUnavailable);
            return JsonOk(OkResult("recovered"));
        });

        var result = await invoker.InvokeAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        result.Text.Should().Be("recovered");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task InvokeAsync_ThrowsArgumentException_OnEmptyRuntimeUrl()
    {
        var invoker = BuildInvoker(_ => JsonOk(OkResult()));

        var act = async () => await invoker.InvokeAsync(
            runtimeUrl: "",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ─── StreamAsync ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    private static HttpResponseMessage SseResponse(IEnumerable<(string EventName, string DataJson)> frames)
    {
        var sb = new StringBuilder();
        foreach (var (name, data) in frames)
        {
            sb.Append("event: ").Append(name).Append('\n');
            sb.Append("data: ").Append(data).Append('\n');
            sb.Append('\n');
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    [Fact]
    public async Task StreamAsync_YieldsFullEventTaxonomy()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ctx = new AgentContext();
        var frames = new[]
        {
            ("turn.started",    JsonSerializer.Serialize(new TurnStarted(at, ctx, "hi"), SseJsonOptions)),
            ("delta",           JsonSerializer.Serialize(new CompletionDelta(at, ctx, "hello"), SseJsonOptions)),
            ("turn.completed",  JsonSerializer.Serialize(new TurnCompleted(at, ctx, "hello", null, null, null, TimeSpan.Zero), SseJsonOptions)),
        };
        var invoker = BuildInvoker(_ => SseResponse(frames));
        var events = new List<AgentEvent>();

        await foreach (var e in invoker.StreamAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null))
        {
            events.Add(e);
        }

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<TurnStarted>();
        events[1].Should().BeOfType<CompletionDelta>().Which.TextDelta.Should().Be("hello");
        events[2].Should().BeOfType<TurnCompleted>();
    }

    [Fact]
    public async Task StreamAsync_ForwardsBearerToken()
    {
        string? capturedAuth = null;
        var invoker = BuildInvoker(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return SseResponse([]);
        });

        await foreach (var _ in invoker.StreamAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: "stream-tok"))
        { }

        capturedAuth.Should().Be("Bearer stream-tok");
    }

    [Fact]
    public async Task StreamAsync_NullBearerToken_NoAuthHeader()
    {
        string? capturedAuth = null;
        var invoker = BuildInvoker(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return SseResponse([]);
        });

        await foreach (var _ in invoker.StreamAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null))
        { }

        capturedAuth.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_BuildsCorrectPath_WithVersion()
    {
        string? capturedPath = null;
        var invoker = BuildInvoker(req =>
        {
            capturedPath = req.RequestUri?.PathAndQuery;
            return SseResponse([]);
        });

        await foreach (var _ in invoker.StreamAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "2.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null))
        { }

        capturedPath.Should().Be("/v1/agents/agent-1/invoke/stream?version=2.0");
    }

    [Fact]
    public async Task StreamAsync_BuildsCorrectPath_NoVersion()
    {
        string? capturedPath = null;
        var invoker = BuildInvoker(req =>
        {
            capturedPath = req.RequestUri?.PathAndQuery;
            return SseResponse([]);
        });

        await foreach (var _ in invoker.StreamAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", string.Empty),
            new AgentInvocationRequest("hi"),
            bearerToken: null))
        { }

        capturedPath.Should().Be("/v1/agents/agent-1/invoke/stream");
    }

    [Fact]
    public async Task StreamAsync_501_ThrowsRemoteAgentInvocationException()
    {
        var invoker = BuildInvoker(_ => new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        var act = async () =>
        {
            await foreach (var _ in invoker.StreamAsync(
                "https://runtime-b.svc",
                new AgentHandle("agent-1", "1.0"),
                new AgentInvocationRequest("hi"),
                bearerToken: null))
            { }
        };

        var ex = await act.Should().ThrowAsync<RemoteAgentInvocationException>();
        ex.Which.Status.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task StreamAsync_UnknownEventNames_Skipped()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ctx = new AgentContext();
        var frames = new[]
        {
            ("future.event",   "{}"),
            ("turn.completed", JsonSerializer.Serialize(new TurnCompleted(at, ctx, "ok", null, null, null, TimeSpan.Zero), SseJsonOptions)),
        };
        var invoker = BuildInvoker(_ => SseResponse(frames));
        var events = new List<AgentEvent>();

        await foreach (var e in invoker.StreamAsync(
            "https://runtime-b.svc",
            new AgentHandle("agent-1", "1.0"),
            new AgentInvocationRequest("hi"),
            bearerToken: null))
        {
            events.Add(e);
        }

        events.Should().ContainSingle().Which.Should().BeOfType<TurnCompleted>();
    }
}

// ─── test infrastructure ──────────────────────────────────────────────────────

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_handler(request));
}
