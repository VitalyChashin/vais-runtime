// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Polly;
using Polly.Retry;
using Vais.Agents.Ai.MicrosoftAgentFramework;
using Vais.Agents.Ai.SemanticKernel;
using Vais.Agents.Core;
using Xunit;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;
using SkAuthorRole = Microsoft.SemanticKernel.ChatCompletion.AuthorRole;

namespace Vais.Agents.ParityTests;

/// <summary>
/// Pins the v0.10 idempotence contract on both streaming adapters: exceptions
/// raised before the first <see cref="CompletionUpdate"/> is yielded are safe to
/// retry (the provider is invoked again with the same request); exceptions raised
/// after the first delta are surfaced to the caller unretried. Covers the
/// two-flavour preamble-failure case (sync throw from <c>StreamAsync</c>, throw
/// from first <c>MoveNextAsync</c>) plus the post-first-delta case on each
/// adapter, plus one cross-stack parity test.
/// </summary>
public sealed class StreamingIdempotenceParityTests
{
    private static ResiliencePipeline ZeroDelayRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
            })
            .Build();

    // ---- SK adapter ----

    [Fact]
    public async Task Sk_Preamble_Failure_Retries_Safely()
    {
        var service = new FlakyChatCompletionService(
            new Func<IAsyncEnumerable<StreamingChatMessageContent>>[]
            {
                () => throw new HttpRequestException("preamble"),
                () => YieldSk("a", "b", "c"),
            });

        var deltas = await RunSkAsync(service);

        deltas.Should().Equal("a", "b", "c");
        service.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Sk_First_MoveNext_Failure_Retries_Safely()
    {
        var service = new FlakyChatCompletionService(
            new Func<IAsyncEnumerable<StreamingChatMessageContent>>[]
            {
                () => ThrowOnFirstMoveNextSk(),
                () => YieldSk("a", "b", "c"),
            });

        var deltas = await RunSkAsync(service);

        deltas.Should().Equal("a", "b", "c");
        service.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Sk_Post_First_Delta_Failure_Is_Not_Retried()
    {
        var service = new FlakyChatCompletionService(
            new Func<IAsyncEnumerable<StreamingChatMessageContent>>[]
            {
                () => YieldThenThrowSk("first-delta"),
                () => throw new InvalidOperationException("SHOULD NOT BE CALLED"),
            });

        var deltas = new List<string>();
        var agent = BuildSkAgent(service);
        var act = async () =>
        {
            await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }
        };

        await act.Should().ThrowAsync<IOException>().WithMessage("mid-stream");
        deltas.Should().Equal("first-delta");
        service.Attempts.Should().Be(1);
    }

    // ---- MAF adapter ----

    [Fact]
    public async Task Maf_Preamble_Failure_Retries_Safely()
    {
        var client = new FlakyChatClient(
            new Func<IAsyncEnumerable<ChatResponseUpdate>>[]
            {
                () => throw new HttpRequestException("preamble"),
                () => YieldMaf("a", "b", "c"),
            });

        var deltas = await RunMafAsync(client);

        deltas.Should().Equal("a", "b", "c");
        client.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Maf_First_MoveNext_Failure_Retries_Safely()
    {
        var client = new FlakyChatClient(
            new Func<IAsyncEnumerable<ChatResponseUpdate>>[]
            {
                () => ThrowOnFirstMoveNextMaf(),
                () => YieldMaf("a", "b", "c"),
            });

        var deltas = await RunMafAsync(client);

        deltas.Should().Equal("a", "b", "c");
        client.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Maf_Post_First_Delta_Failure_Is_Not_Retried()
    {
        var client = new FlakyChatClient(
            new Func<IAsyncEnumerable<ChatResponseUpdate>>[]
            {
                () => YieldThenThrowMaf("first-delta"),
                () => throw new InvalidOperationException("SHOULD NOT BE CALLED"),
            });

        var deltas = new List<string>();
        var agent = BuildMafAgent(client);
        var act = async () =>
        {
            await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }
        };

        await act.Should().ThrowAsync<IOException>().WithMessage("mid-stream");
        deltas.Should().Equal("first-delta");
        client.Attempts.Should().Be(1);
    }

    // ---- cross-stack parity ----

    [Fact]
    public async Task Both_Adapters_Produce_Equivalent_Streams_After_Retry()
    {
        // Same preamble-failure + second-attempt-succeeds scenario on both stacks.
        // The neutral StreamAsync contract is expected to produce identical observable
        // behaviour (same deltas in order, same attempt count) regardless of backend.

        var skService = new FlakyChatCompletionService(
            new Func<IAsyncEnumerable<StreamingChatMessageContent>>[]
            {
                () => throw new HttpRequestException("preamble"),
                () => YieldSk("a", "b", "c"),
            });
        var sk = await RunSkAsync(skService);

        var mafClient = new FlakyChatClient(
            new Func<IAsyncEnumerable<ChatResponseUpdate>>[]
            {
                () => throw new HttpRequestException("preamble"),
                () => YieldMaf("a", "b", "c"),
            });
        var maf = await RunMafAsync(mafClient);

        sk.Should().Equal(maf);
        skService.Attempts.Should().Be(mafClient.Attempts);
        skService.Attempts.Should().Be(2);
    }

    // ---- SK helpers ----

    private static StatefulAiAgent BuildSkAgent(IChatCompletionService service)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(service);
        return new StatefulAiAgent(
            new SkCompletionProvider(builder.Build()),
            new StatefulAgentOptions { StreamingResiliencePipeline = ZeroDelayRetryPipeline() });
    }

    private static async Task<List<string>> RunSkAsync(IChatCompletionService service)
    {
        var agent = BuildSkAgent(service);
        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }
        return deltas;
    }

