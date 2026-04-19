// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// v0.5 PR 5: durable-execution journal backed by an Orleans grain. Exercises
/// the full wire — append, read, clear, cross-activation rehydration, plus
/// end-to-end cache-replay through <see cref="Vais.Agents.Core.StatefulAiAgent"/>
/// composed with <see cref="OrleansAgentJournal"/>.
/// </summary>
[Collection(OrleansClusterCollection.CollectionName)]
public sealed class AgentRunJournalGrainTests
{
    private readonly OrleansClusterFixture _fx;

    public AgentRunJournalGrainTests(OrleansClusterFixture fx) => _fx = fx;

    [Fact]
    public async Task Append_Then_GetEntries_Round_Trips_A_ToolCallRecorded_Entry()
    {
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";
        var entry = MakeEntry(runId, "c1", "tool-a", "result-a");

        await journal.AppendAsync(entry);

        var read = await CollectAsync(journal.ReadAsync(runId));
        read.Should().ContainSingle();
        var recorded = read[0].Should().BeOfType<ToolCallRecorded>().Subject;
        recorded.RunId.Should().Be(runId);
        recorded.CallId.Should().Be("c1");
        recorded.ToolName.Should().Be("tool-a");
        recorded.Outcome.Result.Should().Be("result-a");
    }

    [Fact]
    public async Task Entries_Preserve_Append_Order_Across_Reads()
    {
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";

        for (var i = 0; i < 5; i++)
        {
            await journal.AppendAsync(MakeEntry(runId, $"c{i}", "tool", $"r{i}"));
        }

        var read = await CollectAsync(journal.ReadAsync(runId));
        read.Select(e => ((ToolCallRecorded)e).CallId)
            .Should().Equal("c0", "c1", "c2", "c3", "c4");
    }

    [Fact]
    public async Task Different_Runs_Are_Independent_Grains()
    {
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runA = $"run-a-{Guid.NewGuid():N}";
        var runB = $"run-b-{Guid.NewGuid():N}";

        await journal.AppendAsync(MakeEntry(runA, "c1", "tool", "from-a"));
        await journal.AppendAsync(MakeEntry(runB, "c1", "tool", "from-b"));

        var a = await CollectAsync(journal.ReadAsync(runA));
        var b = await CollectAsync(journal.ReadAsync(runB));

        a.Should().ContainSingle();
        ((ToolCallRecorded)a[0]).Outcome.Result.Should().Be("from-a");
        b.Should().ContainSingle();
        ((ToolCallRecorded)b[0]).Outcome.Result.Should().Be("from-b");
    }

    [Fact]
    public async Task Entries_Survive_Grain_Activation_Collection()
    {
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";

        await journal.AppendAsync(MakeEntry(runId, "c1", "tool", "persisted"));
        await journal.AppendAsync(MakeEntry(runId, "c2", "tool", "still-here"));

        // Force all grains to deactivate; memory grain storage should hold state
        // across activations so the next read rehydrates the list.
        var mgmt = _fx.Cluster.Client.GetGrain<IManagementGrain>(0);
        await mgmt.ForceActivationCollection(TimeSpan.Zero);

        var read = await CollectAsync(journal.ReadAsync(runId));
        read.Should().HaveCount(2);
        read.Select(e => ((ToolCallRecorded)e).CallId).Should().Equal("c1", "c2");
    }

    [Fact]
    public async Task ClearAsync_Empties_The_Journal()
    {
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";
        await journal.AppendAsync(MakeEntry(runId, "c1", "tool", "ok"));

        await journal.ClearAsync(runId);

        var read = await CollectAsync(journal.ReadAsync(runId));
        read.Should().BeEmpty();
    }

