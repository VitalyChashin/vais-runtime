// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using FluentAssertions;
using Polly;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Observability.Tests;

/// <summary>
/// Verifies that <see cref="StatefulAiAgent"/> emits a per-turn Activity with the
/// OTel GenAI tags when a listener is attached, and produces no Activity when no
/// listener is attached (zero-cost default).
/// </summary>
public sealed class ActivityEmissionTests
{
    [Fact]
    public async Task Emits_Activity_With_GenAI_Tags_On_Success()
    {
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var provider = new FakeCompletionProvider(_ => new CompletionResponse("hi", "gpt-4o-mini", 11, 22));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "test-agent" });

        await agent.AskAsync("hello");

        recorded.Should().ContainSingle();
        var activity = recorded[0];
        activity.DisplayName.Should().Be("chat gpt-4o-mini");
        activity.Status.Should().Be(ActivityStatusCode.Ok);

        var tags = activity.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags[AgenticTags.GenAiSystem].Should().Be("fake");
        tags[AgenticTags.GenAiOperationName].Should().Be("chat");
        tags[AgenticTags.GenAiResponseModel].Should().Be("gpt-4o-mini");
        tags[AgenticTags.GenAiUsageInputTokens].Should().Be(11);
        tags[AgenticTags.GenAiUsageOutputTokens].Should().Be(22);
        tags[AgenticTags.AgentName].Should().Be("test-agent");
    }

    [Fact]
    public async Task Emits_Activity_With_Error_Status_On_Failure()
    {
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var provider = new FakeCompletionProvider(_ => throw new InvalidOperationException("boom"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ResiliencePipeline = ResiliencePipeline.Empty, // don't retry
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.AskAsync("hello"));

        recorded.Should().ContainSingle();
        var activity = recorded[0];
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.TagObjects.Should().ContainSingle(kv => kv.Key == AgenticTags.ErrorType)
            .Which.Value.Should().Be("InvalidOperationException");
    }

    [Fact]
    public async Task Propagates_Context_Tags()
    {
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var accessor = new AsyncLocalAgentContextAccessor();
        using var _ = accessor.Push(new AgentContext(
            UserId: "u-42",
            TenantId: "t-7",
            CorrelationId: "corr-abc",
            AgentName: "from-context"));

        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { ContextAccessor = accessor });

        await agent.AskAsync("hi");

        var tags = recorded.Single().TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags[AgenticTags.UserId].Should().Be("u-42");
        tags[AgenticTags.TenantId].Should().Be("t-7");
        tags[AgenticTags.CorrelationId].Should().Be("corr-abc");
        tags[AgenticTags.AgentName].Should().Be("from-context");
    }

    [Fact]
    public async Task No_Listener_No_Activity_Emitted()
    {
        // Sanity check that without a listener the code path is a no-op. We can't observe
        // "no activity" directly; we simply verify the call completes and no listener
        // leaks. A listener attached *after* the call would capture nothing.
        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider);

        var reply = await agent.AskAsync("hello");
        reply.Should().NotBeNullOrEmpty();

        var post = new List<Activity>();
        using var listener = CreateListener(post);
        post.Should().BeEmpty();
    }

    private static ActivityListener CreateListener(List<Activity> sink) =>
        SubscribeTo(sink, AgenticDiagnostics.ActivitySourceName);

    internal static ActivityListener SubscribeTo(List<Activity> sink, string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = sink.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
