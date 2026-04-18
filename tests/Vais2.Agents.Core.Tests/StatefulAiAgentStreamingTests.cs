// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

/// <summary>
/// Focused coverage for <see cref="StatefulAiAgent.StreamAsync"/>. Uses a
/// <see cref="FakeStreamingCompletionProvider"/> so tests stay deterministic and
/// don't depend on SK or MAF plumbing.
/// </summary>
public sealed class StatefulAiAgentStreamingTests
{
    [Fact]
    public async Task StreamAsync_Rejects_Empty_User_Message()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("x") });
        var agent = new StatefulAiAgent(provider);

        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("   "))
            {
            }
        };

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.ParamName.Should().Be("userMessage");
    }

    [Fact]
    public async Task StreamAsync_Throws_When_Provider_Is_Not_Streaming()
    {
        // FakeCompletionProvider is plain ICompletionProvider — no IStreamingCompletionProvider.
        var agent = new StatefulAiAgent(new FakeCompletionProvider());

        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("hi"))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not support streaming*");
    }

    [Fact]
    public async Task StreamAsync_Yields_Deltas_In_Order_And_Appends_Full_History()
    {
        var updates = new[]
        {
            new CompletionUpdate("Hello"),
            new CompletionUpdate(", "),
            new CompletionUpdate("world", ModelId: "fake-model", PromptTokens: 3, CompletionTokens: 2),
        };
        var provider = new FakeStreamingCompletionProvider(updates);
        var agent = new StatefulAiAgent(provider);

        var collected = new List<string>();
        await foreach (var delta in agent.StreamAsync("greet"))
        {
            collected.Add(delta);
        }

        collected.Should().Equal("Hello", ", ", "world");

        agent.History.Should().HaveCount(2);
        agent.History[0].Should().Be(new ChatTurn(ChatRole.User, "greet"));
        agent.History[1].Should().Be(new ChatTurn(ChatRole.Assistant, "Hello, world"));
    }

    [Fact]
    public async Task StreamAsync_Reports_Usage_Once_With_Accumulated_Metadata_From_Final_Update()
    {
        var updates = new[]
        {
            new CompletionUpdate("a"),
            new CompletionUpdate("b", ModelId: "m1", PromptTokens: 7, CompletionTokens: 4),
        };
        var usageSink = new RecordingUsageSink();
        var provider = new FakeStreamingCompletionProvider(updates);
        var agent = new StatefulAiAgent(
            provider,
            new StatefulAgentOptions { UsageSink = usageSink });

        await foreach (var _ in agent.StreamAsync("go")) { }

        usageSink.Records.Should().ContainSingle();
        var record = usageSink.Records[0];
        record.ProviderName.Should().Be("FakeStreaming");
        record.ModelId.Should().Be("m1");
        record.PromptTokens.Should().Be(7);
        record.CompletionTokens.Should().Be(4);
        record.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_Propagates_Cancellation()
    {
        var provider = new FakeStreamingCompletionProvider(
            Enumerable.Range(0, 100).Select(i => new CompletionUpdate($"{i} ")));
        var agent = new StatefulAiAgent(provider);

        using var cts = new CancellationTokenSource();

        Func<Task> act = async () =>
        {
            await foreach (var delta in agent.StreamAsync("go", cts.Token))
            {
                cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StreamAsync_Does_Not_Append_Assistant_Turn_When_Provider_Throws()
    {
        var provider = new FakeStreamingCompletionProvider(_ =>
            Throw<IEnumerable<CompletionUpdate>>(new InvalidOperationException("boom")));
        var agent = new StatefulAiAgent(provider);

        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("go")) { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        // User turn was added before streaming started — stays. Assistant turn must NOT be appended
        // on failure: the caller got no coherent response to persist.
        agent.History.Should().ContainSingle().Which.Role.Should().Be(ChatRole.User);
    }

    private static T Throw<T>(Exception ex) => throw ex;

    private sealed class RecordingUsageSink : IUsageSink
    {
        public List<UsageRecord> Records { get; } = new();

        public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
