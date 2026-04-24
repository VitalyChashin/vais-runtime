// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Verifies streaming journal replay functionality. When <see cref="ReplayMode.Full"/>
/// is enabled, each <see cref="CompletionUpdate"/> is journaled and replayed verbatim on resume.
/// </summary>
public sealed class StreamingJournalReplayTests
{
    [Fact]
    public async Task ToolOnlyMode_DoesNotJournalDeltas()
    {
        var journal = new InMemoryAgentJournal();
        var provider = new FakeStreamingProvider(new[]
        {
            new CompletionUpdate("Hello"),
            new CompletionUpdate(" "),
            new CompletionUpdate("world!")
        });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ReplayMode = ReplayMode.ToolOnly
        });

        var runId = "tool-only-test-run";
        await foreach (var _ in agent.StreamAsync("hi", new AgentContext { RunId = runId }, default)) { }

        var entries = await CollectAsync(journal.ReadAsync(runId));
        entries.Should().BeEmpty("ToolOnly mode should not journal deltas");
    }

    [Fact]
    public async Task FullMode_JournalsAllDeltas()
    {
        var journal = new InMemoryAgentJournal();
        var deltas = new[]
        {
            new CompletionUpdate("Hello"),
            new CompletionUpdate(" "),
            new CompletionUpdate("world!")
        };

        var provider = new FakeStreamingProvider(deltas);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ReplayMode = ReplayMode.Full
        });

        var runId = "full-mode-test-run";
        await foreach (var _ in agent.StreamAsync("hi", new AgentContext { RunId = runId }, default)) { }

        var entries = await CollectAsync(journal.ReadAsync(runId));
        entries.Should().HaveCount(3);

        var deltaEntries = entries.OfType<CompletionDeltaRecorded>().ToList();
        deltaEntries.Should().HaveCount(3);

        deltaEntries[0].SequenceNumber.Should().Be(0);
        deltaEntries[0].Delta.TextDelta.Should().Be("Hello");
        deltaEntries[1].SequenceNumber.Should().Be(1);
        deltaEntries[1].Delta.TextDelta.Should().Be(" ");
        deltaEntries[2].SequenceNumber.Should().Be(2);
        deltaEntries[2].Delta.TextDelta.Should().Be("world!");
    }

    [Fact]
    public async Task FullMode_JournalsToolCallsOnDelta()
    {
        var journal = new InMemoryAgentJournal();
        var toolCall = new ToolCallRequest("my-tool", JsonDocument.Parse("{}").RootElement, "tc1");
        var deltas = new[]
        {
            new CompletionUpdate("Thinking..."),
            new CompletionUpdate("", ToolCalls: new[] { toolCall })
        };

        var provider = new FakeStreamingProvider(deltas);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ToolRegistry = new FakeRegistry(new FakeTool("my-tool", _ => "tool-result")),
            ReplayMode = ReplayMode.Full
        });

        var runId = "tool-call-delta-run";
        await foreach (var _ in agent.StreamAsync("use tool", new AgentContext { RunId = runId }, default)) { }

        var entries = await CollectAsync(journal.ReadAsync(runId));
        // 2 CompletionDeltaRecorded (one per delta) + 1 ToolCallRecorded (from DefaultToolCallDispatcher)
        entries.Should().HaveCount(3);

        var deltaEntries = entries.OfType<CompletionDeltaRecorded>().ToList();
        deltaEntries.Should().HaveCount(2);

        var deltaEntry = deltaEntries.Single(e => e.Delta.ToolCalls is { Count: > 0 });
        deltaEntry.Delta.ToolCalls!.Should().HaveCount(1);
        deltaEntry.Delta.ToolCalls![0].ToolName.Should().Be("my-tool");
    }

    [Fact]
    public async Task Replay_ReYieldsExactDeltaSequence()
    {
        var journal = new InMemoryAgentJournal();
        var deltas = new[]
        {
            new CompletionUpdate("A"),
            new CompletionUpdate("B"),
            new CompletionUpdate("C")
        };

        var provider = new FakeStreamingProvider(deltas);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ReplayMode = ReplayMode.Full
        });

        var runId = "replay-sequence-run";

        // First run: stream and capture deltas
        var firstRunDeltas = new List<string>();
        await foreach (var evt in agent.StreamAsync("hi", new AgentContext { RunId = runId }, default))
        {
            if (evt is CompletionDelta cd && cd.TextDelta.Length > 0)
            {
                firstRunDeltas.Add(cd.TextDelta);
            }
        }
        firstRunDeltas.Should().Equal("A", "B", "C");

        // Second run (simulating resume): replay from journal
        var replayedDeltas = new List<string>();
        await foreach (var evt in agent.StreamAsync("resume", new AgentContext { RunId = runId }, default))
        {
            if (evt is CompletionDelta cd && cd.TextDelta.Length > 0)
            {
                replayedDeltas.Add(cd.TextDelta);
            }
        }

        replayedDeltas.Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task Replay_BypassesProvider()
    {
        var journal = new InMemoryAgentJournal();
        var deltas = new[] { new CompletionUpdate("test-result") };
        var provider = new FakeStreamingProvider(deltas);

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ReplayMode = ReplayMode.Full
        });

        var runId = "bypass-provider-run";

        // First run: provider should succeed and journal the delta
        provider.CallCount.Should().Be(0);
        await foreach (var _ in agent.StreamAsync("hi", new AgentContext { RunId = runId }, default)) { }
        provider.CallCount.Should().Be(1);

        // Make provider fail to confirm it is not called on replay
        provider.FailOnCall = true;
        provider.CallCount = 0;

        // Second run (replay): provider should NOT be called
        await foreach (var _ in agent.StreamAsync("resume", new AgentContext { RunId = runId }, default)) { }
        provider.CallCount.Should().Be(0, "provider should be bypassed on replay");
    }

    [Fact]
    public async Task Replay_WithToolCalls_ReplaysDeltasAndToolOutcomes()
    {
        var journal = new InMemoryAgentJournal();
        var toolCall = new ToolCallRequest("test-tool", JsonDocument.Parse("{}").RootElement, "tc1");
        var deltas = new[]
        {
            new CompletionUpdate("tool response", ToolCalls: new[] { toolCall })
        };

        var provider = new FakeStreamingProvider(deltas);
        var tool = new FakeTool("test-tool", _ => "tool-result");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ToolRegistry = new FakeRegistry(tool),
            ReplayMode = ReplayMode.Full
        });

        var runId = "replay-with-tools-run";
        var toolInvocations = 0;
        tool.Invoked += (_, _) => { toolInvocations++; };

        // First run: stream with tool call
        var firstTurnDeltas = new List<string>();
        await foreach (var evt in agent.StreamAsync("use tool", new AgentContext { RunId = runId }, default))
        {
            if (evt is CompletionDelta cd && cd.TextDelta.Length > 0)
            {
                firstTurnDeltas.Add(cd.TextDelta);
            }
        }
        firstTurnDeltas.Should().ContainSingle("tool response");
        toolInvocations.Should().Be(1);

        // Reset for second run
        toolInvocations = 0;

        // Second run (replay): deltas replayed, tool outcome from journal
        await foreach (var evt in agent.StreamAsync("resume", new AgentContext { RunId = runId }, default)) { }
        toolInvocations.Should().Be(0, "tool should not be re-invoked on replay");
    }

    [Fact]
    public async Task ToolOnlyMode_OnResume_ProviderReinvoked()
    {
        var journal = new InMemoryAgentJournal();
        var deltas = new[] { new CompletionUpdate("first") };
        var provider = new FakeStreamingProvider(deltas);

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ReplayMode = ReplayMode.ToolOnly
        });

        var runId = "tool-only-resume-run";

        // First run: stream
        await foreach (var _ in agent.StreamAsync("hi", new AgentContext { RunId = runId }, default)) { }
        provider.CallCount.Should().Be(1);

        // Second run (resume with ToolOnly mode): provider should be called again
        await foreach (var _ in agent.StreamAsync("resume", new AgentContext { RunId = runId }, default)) { }
        provider.CallCount.Should().Be(2, "provider should be re-invoked in ToolOnly mode");
    }

    [Fact]
    public async Task SequenceNumbers_IncrementCorrectlyAcrossTurns()
    {
        var journal = new InMemoryAgentJournal();
        var toolCall = new ToolCallRequest("tool", JsonDocument.Parse("{}").RootElement, "tc1");

        // Script: Turn 1 (text + tool), Turn 2 (text only)
        var scriptedDeltas = new Queue<CompletionUpdate>(new[]
        {
            new CompletionUpdate("Turn1-1"),
            new CompletionUpdate("Turn1-2", ToolCalls: new[] { toolCall }),
            new CompletionUpdate("Turn2-1"),
            new CompletionUpdate("Turn2-2")
        });

        var provider = new FakeStreamingProvider(scriptedDeltas);
        var tool = new FakeTool("tool", _ => "result");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Journal = journal,
            ToolRegistry = new FakeRegistry(tool),
            ReplayMode = ReplayMode.Full
        });

        var runId = "sequence-run";
        var deltaEntries = new List<CompletionDeltaRecorded>();

        await foreach (var _ in agent.StreamAsync("multi-turn", new AgentContext { RunId = runId }, default)) { }

        var allEntries = await CollectAsync(journal.ReadAsync(runId));
        deltaEntries = allEntries.OfType<CompletionDeltaRecorded>().ToList();

        deltaEntries.Select(e => e.SequenceNumber).Should().Equal(0, 1, 2, 3);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source)
        {
            results.Add(item);
        }
        return results;
    }

    private sealed class FakeStreamingProvider : IStreamingCompletionProvider, ICompletionProvider
    {
        private readonly Queue<CompletionUpdate> _updates;
        private readonly List<CompletionRequest> _received = new();

        public FakeStreamingProvider(IEnumerable<CompletionUpdate> updates)
        {
            _updates = new Queue<CompletionUpdate>(updates);
        }

        public bool FailOnCall { get; set; }
        public int CallCount { get; set; }

        public string ProviderName => "FakeStreaming";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            _received.Add(request);
            CallCount++;
            if (FailOnCall)
            {
                throw new InvalidOperationException("Provider call blocked");
            }
            var text = string.Concat(_updates.Select(u => u.TextDelta));
            return Task.FromResult(new CompletionResponse(text, "fake-model"));
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators - expected for async enumerables without async operations
        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
            CompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _received.Add(request);
            CallCount++;
            if (FailOnCall)
            {
                throw new InvalidOperationException("Provider call blocked");
            }

            while (_updates.TryDequeue(out var update))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
    }

    private sealed class FakeTool : ITool
    {
        private readonly Func<string, string> _implementation;

        public FakeTool(string name, Func<string, string> implementation)
        {
            Name = name;
            _implementation = implementation;
            Invoked = (_, _) => { };
        }

        public string Name { get; }
        public string Description => "Fake tool for testing";
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement;

        public event Action<AgentContext, ToolCallRequest>? Invoked;

        public Task<string> InvokeAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default)
        {
            var argumentsJson = arguments.GetRawText();
            var result = _implementation(argumentsJson);
            Invoked?.Invoke(new AgentContext(), new ToolCallRequest(Name, arguments, "test-call"));
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRegistry : IToolRegistry
    {
        private readonly ITool? _tool;

        public FakeRegistry(ITool? tool = null)
        {
            _tool = tool;
        }

        public IReadOnlyList<ITool> Tools => _tool is not null ? new[] { _tool } : Array.Empty<ITool>();

        public ITool? GetByName(string name) => _tool?.Name == name ? _tool : null;
    }
}