#pragma warning disable CS1998 // test iterators are synchronous by design
    private static async IAsyncEnumerable<StreamingChatMessageContent> YieldSk(params string[] chunks)
    {
        foreach (var c in chunks)
        {
            yield return new StreamingChatMessageContent(SkAuthorRole.Assistant, c);
        }
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> ThrowOnFirstMoveNextSk()
    {
        throw new HttpRequestException("first-move-next");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> YieldThenThrowSk(string firstDelta)
    {
        yield return new StreamingChatMessageContent(SkAuthorRole.Assistant, firstDelta);
        throw new IOException("mid-stream");
    }
#pragma warning restore CS1998

    // ---- MAF helpers ----

    private static StatefulAiAgent BuildMafAgent(IChatClient client)
        => new(
            new MafCompletionProvider(client),
            new StatefulAgentOptions { StreamingResiliencePipeline = ZeroDelayRetryPipeline() });

    private static async Task<List<string>> RunMafAsync(IChatClient client)
    {
        var agent = BuildMafAgent(client);
        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }
        return deltas;
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<ChatResponseUpdate> YieldMaf(params string[] chunks)
    {
        foreach (var c in chunks)
        {
            yield return new ChatResponseUpdate(MeaiChatRole.Assistant, c);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowOnFirstMoveNextMaf()
    {
        throw new HttpRequestException("first-move-next");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldThenThrowMaf(string firstDelta)
    {
        yield return new ChatResponseUpdate(MeaiChatRole.Assistant, firstDelta);
        throw new IOException("mid-stream");
    }
#pragma warning restore CS1998

    // ---- test doubles ----

    /// <summary>
    /// <see cref="IChatCompletionService"/> with a per-attempt behaviour queue. Each call
    /// to <see cref="GetStreamingChatMessageContentsAsync"/> advances the attempt index
    /// and invokes the matching factory — which may yield updates, throw synchronously,
    /// or return an enumerable that throws on first <c>MoveNextAsync</c>.
    /// </summary>
    private sealed class FlakyChatCompletionService(params Func<IAsyncEnumerable<StreamingChatMessageContent>>[] behaviours)
        : IChatCompletionService
    {
        private readonly Func<IAsyncEnumerable<StreamingChatMessageContent>>[] _behaviours = behaviours;
        public int Attempts { get; private set; }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>
        {
            ["ModelId"] = "flaky-sk-model",
        };

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var idx = Attempts;
            Attempts++;
            if (idx >= _behaviours.Length)
            {
                throw new InvalidOperationException($"FlakyChatCompletionService out of scripts (attempt {idx + 1}, have {_behaviours.Length})");
            }
            return _behaviours[idx]();
        }
    }

    /// <summary>
    /// <see cref="IChatClient"/> with a per-attempt behaviour queue. Each call to
    /// <see cref="GetStreamingResponseAsync"/> advances the attempt index and invokes
    /// the matching factory.
    /// </summary>
    private sealed class FlakyChatClient(params Func<IAsyncEnumerable<ChatResponseUpdate>>[] behaviours) : IChatClient
    {
        private readonly Func<IAsyncEnumerable<ChatResponseUpdate>>[] _behaviours = behaviours;
        public int Attempts { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var idx = Attempts;
            Attempts++;
            if (idx >= _behaviours.Length)
            {
                throw new InvalidOperationException($"FlakyChatClient out of scripts (attempt {idx + 1}, have {_behaviours.Length})");
            }
            return _behaviours[idx]();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return new ChatClientMetadata(providerName: "flaky-maf", defaultModelId: "flaky-maf-model");
            }
            return null;
        }

        public void Dispose() { }
    }
}
