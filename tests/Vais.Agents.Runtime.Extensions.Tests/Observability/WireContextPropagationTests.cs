// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Vais.Agents.Runtime.Extensions.Container;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Tests.Observability;

/// <summary>
/// Verifies that <see cref="HttpContainerHandlerProxy"/> injects W3C trace context and
/// <c>X-Vais-*</c> identity headers on every <c>/pre</c> and <c>/post</c> HTTP call
/// (EXO-8 of the extensions observability plan).
/// </summary>
public sealed class WireContextPropagationTests : IDisposable
{
    private readonly ActivityListener _listener;

    public WireContextPropagationTests()
    {
        // Register a listener for the extension telemetry source so the invocation
        // span created by ExtensionInvocationInstrumentation is active when the
        // proxy makes its HTTP calls.
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ExtensionTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // ── 1. Pre call carries traceparent + X-Vais-* headers ───────────────────

    [Fact]
    public async Task InvokeInput_Pre_Has_TraceContext_And_VaisHeaders()
    {
        var capture = new RequestCapturingHandler();
        var proxy = BuildProxy(capture, "ext-w1", "1.0.0", "h1", "agentInput");

        var ctx = new AgentInputContext { AgentId = "agent-w1", RunId = "run-w1", NodeId = "node-1", Message = "hi" };
        await proxy.InvokeInputAsync(ctx, () => Task.CompletedTask, CancellationToken.None);

        var pre = capture.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/pre"));

        pre.Headers.Contains("traceparent").Should().BeTrue("invocation span must propagate traceparent");
        GetSingle(pre, "X-Vais-Agent-Id").Should().Be("agent-w1");
        GetSingle(pre, "X-Vais-Run-Id").Should().Be("run-w1");
        GetSingle(pre, "X-Vais-Node-Id").Should().Be("node-1");
    }

    // ── 2. Post call also carries traceparent + X-Vais-* headers ─────────────

    [Fact]
    public async Task InvokeInput_Post_Has_TraceContext_And_VaisHeaders()
    {
        var capture = new RequestCapturingHandler();
        var proxy = BuildProxy(capture, "ext-w2", "1.0.0", "h2", "agentInput");

        var ctx = new AgentInputContext { AgentId = "agent-w2", RunId = "run-w2", Message = "hi" };
        await proxy.InvokeInputAsync(ctx, () => Task.CompletedTask, CancellationToken.None);

        var post = capture.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/post"));

        post.Headers.Contains("traceparent").Should().BeTrue();
        GetSingle(post, "X-Vais-Agent-Id").Should().Be("agent-w2");
        GetSingle(post, "X-Vais-Run-Id").Should().Be("run-w2");
    }

    // ── 3. traceparent contains the parent trace-id when a span is active ─────

    [Fact]
    public async Task TraceParent_Inherits_Parent_TraceId()
    {
        // Register a listener for an ad-hoc source so the parent activity is recorded.
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "vais-test-parent",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(parentListener);

        var capture = new RequestCapturingHandler();
        var proxy = BuildProxy(capture, "ext-w3", "1.0.0", "h3", "agentInput");

        using var parent = new ActivitySource("vais-test-parent").StartActivity("parent-turn");
        var expectedTraceId = parent!.TraceId.ToString();

        var ctx = new AgentInputContext { AgentId = "agent-w3", RunId = "run-w3", Message = "hi" };
        await proxy.InvokeInputAsync(ctx, () => Task.CompletedTask, CancellationToken.None);

        var pre = capture.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/pre"));
        var traceparent = GetSingle(pre, "traceparent");

        // W3C format: 00-<traceId>-<spanId>-<flags>
        traceparent.Should().Contain(expectedTraceId,
            "invocation span inherits the parent trace-id");
    }

    // ── 4. Null RunId omits X-Vais-Run-Id; null NodeId omits X-Vais-Node-Id ──

    [Fact]
    public async Task Null_RunId_And_NodeId_Omit_Their_Headers()
    {
        var capture = new RequestCapturingHandler();
        var proxy = BuildProxy(capture, "ext-w4", "1.0.0", "h4", "agentInput");

        var ctx = new AgentInputContext { AgentId = "agent-w4", RunId = null, NodeId = null, Message = "hi" };
        await proxy.InvokeInputAsync(ctx, () => Task.CompletedTask, CancellationToken.None);

        var pre = capture.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/pre"));

        GetSingle(pre, "X-Vais-Agent-Id").Should().Be("agent-w4");
        pre.Headers.Contains("X-Vais-Run-Id").Should().BeFalse("null RunId must not produce header");
        pre.Headers.Contains("X-Vais-Node-Id").Should().BeFalse("null NodeId must not produce header");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpContainerHandlerProxy BuildProxy(
        HttpMessageHandler handler,
        string extId, string version, string handlerId, string seam)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test-ext/") };
        var descriptor = new HandlerBindingDescriptor(extId, version, handlerId, seam, "container");
        return new HttpContainerHandlerProxy(
            http,
            preEndpoint: $"/handlers/{handlerId}/pre",
            postEndpoint: $"/handlers/{handlerId}/post",
            failureMode: "log",
            descriptor: descriptor);
    }

    private static string GetSingle(HttpRequestMessage msg, string header)
    {
        msg.Headers.TryGetValues(header, out var values).Should().BeTrue($"header '{header}' must be present");
        return values!.Should().ContainSingle().Subject;
    }

    /// <summary>
    /// Returns a canned next/next JSON response for pre and an empty success for post.
    /// Stores all requests so tests can inspect them.
    /// </summary>
    private sealed class RequestCapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var isPost = request.RequestUri!.AbsolutePath.EndsWith("/post");
            var json = isPost
                ? """{"action":"passThrough","contextPatch":null}"""
                : """{"action":"next","continuationToken":null,"contextPatch":null}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
