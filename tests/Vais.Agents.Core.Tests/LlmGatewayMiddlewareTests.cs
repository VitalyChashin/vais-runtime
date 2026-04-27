// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class LlmGatewayMiddlewareTests
{
    // ── GW-1: non-streaming paths ────────────────────────────────────────────

    [Fact]
    public async Task PassThrough_Middleware_Reaches_Provider()
    {
        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new PassThroughMiddleware()],
        });

        await agent.AskAsync("hello");

        provider.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task ShortCircuit_NonStreaming_Provider_Is_Never_Called()
    {
        var provider = new FakeCompletionProvider();
        var shortCircuit = new ShortCircuitMiddleware(new CompletionResponse("synthetic"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [shortCircuit],
        });

        var result = await agent.AskAsync("hello");

        result.Should().Be("synthetic");
        provider.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_Mutation_Provider_Receives_Modified_Request()
    {
        CompletionRequest? received = null;
        var provider = new FakeCompletionProvider(r => { received = r; return new CompletionResponse("ok"); });
        var mutator = new RequestMutatingMiddleware("MUTATED");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [mutator],
        });

        await agent.AskAsync("hello");

        received.Should().NotBeNull();
        received!.SystemPrompt.Should().Be("MUTATED");
    }

    [Fact]
    public async Task Response_Mutation_Caller_Receives_Modified_Response()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("original"));
        var mutator = new ResponseMutatingMiddleware("modified");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [mutator],
        });

        var result = await agent.AskAsync("hello");

        result.Should().Be("modified");
    }

    // ── GW-1: streaming paths ────────────────────────────────────────────────

    [Fact]
    public async Task PassThrough_Streaming_Middleware_Deltas_Flow_Unchanged()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("a"),
            new CompletionUpdate("b"),
        });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new PassThroughMiddleware()],
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) deltas.Add(d);

        deltas.Should().Equal("a", "b");
        provider.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task ShortCircuit_Streaming_Provider_Is_Never_Called()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("SHOULD NOT APPEAR") });
        var shortCircuit = new StreamingShortCircuitMiddleware(new[] { "x", "y" });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [shortCircuit],
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) deltas.Add(d);

        deltas.Should().Equal("x", "y");
        provider.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task OnDeltaAsync_Transform_Mutates_Deltas_In_Flight()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("hello"),
            new CompletionUpdate("world"),
        });
        var upper = new DeltaUpperCaseMiddleware();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [upper],
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) deltas.Add(d);

        deltas.Should().Equal("HELLO", "WORLD");
    }

    [Fact]
    public async Task OnStreamCompleteAsync_Fires_With_Accumulated_Response()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("part1 ", ModelId: "m1", PromptTokens: 10),
            new CompletionUpdate("part2", CompletionTokens: 5),
        });
        CompletionResponse? observed = null;
        var observer = new StreamCompleteObserverMiddleware(r => observed = r);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [observer],
        });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        observed.Should().NotBeNull();
        observed!.Text.Should().Be("part1 part2");
        observed.ModelId.Should().Be("m1");
        observed.PromptTokens.Should().Be(10);
        observed.CompletionTokens.Should().Be(5);
    }

    // ── GW-2/GW-3: ordering ─────────────────────────────────────────────────

    [Fact]
    public async Task Gateway_Middleware_Prepended_Before_Filters_In_Registration_Order()
    {
        var order = new List<string>();
        var gw1 = new OrderTrackingMiddleware("gw1", order);
        var gw2 = new OrderTrackingMiddleware("gw2", order);
        var filter1 = new OrderTrackingFilter("f1", order);
        var filter2 = new OrderTrackingFilter("f2", order);

        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [gw1, gw2],
            Filters = [filter1, filter2],
        });

        await agent.AskAsync("hello");

        order.Should().Equal("gw1", "gw2", "f1", "f2");
    }

    [Fact]
    public async Task Gateway_Middleware_Prepended_Before_StreamingFilters_In_Registration_Order()
    {
        var order = new List<string>();
        var gw1 = new OrderTrackingMiddleware("gw1", order);
        var gw2 = new OrderTrackingMiddleware("gw2", order);
        var sf1 = new OrderTrackingStreamingFilter("sf1", order);

        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("ok") });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [gw1, gw2],
            StreamingFilters = [sf1],
        });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        order.Should().Equal("gw1-stream", "gw2-stream", "sf1");
    }

    // ── GW-4: DI wiring ─────────────────────────────────────────────────────

    [Fact]
    public void AddLlmGatewayMiddleware_Registers_As_LlmGatewayMiddleware_Singleton()
    {
        var services = new ServiceCollection();
        services.AddLlmGatewayMiddleware<PassThroughMiddleware>();
        services.AddLlmGatewayMiddleware<DeltaUpperCaseMiddleware>();

        var sp = services.BuildServiceProvider();
        var all = sp.GetServices<LlmGatewayMiddleware>().ToList();

        all.Should().HaveCount(2);
        all[0].Should().BeOfType<PassThroughMiddleware>();
        all[1].Should().BeOfType<DeltaUpperCaseMiddleware>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class PassThroughMiddleware : LlmGatewayMiddleware { }

    private sealed class ShortCircuitMiddleware(CompletionResponse response) : LlmGatewayMiddleware
    {
        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class RequestMutatingMiddleware(string systemPrompt) : LlmGatewayMiddleware
    {
        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
            => next(request with { SystemPrompt = systemPrompt }, cancellationToken);
    }

    private sealed class ResponseMutatingMiddleware(string text) : LlmGatewayMiddleware
    {
        protected override async Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            var response = await next(request, cancellationToken).ConfigureAwait(false);
            return response with { Text = text };
        }
    }

    private sealed class StreamingShortCircuitMiddleware(IEnumerable<string> deltas) : LlmGatewayMiddleware
    {
#pragma warning disable CS1998 // Async iterator lacks await — yield return is sufficient.
        protected override async IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var d in deltas)
                yield return new CompletionUpdate(d);
        }
#pragma warning restore CS1998
    }

    private sealed class DeltaUpperCaseMiddleware : LlmGatewayMiddleware
    {
        protected override ValueTask<CompletionUpdate> OnDeltaAsync(
            CompletionUpdate update,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(update with { TextDelta = update.TextDelta.ToUpperInvariant() });
    }

    private sealed class StreamCompleteObserverMiddleware(Action<CompletionResponse> observe) : LlmGatewayMiddleware
    {
        protected override ValueTask OnStreamCompleteAsync(
            CompletionResponse final,
            CancellationToken cancellationToken = default)
        {
            observe(final);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OrderTrackingMiddleware(string name, List<string> order) : LlmGatewayMiddleware
    {
        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            order.Add(name);
            return next(request, cancellationToken);
        }

        protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            CancellationToken cancellationToken)
        {
            order.Add($"{name}-stream");
            return next(request, cancellationToken);
        }
    }

    private sealed class OrderTrackingFilter(string name, List<string> order) : IAgentFilter
    {
        public Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            order.Add(name);
            return next(request, cancellationToken);
        }
    }

    private sealed class OrderTrackingStreamingFilter(string name, List<string> order) : IStreamingAgentFilter
    {
        public IAsyncEnumerable<CompletionUpdate> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            CancellationToken cancellationToken)
        {
            order.Add(name);
            return next(request, cancellationToken);
        }
    }
}
