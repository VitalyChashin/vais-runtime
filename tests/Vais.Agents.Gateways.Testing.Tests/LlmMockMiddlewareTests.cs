// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Gateways.Testing.Tests;

/// <summary>
/// GW-14 — Tests for <see cref="LlmMockMiddleware"/>.
/// </summary>
public sealed class LlmMockMiddlewareTests
{
    // Minimal provider stub — never reached because mock intercepts.
    private static readonly ICompletionProvider _unreachable =
        new UnreachableProvider();

    [Fact]
    public async Task Returns_Queued_Response_On_NonStreaming_Path()
    {
        var mock = new LlmMockMiddleware(
            new CompletionResponse("reply-1", ModelId: "m1", PromptTokens: 5, CompletionTokens: 2));

        var agent = new StatefulAiAgent(_unreachable, new StatefulAgentOptions
        {
            GatewayMiddleware = [mock],
        });

        var result = await agent.AskAsync("hello");

        result.Should().Be("reply-1");
    }

    [Fact]
    public async Task Returns_Queued_Response_On_Streaming_Path()
    {
        var mock = new LlmMockMiddleware(
            new CompletionResponse("stream-text", ModelId: "s1", PromptTokens: 3, CompletionTokens: 1));

        var agent = new StatefulAiAgent(_unreachable, new StatefulAgentOptions
        {
            GatewayMiddleware = [mock],
        });

        var chunks = new List<string>();
        await foreach (var chunk in agent.StreamAsync("hello"))
            chunks.Add(chunk);

        string.Concat(chunks).Should().Be("stream-text");
    }

    [Fact]
    public async Task Returns_Responses_In_Order()
    {
        var mock = new LlmMockMiddleware(
            new CompletionResponse("first"),
            new CompletionResponse("second"),
            new CompletionResponse("third"));

        var agent = new StatefulAiAgent(_unreachable, new StatefulAgentOptions
        {
            GatewayMiddleware = [mock],
        });

        var r1 = await agent.AskAsync("q1");
        var r2 = await agent.AskAsync("q2");
        var r3 = await agent.AskAsync("q3");

        r1.Should().Be("first");
        r2.Should().Be("second");
        r3.Should().Be("third");
    }

    [Fact]
    public async Task Throws_When_Queue_Exhausted()
    {
        var mock = new LlmMockMiddleware(new CompletionResponse("only-one"));

        var agent = new StatefulAiAgent(_unreachable, new StatefulAgentOptions
        {
            GatewayMiddleware = [mock],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty, // no retries
        });

        await agent.AskAsync("first"); // consumes the one queued response

        Func<Task> act = () => agent.AskAsync("second");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no more queued responses*");
    }

    private sealed class UnreachableProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "unreachable";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach the real provider.");

        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach the real provider.");
    }
}
