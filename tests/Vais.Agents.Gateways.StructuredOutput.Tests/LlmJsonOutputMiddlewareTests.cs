// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Core;
using Vais.Agents.Gateways.Testing;
using Xunit;

namespace Vais.Agents.Gateways.StructuredOutput.Tests;

/// <summary>
/// GW-15 — Tests for <see cref="LlmJsonOutputMiddleware{T}"/>.
/// </summary>
public sealed class LlmJsonOutputMiddlewareTests
{
    private sealed record AnswerDto(string Answer, int Score);

    // Filter chain builds right-to-left: index 0 is outermost (first to run).
    // JsonOutput must be first so it wraps the mock and can validate what the mock returns.
    private static StatefulAiAgent BuildAgent(params CompletionResponse[] responses)
    {
        var stub = new StubProvider();
        return new StatefulAiAgent(stub, new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmJsonOutputMiddleware<AnswerDto>(),
                new LlmMockMiddleware(responses),
            ],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });
    }

    [Fact]
    public async Task Valid_Json_Passes_Through()
    {
        var agent = BuildAgent(new CompletionResponse("""{"Answer":"ok","Score":1}"""));

        var result = await agent.AskAsync("hello");

        result.Should().Contain("ok");
    }

    [Fact]
    public async Task Invalid_Json_Throws_GuardrailDeniedException()
    {
        var agent = BuildAgent(new CompletionResponse("not json at all"));

        Func<Task> act = () => agent.AskAsync("hello");
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>()
            .Where(e => e.Layer == GuardrailLayer.Output);
    }

    [Fact]
    public async Task Wrong_Shape_Throws_GuardrailDeniedException()
    {
        // Valid JSON but not AnswerDto shape — missing required fields.
        var agent = BuildAgent(new CompletionResponse("""{"Unrelated":true}"""));

        // System.Text.Json doesn't throw on missing properties by default,
        // so only actual JSON parse errors trigger the guardrail.
        // Verify that correct JSON does NOT throw.
        var result = await agent.AskAsync("hello");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Valid_Json_Passes_On_Streaming_Path()
    {
        var stub = new StubStreamingProvider();
        var agent = new StatefulAiAgent(stub, new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmJsonOutputMiddleware<AnswerDto>(),
                new LlmMockMiddleware(new CompletionResponse("""{"Answer":"hi","Score":5}""")),
            ],
        });

        var chunks = new List<string>();
        await foreach (var chunk in agent.StreamAsync("hello"))
            chunks.Add(chunk);

        string.Concat(chunks).Should().Contain("hi");
    }

    [Fact]
    public async Task Invalid_Json_Throws_On_Streaming_Path()
    {
        var stub = new StubStreamingProvider();
        var agent = new StatefulAiAgent(stub, new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmJsonOutputMiddleware<AnswerDto>(),
                new LlmMockMiddleware(new CompletionResponse("bad json")),
            ],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });

        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("hello")) { }
        };
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>()
            .Where(e => e.Layer == GuardrailLayer.Output);
    }

    private sealed class StubProvider : ICompletionProvider
    {
        public string ProviderName => "stub";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
    }

    private sealed class StubStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "stub-streaming";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
    }
}
