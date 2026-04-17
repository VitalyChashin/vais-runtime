// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Polly;
using Vais2.Agents.Core;
using Vais2.Agents.Hosting.InMemory;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class M2ContractTests
{
    // -------------------------------------------------------------------------
    // IUsageSink + UsageRecord
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UsageSink_Receives_Record_Per_Successful_Turn()
    {
        var sink = new RecordingUsageSink();
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok", "model-x", 3, 4));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            UsageSink = sink,
            AgentName = "agent-7",
        });

        await agent.AskAsync("hi");

        sink.Records.Should().ContainSingle();
        var rec = sink.Records[0];
        rec.Succeeded.Should().BeTrue();
        rec.ProviderName.Should().Be("Fake");
        rec.ModelId.Should().Be("model-x");
        rec.PromptTokens.Should().Be(3);
        rec.CompletionTokens.Should().Be(4);
        rec.TotalTokens.Should().Be(7);
        rec.AgentName.Should().Be("agent-7");
        rec.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task UsageSink_Receives_Record_On_Failure()
    {
        var sink = new RecordingUsageSink();
        var provider = new FakeCompletionProvider(_ => throw new InvalidOperationException("boom"));
        var agent = new StatefulAiAgent(
            provider,
            new StatefulAgentOptions
            {
                UsageSink = sink,
                ResiliencePipeline = ResiliencePipeline.Empty, // don't retry in the test
            });

        Func<Task> act = async () => await agent.AskAsync("hi");
        await act.Should().ThrowAsync<InvalidOperationException>();

        sink.Records.Should().ContainSingle()
            .Which.Should().Match<UsageRecord>(r =>
                r.Succeeded == false &&
                r.ErrorType == "InvalidOperationException");
    }

    [Fact]
    public async Task UsageSink_Failure_Does_Not_Break_Turn()
    {
        var throwingSink = new ThrowingUsageSink();
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { UsageSink = throwingSink });

        var reply = await agent.AskAsync("hi");

        reply.Should().Be("ok");
    }

    // -------------------------------------------------------------------------
    // IAgentContextAccessor
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Context_From_Accessor_Flows_To_Usage_Record()
    {
        var sink = new RecordingUsageSink();
        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            UsageSink = sink,
            ContextAccessor = accessor,
        });

        using (accessor.Push(new AgentContext(UserId: "alice", TenantId: "acme", CorrelationId: "corr-1")))
        {
            await agent.AskAsync("hi");
        }

        var rec = sink.Records.Single();
        rec.UserId.Should().Be("alice");
        rec.TenantId.Should().Be("acme");
        rec.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public void AsyncLocal_Accessor_Restores_Previous_Scope_On_Dispose()
    {
        var accessor = new AsyncLocalAgentContextAccessor();
        accessor.Current.Should().Be(AgentContext.Empty);

        using (accessor.Push(new AgentContext(UserId: "a")))
        {
            accessor.Current.UserId.Should().Be("a");

            using (accessor.Push(new AgentContext(UserId: "b")))
            {
                accessor.Current.UserId.Should().Be("b");
            }

            accessor.Current.UserId.Should().Be("a");
        }

        accessor.Current.Should().Be(AgentContext.Empty);
    }

    // -------------------------------------------------------------------------
    // IAgentFilter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Filters_Run_In_Registration_Order_Around_Provider()
    {
        var log = new List<string>();
        var f1 = new TracingFilter("f1", log);
        var f2 = new TracingFilter("f2", log);

        var provider = new FakeCompletionProvider(_ =>
        {
            log.Add("provider");
            return new CompletionResponse("ok");
        });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Filters = new[] { (IAgentFilter)f1, f2 },
        });

        await agent.AskAsync("hi");

        log.Should().Equal(
            "f1:before",
            "f2:before",
            "provider",
            "f2:after",
            "f1:after");
    }

    [Fact]
    public async Task Filter_Can_Short_Circuit_Without_Calling_Provider()
    {
        var called = false;
        var provider = new FakeCompletionProvider(_ =>
        {
            called = true;
            return new CompletionResponse("should not reach");
        });

        var stub = new StubResponseFilter(new CompletionResponse("filtered-out"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { Filters = new[] { (IAgentFilter)stub } });

        var reply = await agent.AskAsync("hi");

        reply.Should().Be("filtered-out");
        called.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Resilience
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_Retries_Transient_Failures_And_Succeeds()
    {
        var attempts = 0;
        var provider = new FakeCompletionProvider(_ =>
        {
            attempts++;
            if (attempts < 2)
            {
                throw new InvalidOperationException("transient");
            }

            return new CompletionResponse("ok");
        });

        var quickRetry = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
            })
            .Build();

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { ResiliencePipeline = quickRetry });
        var reply = await agent.AskAsync("hi");

        reply.Should().Be("ok");
        attempts.Should().Be(2);
    }

    // -------------------------------------------------------------------------
    // InMemoryAgentRuntime
    // -------------------------------------------------------------------------

    [Fact]
    public void InMemoryRuntime_Returns_Same_Instance_For_Same_Id()
    {
        var provider = new FakeCompletionProvider();
        var runtime = new InMemoryAgentRuntime(provider);

        var a1 = runtime.GetOrCreate("agent-a");
        var a2 = runtime.GetOrCreate("agent-a");
        var b = runtime.GetOrCreate("agent-b");

        a1.Should().BeSameAs(a2);
        a1.Should().NotBeSameAs(b);
    }

    [Fact]
    public void InMemoryRuntime_TryGet_And_Remove_Work()
    {
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider());

        runtime.TryGet("a", out _).Should().BeFalse();
        var created = runtime.GetOrCreate("a");
        runtime.TryGet("a", out var fetched).Should().BeTrue();
        fetched.Should().BeSameAs(created);

        runtime.Remove("a").Should().BeTrue();
        runtime.Remove("a").Should().BeFalse();
        runtime.TryGet("a", out _).Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryRuntime_Distinct_Agents_Keep_Separate_History()
    {
        var provider = new FakeCompletionProvider(req => new CompletionResponse($"reply-{req.History.Count}"));
        var runtime = new InMemoryAgentRuntime(provider);

        var alice = runtime.GetOrCreate("alice");
        var bob = runtime.GetOrCreate("bob");

        await alice.AskAsync("hi from alice");
        await alice.AskAsync("another from alice");
        await bob.AskAsync("hi from bob");

        alice.History.Should().HaveCount(4);
        bob.History.Should().HaveCount(2);
    }

    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

    private sealed class RecordingUsageSink : IUsageSink
    {
        public List<UsageRecord> Records { get; } = new();

        public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return default;
        }
    }

    private sealed class ThrowingUsageSink : IUsageSink
    {
        public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("sink is broken");
    }

    private sealed class TracingFilter(string name, List<string> log) : IAgentFilter
    {
        public async Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            log.Add($"{name}:before");
            var response = await next(request, cancellationToken).ConfigureAwait(false);
            log.Add($"{name}:after");
            return response;
        }
    }

    private sealed class StubResponseFilter(CompletionResponse response) : IAgentFilter
    {
        public Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
