// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Part 2 (run-health) RH-8 / RH-9 — verifies that <see cref="ContainerAgentShim"/> spans carry
/// the correct Langfuse observation level tags and that spans nest under an ambient parent.
/// </summary>
public sealed class ContainerAgentShimObservabilityTests
{
    private static readonly JsonSerializerOptions s_webOpts = new(JsonSerializerDefaults.Web);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpResponseMessage OkPartialInvoke(string msg, string? reason = null) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new { assistantMessage = msg, isPartial = true, failureReason = reason },
                options: s_webOpts)
        };

    private static HttpResponseMessage OkInvoke(string msg) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { assistantMessage = msg }, options: s_webOpts)
        };

    private static HttpResponseMessage SseEvents(params string[] lines)
    {
        var body = string.Join("", lines);
        var content = new StreamContent(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> InvokeHandler { get; set; }
            = _ => Task.FromResult(OkInvoke("default"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery is "/health" or "/health/")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return InvokeHandler(req);
        }
    }

    private static AgentManifest MakeManifest(string id = "obs-agent")
        => new(id, "1.0", new AgentHandlerRef("Test"), [], []);

    private static (ContainerAgentShim Shim, FakeHttpHandler Handler) MakeShim(string agentId = "obs-agent")
    {
        var handler = new FakeHttpHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns("tok");
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns("tok");

        var shim = new ContainerAgentShim(
            supervisor: null!, invokeClient: httpClient, preprocessors: [],
            manifest: MakeManifest(agentId), callTokenService: tokenSvc,
            internalLlmGatewayUrl: "http://gw/llm", internalToolGatewayUrl: "http://gw/tools",
            invokeTimeoutSeconds: 30, sessionConfig: null, invokeIdleTimeoutSeconds: null,
            contextAccessor: null, translator: null, logger: NullLogger.Instance);

        return (shim, handler);
    }

    private static async Task<List<AgentEvent>> CollectAsync(ContainerAgentShim shim)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in shim.StreamAsync("hi", AgentContext.Empty, CancellationToken.None))
            events.Add(e);
        return events;
    }

    private static IDisposable ListenTo(string source, List<Activity> sink)
    {
        var l = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => sink.Add(a),
        };
        ActivitySource.AddActivityListener(l);
        return l;
    }

    // ── RH-8: AskAsync partial sets WARNING ───────────────────────────────────

    [Fact]
    public async Task AskAsync_PartialResponse_SetsWarningLevelTag()
    {
        var agentId = $"partial-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenTo("Vais.Agents.Runtime.Plugins.Container", activities);

        var (shim, handler) = MakeShim(agentId);
        handler.InvokeHandler = _ => Task.FromResult(OkPartialInvoke("partial result", "upstream cap"));

        await shim.AskAsync("test");

        var ask = activities
            .Where(a => a.OperationName == "container.agent.ask" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        ask.GetTagItem("langfuse.observation.level").Should().Be("WARNING");
        ask.GetTagItem("vais.turn.partial").Should().Be(true);
        ask.GetTagItem("langfuse.observation.status_message").Should().Be("upstream cap");
    }

    // ── RH-8: StreamAsync error sets span status ERROR ────────────────────────

    [Fact]
    public async Task StreamAsync_CollectionError_SetsSpanStatusError()
    {
        var agentId = $"err-stream-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenTo("Vais.Agents.Runtime.Plugins.Container", activities);

        var (shim, handler) = MakeShim(agentId);
        handler.InvokeHandler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"errorType\":\"ServerError\",\"errorMessage\":\"boom\"}")
        });

        var events = await CollectAsync(shim);
        events.Should().Contain(e => e is TurnFailed);

        var stream = activities
            .Where(a => a.OperationName == "container.agent.stream" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        stream.Status.Should().Be(ActivityStatusCode.Error);
    }

    // ── RH-8: StreamAsync partial sets WARNING on TurnCompleted ───────────────

    [Fact]
    public async Task StreamAsync_PartialFinalResponse_SetsWarningAndTurnCompletedLevel()
    {
        var agentId = $"partial-stream-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenTo("Vais.Agents.Runtime.Plugins.Container", activities);

        var finalJson = JsonSerializer.Serialize(
            new { assistantMessage = "partial text", isPartial = true, failureReason = "cap hit" },
            s_webOpts);
        var (shim, handler) = MakeShim(agentId);
        handler.InvokeHandler = _ => Task.FromResult(SseEvents(
            "event: delta\ndata: {\"text\":\"partial \"}\n\n",
            $"event: done\ndata: {finalJson}\n\n"));

        var events = await CollectAsync(shim);

        var completed = events.OfType<TurnCompleted>().Should().ContainSingle().Subject;
        completed.Level.Should().Be(FailureLevel.Warning);

        var stream = activities
            .Where(a => a.OperationName == "container.agent.stream" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        stream.GetTagItem("langfuse.observation.level").Should().Be("WARNING");
        stream.GetTagItem("vais.turn.partial").Should().Be(true);
    }

    // ── RH-9: span nesting — child span inherits parent trace id ─────────────

    [Fact]
    public async Task AskAsync_WithAmbientParent_NestsSpanUnderParent()
    {
        var agentId = $"nest-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenTo("Vais.Agents.Runtime.Plugins.Container", activities);

        using var parentSource = new ActivitySource("test.container.parent");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.container.parent",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(parentListener);

        using var parent = parentSource.StartActivity("parent.span");
        var parentTraceId = parent!.TraceId;

        var (shim, _) = MakeShim(agentId);
        await shim.AskAsync("hello");

        var ask = activities
            .Where(a => a.OperationName == "container.agent.ask" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        ask.TraceId.Should().Be(parentTraceId);
        ask.ParentId.Should().Be(parent.Id);
    }
}
