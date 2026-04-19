// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// PR 2 of the durable-execution pillar: <see cref="DefaultToolCallDispatcher"/>
/// writes every <see cref="ToolCallOutcome"/> to the <see cref="IAgentJournal"/>
/// when one is supplied + cache-replays on re-dispatch of the same
/// (<see cref="AgentContext.RunId"/>, <see cref="ToolCallRequest.CallId"/>) pair.
/// </summary>
public sealed class DefaultToolCallDispatcherJournalingTests
{
    [Fact]
    public async Task Null_RunId_Means_No_Journal_Interaction_Even_When_Journal_Is_Set()
    {
        // A journal that throws on any call would blow up the dispatcher if it were
        // reached — this test proves the dispatcher short-circuits journal I/O when
        // RunId is null (preserves pre-v0.5 behaviour).
        var tool = new FakeTool("echo", _ => "ok");
        var journal = new ThrowingJournal();
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);

        var call = new ToolCallRequest("echo", EmptyArgs, "c1");
        var outcome = await dispatcher.DispatchAsync(call, AgentContext.Empty);

        outcome.Result.Should().Be("ok");
        journal.AppendCount.Should().Be(0);
        journal.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task Journal_Is_Skipped_When_Journal_Is_Null_Even_With_RunId()
    {
        // Default dispatcher with no journal arg defaults to NullAgentJournal.Instance.
        // RunId-on-context with the null journal must not touch any journal API
        // (v0.4 behaviour preserved).
        var invocations = 0;
        var tool = new FakeTool("echo", _ => { invocations++; return "ok"; });
        var dispatcher = new DefaultToolCallDispatcher(new FakeRegistry(tool));

        var ctx = AgentContext.Empty with { RunId = "run-1" };
        var call = new ToolCallRequest("echo", EmptyArgs, "c1");
        var first = await dispatcher.DispatchAsync(call, ctx);
        var second = await dispatcher.DispatchAsync(call, ctx);

        // Without a real journal, the dispatcher has no cache to replay from —
        // so the tool runs twice.
        first.Result.Should().Be("ok");
        second.Result.Should().Be("ok");
        invocations.Should().Be(2);
    }

    [Fact]
    public async Task Successful_Outcome_Is_Journaled_When_RunId_And_Journal_Both_Set()
    {
        var tool = new FakeTool("echo", _ => "hello");
        var journal = new InMemoryAgentJournal();
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);

        var ctx = AgentContext.Empty with { RunId = "run-a" };
        var call = new ToolCallRequest("echo", EmptyArgs, "c1");
        await dispatcher.DispatchAsync(call, ctx);

