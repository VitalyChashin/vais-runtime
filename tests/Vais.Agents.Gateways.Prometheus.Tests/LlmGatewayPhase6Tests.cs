// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Prometheus;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Gateways.Prometheus.Tests;

/// <summary>
/// GW-27 — <see cref="LlmPrometheusMiddleware"/> metric emission tests.
/// Each test creates an isolated <see cref="CollectorRegistry"/> to avoid
/// global state pollution between test runs.
/// </summary>
public sealed class LlmGatewayPhase6Tests
{
    // ── GW-27: metric tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Successful_Call_Increments_RequestsTotal_Success()
    {
        var (middleware, factory) = BuildMiddleware();

        await LlmGatewayPipeline.InvokeAsync(
            BuildRequest(),
            new FakeProvider(_ => new CompletionResponse("ok", "gpt-4o")),
            [middleware]);

        var counter = factory.CreateCounter("llm_requests_total", "",
            new CounterConfiguration { LabelNames = ["model", "workspace", "status"] });

        counter.WithLabels("gpt-4o", "_default", "success").Value.Should().Be(1);
        counter.WithLabels("gpt-4o", "_default", "error").Value.Should().Be(0);
    }

    [Fact]
    public async Task Provider_Exception_Increments_RequestsTotal_Error()
    {
        var (middleware, factory) = BuildMiddleware();

        var act = () => LlmGatewayPipeline.InvokeAsync(
            BuildRequest(),
            new FakeProvider(_ => throw new InvalidOperationException("provider failed")),
            [middleware]);

        await act.Should().ThrowAsync<InvalidOperationException>();

        var counter = factory.CreateCounter("llm_requests_total", "",
            new CounterConfiguration { LabelNames = ["model", "workspace", "status"] });

        counter.WithLabels("", "_default", "error").Value.Should().Be(1);
        counter.WithLabels("", "_default", "success").Value.Should().Be(0);
    }

    [Fact]
    public async Task Token_Counts_Are_Recorded_On_TokensTotal()
    {
        var (middleware, factory) = BuildMiddleware();

        await LlmGatewayPipeline.InvokeAsync(
            BuildRequest(),
            new FakeProvider(_ => new CompletionResponse("ok", "gpt-4o",
                PromptTokens: 100, CompletionTokens: 50)),
            [middleware]);

        var counter = factory.CreateCounter("llm_tokens_total", "",
            new CounterConfiguration { LabelNames = ["model", "workspace", "type"] });

        counter.WithLabels("gpt-4o", "_default", "prompt").Value.Should().Be(100);
        counter.WithLabels("gpt-4o", "_default", "completion").Value.Should().Be(50);
    }

    [Fact]
    public async Task Workspace_Label_Uses_AgentContext_WorkspaceId()
    {
        var accessor = new AsyncLocalAgentContextAccessor();
        var registry = Metrics.NewCustomRegistry();
        var factory = Metrics.WithCustomRegistry(registry);
        var middleware = new LlmPrometheusMiddleware(accessor, factory);

        using var _ = accessor.Push(new AgentContext { WorkspaceId = "ws-acme" });

        await LlmGatewayPipeline.InvokeAsync(
            BuildRequest(),
            new FakeProvider(_ => new CompletionResponse("ok", "gpt-4o")),
            [middleware]);

        var counter = factory.CreateCounter("llm_requests_total", "",
            new CounterConfiguration { LabelNames = ["model", "workspace", "status"] });

        counter.WithLabels("gpt-4o", "ws-acme", "success").Value.Should().Be(1);
        counter.WithLabels("gpt-4o", "_default", "success").Value.Should().Be(0);
    }

    [Fact]
    public async Task RequestDuration_Histogram_Is_Observed()
    {
        var (middleware, factory) = BuildMiddleware();

        await LlmGatewayPipeline.InvokeAsync(
            BuildRequest(),
            new FakeProvider(_ => new CompletionResponse("ok", "model-a")),
            [middleware]);

        var histogram = factory.CreateHistogram("llm_request_duration_seconds", "",
            new HistogramConfiguration { LabelNames = ["model", "workspace"] });

        histogram.WithLabels("model-a", "_default").Count.Should().Be(1);
        histogram.WithLabels("model-a", "_default").Sum.Should().BeGreaterThan(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (LlmPrometheusMiddleware middleware, MetricFactory factory) BuildMiddleware()
    {
        var accessor = new AsyncLocalAgentContextAccessor();
        var registry = Metrics.NewCustomRegistry();
        var factory = Metrics.WithCustomRegistry(registry);
        return (new LlmPrometheusMiddleware(accessor, factory), factory);
    }

    private static CompletionRequest BuildRequest()
        => new([new ChatTurn(AgentChatRole.User, "hello")]);

    private sealed class FakeProvider : ICompletionProvider
    {
        private readonly Func<CompletionRequest, CompletionResponse> _respond;

        internal FakeProvider(Func<CompletionRequest, CompletionResponse> respond) => _respond = respond;

        public string ProviderName => "Fake";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_respond(request));
    }
}