    [Fact]
    public async Task JsonElement_Arguments_Round_Trip_Through_Orleans()
    {
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";
        var args = JsonDocument.Parse("""{"city":"Paris","count":3}""").RootElement;

        await journal.AppendAsync(new ToolCallRecorded(
            RunId: runId,
            CallId: "c1",
            ToolName: "weather",
            Arguments: args,
            Outcome: new ToolCallOutcome("c1", "sunny"),
            At: DateTimeOffset.UtcNow));

        var read = await CollectAsync(journal.ReadAsync(runId));
        var recorded = read.Should().ContainSingle().Which.Should().BeOfType<ToolCallRecorded>().Subject;
        recorded.Arguments.GetProperty("city").GetString().Should().Be("Paris");
        recorded.Arguments.GetProperty("count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Grain_Converter_Handles_Exact_ToolCallRecorded_Type()
    {
        // Stores the concrete ToolCallRecorded through the grain's
        // List<JournalEntry> state — exercises the abstract-base converter path.
        // Then reads back and confirms the subtype is preserved.
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";

        var recorded = new ToolCallRecorded(
            RunId: runId,
            CallId: "c1",
            ToolName: "tool",
            Arguments: JsonDocument.Parse("{}").RootElement,
            Outcome: new ToolCallOutcome("c1", "result", Error: "IOException"),
            At: DateTimeOffset.UtcNow);
        await journal.AppendAsync(recorded);

        var read = await CollectAsync(journal.ReadAsync(runId));
        read.Should().ContainSingle().Which.Should().BeOfType<ToolCallRecorded>();
        ((ToolCallRecorded)read[0]).Outcome.Error.Should().Be("IOException");
    }

    [Fact]
    public async Task OrleansAgentRuntime_GetJournal_Returns_Working_Journal()
    {
        var runtime = new OrleansAgentRuntime(_fx.Cluster.Client);
        var journal = runtime.GetJournal();
        var runId = $"run-{Guid.NewGuid():N}";

        await journal.AppendAsync(MakeEntry(runId, "c1", "tool", "ok"));
        var read = await CollectAsync(journal.ReadAsync(runId));
        read.Should().ContainSingle();
    }

    [Fact]
    public async Task Cross_Process_Resume_Cache_Replays_Journaled_Tool_Outcome()
    {
        // Drive one run with a tool-guardrail interrupt; journal records nothing
        // (the tool never ran). Simulate cross-process resume by appending a
        // ToolCallRecorded entry directly to the Orleans journal for the run,
        // then resume the agent with the same RunId and observe cache-replay.
        var journal = new OrleansAgentJournal(_fx.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";

        // Pre-seed the Orleans journal with a completed tool outcome (simulating
        // that this tool ran before the interrupt pause).
        await journal.AppendAsync(new ToolCallRecorded(
            RunId: runId,
            CallId: "seeded-c1",
            ToolName: "probe",
            Arguments: JsonDocument.Parse("{}").RootElement,
            Outcome: new ToolCallOutcome("seeded-c1", "cached-from-silo"),
            At: DateTimeOffset.UtcNow));

        // Local agent configured to consult the Orleans journal + a live tool.
        var invocations = 0;
        var tool = new FakeTool("probe", _ => { invocations++; return "would-be-live"; });

        var scripted = new Queue<Vais.Agents.CompletionResponse>(new[]
        {
            new Vais.Agents.CompletionResponse("calling", ToolCalls: new[]
            {
                new ToolCallRequest("probe", JsonDocument.Parse("{}").RootElement, "seeded-c1"),
            }),
            new Vais.Agents.CompletionResponse("final"),
        });
        var provider = new LocalFakeProvider(_ => scripted.Dequeue());

        var agent = new Vais.Agents.Core.StatefulAiAgent(provider, new Vais.Agents.Core.StatefulAgentOptions
        {
            ToolRegistry = new FakeRegistry(tool),
            Journal = journal,
        });

        var reply = await agent.ResumeAsync(new ResumeInput(
            InterruptId: "i1",
            Payload: JsonDocument.Parse("\"approved\"").RootElement)
        {
            RunId = runId,
        });

        reply.Should().Be("final");
        invocations.Should().Be(0, "the pre-seeded journal entry should short-circuit the tool");
    }

    // ---- helpers ----

    private static JournalEntry MakeEntry(string runId, string callId, string toolName, string result)
        => new ToolCallRecorded(
            RunId: runId,
            CallId: callId,
            ToolName: toolName,
            Arguments: JsonDocument.Parse("{}").RootElement,
            Outcome: new ToolCallOutcome(callId, result),
            At: DateTimeOffset.UtcNow);

    private static async Task<List<JournalEntry>> CollectAsync(IAsyncEnumerable<JournalEntry> source)
    {
        var list = new List<JournalEntry>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    private sealed class LocalFakeProvider(Func<Vais.Agents.CompletionRequest, Vais.Agents.CompletionResponse> impl) : Vais.Agents.ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<Vais.Agents.CompletionResponse> CompleteAsync(Vais.Agents.CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }

    private sealed class FakeRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class FakeTool(string name, Func<JsonElement, string> invoke) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(invoke(arguments));
    }
}