        var entries = await CollectAsync(journal.ReadAsync("run-a"));
        entries.Should().HaveCount(1);
        var recorded = entries[0].Should().BeOfType<ToolCallRecorded>().Subject;
        recorded.RunId.Should().Be("run-a");
        recorded.CallId.Should().Be("c1");
        recorded.ToolName.Should().Be("echo");
        recorded.Outcome.Result.Should().Be("hello");
        recorded.Outcome.Error.Should().BeNull();
    }

    [Fact]
    public async Task Tool_Exception_Outcome_Is_Also_Journaled()
    {
        var tool = new FakeTool("broken", _ => throw new InvalidOperationException("kaboom"));
        var journal = new InMemoryAgentJournal();
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);

        var ctx = AgentContext.Empty with { RunId = "run-b" };
        var call = new ToolCallRequest("broken", EmptyArgs, "c2");
        var outcome = await dispatcher.DispatchAsync(call, ctx);

        outcome.Error.Should().Be(nameof(InvalidOperationException));

        var entries = await CollectAsync(journal.ReadAsync("run-b"));
        var recorded = entries.Should().ContainSingle().Which.Should().BeOfType<ToolCallRecorded>().Subject;
        recorded.Outcome.Error.Should().Be(nameof(InvalidOperationException));
        recorded.Outcome.Result.Should().Contain("kaboom");
    }

    [Fact]
    public async Task Unknown_Tool_Outcome_Is_Also_Journaled()
    {
        var journal = new InMemoryAgentJournal();
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(), toolGuardrails: null, eventBus: null, journal: journal);

        var ctx = AgentContext.Empty with { RunId = "run-c" };
        var call = new ToolCallRequest("missing", EmptyArgs, "c3");
        var outcome = await dispatcher.DispatchAsync(call, ctx);

        outcome.Error.Should().Be(nameof(KeyNotFoundException));

        var entries = await CollectAsync(journal.ReadAsync("run-c"));
        entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Cache_Replay_Bypasses_Tool_Invocation_After_Dispatcher_Rebuild()
    {
        // Simulates a crash + resume: first dispatcher instance runs the tool and
        // journals the outcome; a second fresh instance sharing the journal + same
        // RunId + same CallId returns the cached outcome without invoking the tool.
        var invocations = 0;
        var tool = new FakeTool("echo", _ => { invocations++; return $"run-{invocations}"; });
        var journal = new InMemoryAgentJournal();
        var ctx = AgentContext.Empty with { RunId = "run-x" };
        var call = new ToolCallRequest("echo", EmptyArgs, "c1");

        var first = new DefaultToolCallDispatcher(new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);
        var original = await first.DispatchAsync(call, ctx);
        original.Result.Should().Be("run-1");

        var second = new DefaultToolCallDispatcher(new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);
        var replayed = await second.DispatchAsync(call, ctx);
        replayed.Result.Should().Be("run-1"); // cached, not re-invoked
        invocations.Should().Be(1);
    }

    [Fact]
    public async Task Cache_Replay_Bypasses_Guardrails()
    {
        // Original dispatch passes guardrails. Replay dispatch uses a guardrail that
        // would deny — the cached outcome must short-circuit before the guardrail runs.
        var tool = new FakeTool("echo", _ => "ok");
        var journal = new InMemoryAgentJournal();
        var ctx = AgentContext.Empty with { RunId = "run-y" };
        var call = new ToolCallRequest("echo", EmptyArgs, "c1");

        var first = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);
        await first.DispatchAsync(call, ctx);

        var denying = new DenyingToolGuardrail(denyBefore: true, reason: "should-not-fire");
        var second = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), new[] { (IToolGuardrail)denying }, eventBus: null, journal: journal);
        var replayed = await second.DispatchAsync(call, ctx);

        replayed.Result.Should().Be("ok");
        denying.BeforeInvocations.Should().Be(0);
    }

    [Fact]
    public async Task Cache_Replay_Emits_Single_ToolCallReplayed_Event()
    {
        var tool = new FakeTool("echo", _ => "ok");
        var journal = new InMemoryAgentJournal();
        var ctx = AgentContext.Empty with { RunId = "run-z" };
        var call = new ToolCallRequest("echo", EmptyArgs, "c1");

        var firstBus = new RecordingEventBus();
        var first = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: firstBus, journal: journal);
        await first.DispatchAsync(call, ctx);
        firstBus.Events.Should().HaveCount(2); // ToolCallStarted + ToolCallCompleted on first run.

        var secondBus = new RecordingEventBus();
        var second = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: secondBus, journal: journal);
        await second.DispatchAsync(call, ctx);
        // PR 4: replay emits a single ToolCallReplayed rather than the Started/Completed pair.
        // Keeps observability backends from double-counting against the first dispatch.
        secondBus.Events.Should().ContainSingle().Which.Should().BeOfType<ToolCallReplayed>();
    }

    [Fact]
    public async Task Journal_Append_Failure_Propagates()
    {
        // Journal is load-bearing for resume correctness: append failures must
        // not be silently swallowed.
        var tool = new FakeTool("echo", _ => "ok");
        var journal = new ThrowingJournal(); // ReadAsync yields empty; AppendAsync throws
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);

        var ctx = AgentContext.Empty with { RunId = "run-1" };
        var call = new ToolCallRequest("echo", EmptyArgs, "c1");

        await FluentActions.Invoking(async () => await dispatcher.DispatchAsync(call, ctx))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("journal-append-failed");
    }

    [Fact]
    public async Task Different_CallId_Produces_A_Fresh_Invocation_Even_Within_Same_Run()
    {
        var invocations = 0;
        var tool = new FakeTool("echo", _ => { invocations++; return $"n={invocations}"; });
        var journal = new InMemoryAgentJournal();
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);

        var ctx = AgentContext.Empty with { RunId = "run-1" };
        var c1 = await dispatcher.DispatchAsync(new ToolCallRequest("echo", EmptyArgs, "c1"), ctx);
        var c2 = await dispatcher.DispatchAsync(new ToolCallRequest("echo", EmptyArgs, "c2"), ctx);

        c1.Result.Should().Be("n=1");
        c2.Result.Should().Be("n=2");
        invocations.Should().Be(2);

        // And a replay of c1 on a fresh dispatcher hits the cache.
        var replay = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), toolGuardrails: null, eventBus: null, journal: journal);
        var r1 = await replay.DispatchAsync(new ToolCallRequest("echo", EmptyArgs, "c1"), ctx);
        r1.Result.Should().Be("n=1");
        invocations.Should().Be(2);
    }

    [Fact]
    public async Task StatefulAiAgent_Passes_Journal_Through_To_Default_Dispatcher()
    {
        // End-to-end: options.Journal flows into the auto-constructed DefaultToolCallDispatcher.
        // Uses a custom completion provider that surfaces a tool call, then final text.
        var toolInvocations = 0;
        var tool = new FakeTool("probe", _ => { toolInvocations++; return "tool-said-hi"; });
        var registry = new FakeRegistry(tool);
        var journal = new CountingInMemoryJournal();

        var scriptedResponses = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("calling tool", ToolCalls: new[]
            {
                new ToolCallRequest("probe", EmptyArgs, "call-1"),
            }),
            new CompletionResponse("final answer"),
        });
        var provider = new FakeCompletionProvider(_ => scriptedResponses.Dequeue());

        // Use an AsyncLocal accessor + Push scope so the dispatcher sees a RunId.
        var accessor = new AsyncLocalAgentContextAccessor();

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            Journal = journal,
            ContextAccessor = accessor,
        });

        using var scope = accessor.Push(AgentContext.Empty with { RunId = "agent-run-1" });
        var reply = await agent.AskAsync("hello");

        reply.Should().Be("final answer");
        toolInvocations.Should().Be(1);
        journal.AppendCount.Should().Be(1);
        (await CollectAsync(journal.ReadAsync("agent-run-1"))).Should().HaveCount(1);
    }

    // ---- helpers ----

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static async Task<List<JournalEntry>> CollectAsync(IAsyncEnumerable<JournalEntry> source)
    {
        var list = new List<JournalEntry>();
        await foreach (var item in source) list.Add(item);
        return list;
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

    private sealed class DenyingToolGuardrail(bool denyBefore = false, bool denyAfter = false, string? reason = null) : IToolGuardrail
    {
        public int BeforeInvocations { get; private set; }
        public int AfterInvocations { get; private set; }

        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
        {
            BeforeInvocations++;
            return ValueTask.FromResult(denyBefore ? GuardrailOutcome.Deny(reason) : GuardrailOutcome.Pass);
        }

        public ValueTask<GuardrailOutcome> AfterInvokeAsync(ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
        {
            AfterInvocations++;
            return ValueTask.FromResult(denyAfter ? GuardrailOutcome.Deny(reason) : GuardrailOutcome.Pass);
        }
    }

    private sealed class RecordingEventBus : IAgentEventBus
    {
        public List<AgentEvent> Events { get; } = new();
        public ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return ValueTask.CompletedTask;
        }
        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
            => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class ThrowingJournal : IAgentJournal
    {
        public int AppendCount { get; private set; }
        public int ReadCount { get; private set; }

        public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
        {
            AppendCount++;
            throw new InvalidOperationException("journal-append-failed");
        }

        public async IAsyncEnumerable<JournalEntry> ReadAsync(
            string runId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReadCount++;
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public ValueTask ClearAsync(string runId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class CountingInMemoryJournal : IAgentJournal
    {
        private readonly InMemoryAgentJournal _inner = new();
        public int AppendCount { get; private set; }

        public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
        {
            AppendCount++;
            return _inner.AppendAsync(entry, cancellationToken);
        }

        public IAsyncEnumerable<JournalEntry> ReadAsync(string runId, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(runId, cancellationToken);

        public ValueTask ClearAsync(string runId, CancellationToken cancellationToken = default)
            => _inner.ClearAsync(runId, cancellationToken);
    }
}
