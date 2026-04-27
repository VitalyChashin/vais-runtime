// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class ToolGatewayMiddlewareTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    // ── TG-8 #1: pass-through ───────────────────────────────────────────────

    [Fact]
    public async Task PassThrough_Middleware_Tool_Is_Invoked_And_Outcome_Returned()
    {
        var tool = new FakeGwTool("ping", _ => "pong");
        var dispatcher = new DefaultToolCallDispatcher(
            new GwToolRegistry(tool),
            gatewayMiddleware: [new PassThroughGwMiddleware()]);

        var outcome = await dispatcher.DispatchAsync(
            new ToolCallRequest("ping", EmptyArgs, "c1"), AgentContext.Empty);

        outcome.CallId.Should().Be("c1");
        outcome.Result.Should().Be("pong");
        outcome.Error.Should().BeNull();
    }

    // ── TG-8 #2: short-circuit (deny) ───────────────────────────────────────

    [Fact]
    public async Task ShortCircuit_Deny_Tool_Never_Called_And_CallId_Preserved()
    {
        var invoked = false;
        var tool = new FakeGwTool("dangerous", _ => { invoked = true; return "result"; });
        var dispatcher = new DefaultToolCallDispatcher(
            new GwToolRegistry(tool),
            gatewayMiddleware: [new DenyGwMiddleware()]);

        var outcome = await dispatcher.DispatchAsync(
            new ToolCallRequest("dangerous", EmptyArgs, "c2"), AgentContext.Empty);

        invoked.Should().BeFalse();
        outcome.CallId.Should().Be("c2");
        outcome.Error.Should().Be("ToolDenied");
    }

    // ── TG-8 #3: short-circuit (cached result) ──────────────────────────────

    [Fact]
    public async Task ShortCircuit_CachedResult_Tool_Never_Called_CallId_Preserved()
    {
        var invoked = false;
        var tool = new FakeGwTool("query", _ => { invoked = true; return "live"; });
        var cached = new ToolCallOutcome("original-call", "cached-result");
        var dispatcher = new DefaultToolCallDispatcher(
            new GwToolRegistry(tool),
            gatewayMiddleware: [new CachedResultGwMiddleware(cached)]);

        var outcome = await dispatcher.DispatchAsync(
            new ToolCallRequest("query", EmptyArgs, "c3"), AgentContext.Empty);

        invoked.Should().BeFalse();
        // CallId in returned outcome must be from context, not from the stored cached outcome.
        outcome.CallId.Should().Be("c3");
        outcome.Result.Should().Be("cached-result");
        outcome.Error.Should().BeNull();
    }

    // ── TG-8 #4: response mutation ──────────────────────────────────────────

    [Fact]
    public async Task Response_Mutation_Caller_Receives_Modified_Result()
    {
        var tool = new FakeGwTool("greet", _ => "hello");
        var dispatcher = new DefaultToolCallDispatcher(
            new GwToolRegistry(tool),
            gatewayMiddleware: [new MutatingGwMiddleware("HELLO (enriched)")]);

        var outcome = await dispatcher.DispatchAsync(
            new ToolCallRequest("greet", EmptyArgs, "c4"), AgentContext.Empty);

        outcome.Result.Should().Be("HELLO (enriched)");
        outcome.Error.Should().BeNull();
    }

    // ── TG-8 #5: ordering ───────────────────────────────────────────────────

    [Fact]
    public async Task Ordering_Outer_Runs_First_Inner_Runs_Second()
    {
        var log = new List<string>();
        var tool = new FakeGwTool("any", _ => "ok");
        var dispatcher = new DefaultToolCallDispatcher(
            new GwToolRegistry(tool),
            gatewayMiddleware: [
                new LoggingGwMiddleware("outer", log),
                new LoggingGwMiddleware("inner", log),
            ]);

        await dispatcher.DispatchAsync(
            new ToolCallRequest("any", EmptyArgs, "c5"), AgentContext.Empty);

        log.Should().Equal("outer:before", "inner:before", "inner:after", "outer:after");
    }

    // ── TG-8 #6: journal bypass ─────────────────────────────────────────────

    [Fact]
    public async Task Journal_Hit_Bypasses_Gateway_Chain_Entirely()
    {
        var gwInvoked = false;
        var tool = new FakeGwTool("op", _ => "live");
        var journal = new PrimedJournal("run-1", "j-call-1", new ToolCallOutcome("j-call-1", "journaled"));
        var middleware = new RecordingGwMiddleware(() => gwInvoked = true);

        var dispatcher = new DefaultToolCallDispatcher(
            new GwToolRegistry(tool),
            journal: journal,
            gatewayMiddleware: [middleware]);

        var context = AgentContext.Empty with { RunId = "run-1" };
        var outcome = await dispatcher.DispatchAsync(
            new ToolCallRequest("op", EmptyArgs, "j-call-1"), context);

        gwInvoked.Should().BeFalse("journal hit must bypass gateway chain");
        outcome.Result.Should().Be("journaled");
    }

    // ── TG-8 #7: no-gateway fast path ───────────────────────────────────────

    [Fact]
    public async Task No_Gateway_Fast_Path_Calls_Tool_Directly()
    {
        var invoked = false;
        var tool = new FakeGwTool("direct", _ => { invoked = true; return "done"; });
        var dispatcher = new DefaultToolCallDispatcher(new GwToolRegistry(tool));

        var outcome = await dispatcher.DispatchAsync(
            new ToolCallRequest("direct", EmptyArgs, "c7"), AgentContext.Empty);

        invoked.Should().BeTrue();
        outcome.Error.Should().BeNull();
    }

    // ── TG-8 #8: AllowedTools enforcement survives gateway pass-through ──────

    [Fact]
    public async Task AllowedTools_Enforced_Inside_InnerDispatch_Even_With_Gateway()
    {
        var tool = new FakeGwTool("restricted", _ => "secret");
        var dispatcher = new DefaultToolCallDispatcher(
            new GwToolRegistry(tool),
            gatewayMiddleware: [new PassThroughGwMiddleware()]);

        var context = AgentContext.Empty with
        {
            AllowedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other-tool" },
        };

        var outcome = await dispatcher.DispatchAsync(
            new ToolCallRequest("restricted", EmptyArgs, "c8"), context);

        outcome.Error.Should().Be(nameof(UnauthorizedAccessException));
        outcome.Result.Should().Contain("restricted");
    }

    // ── TG-8 #9: DI wiring via AddToolGatewayMiddleware ─────────────────────

    [Fact]
    public void AddToolGatewayMiddleware_Registers_As_ToolGatewayMiddleware_Singleton()
    {
        var services = new ServiceCollection();
        services.AddToolGatewayMiddleware<PassThroughGwMiddleware>();
        services.AddToolGatewayMiddleware<DenyGwMiddleware>();

        var sp = services.BuildServiceProvider();
        var all = sp.GetServices<ToolGatewayMiddleware>().ToList();

        all.Should().HaveCount(2);
        all[0].Should().BeOfType<PassThroughGwMiddleware>();
        all[1].Should().BeOfType<DenyGwMiddleware>();
    }

    // ── TG-8 #10: StatefulAgentOptions.ToolGatewayMiddleware ordering ────────

    [Fact]
    public async Task ToolGatewayMiddleware_Set_In_Options_Wires_Into_Dispatcher()
    {
        var log = new List<string>();
        var tool = new FakeGwTool("work", _ => "result");

        // Provider returns a tool call then text
        var provider = new SequencedGwProvider(
            new CompletionResponse("calling", ToolCalls: [
                new ToolCallRequest("work", EmptyArgs, "tc-1"),
            ]),
            new CompletionResponse("done"));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new GwToolRegistry(tool),
            ToolGatewayMiddleware = [
                new LoggingGwMiddleware("mw-a", log),
                new LoggingGwMiddleware("mw-b", log),
            ],
        });

        await agent.AskAsync("go");

        log.Should().Contain("mw-a:before");
        log.Should().Contain("mw-b:before");
        // outer (mw-a) must log before inner (mw-b)
        log.IndexOf("mw-a:before").Should().BeLessThan(log.IndexOf("mw-b:before"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class GwToolRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class FakeGwTool(string name, Func<JsonElement, string> invoke) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(invoke(arguments));
    }

    private sealed class PassThroughGwMiddleware : ToolGatewayMiddleware { }

    private sealed class DenyGwMiddleware : ToolGatewayMiddleware
    {
        public override Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext context,
            Func<Task<ToolCallOutcome>> next,
            CancellationToken cancellationToken)
            => Task.FromResult(new ToolCallOutcome(context.CallId, $"Tool '{context.ToolName}' denied.", "ToolDenied"));
    }

    private sealed class CachedResultGwMiddleware(ToolCallOutcome stored) : ToolGatewayMiddleware
    {
        public override Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext context,
            Func<Task<ToolCallOutcome>> next,
            CancellationToken cancellationToken)
            // Remap CallId so the caller gets back the current call's id, not the stored one.
            => Task.FromResult(stored with { CallId = context.CallId });
    }

    private sealed class MutatingGwMiddleware(string mutatedResult) : ToolGatewayMiddleware
    {
        public override async Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext context,
            Func<Task<ToolCallOutcome>> next,
            CancellationToken cancellationToken)
        {
            var outcome = await next().ConfigureAwait(false);
            return outcome with { Result = mutatedResult };
        }
    }

    private sealed class LoggingGwMiddleware(string name, List<string> log) : ToolGatewayMiddleware
    {
        public override async Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext context,
            Func<Task<ToolCallOutcome>> next,
            CancellationToken cancellationToken)
        {
            log.Add($"{name}:before");
            var outcome = await next().ConfigureAwait(false);
            log.Add($"{name}:after");
            return outcome;
        }
    }

    private sealed class RecordingGwMiddleware(Action onInvoke) : ToolGatewayMiddleware
    {
        public override Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext context,
            Func<Task<ToolCallOutcome>> next,
            CancellationToken cancellationToken)
        {
            onInvoke();
            return next();
        }
    }

    private sealed class PrimedJournal(string runId, string callId, ToolCallOutcome outcome) : IAgentJournal
    {
        public async IAsyncEnumerable<JournalEntry> ReadAsync(
            string rid,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (rid == runId)
                yield return new ToolCallRecorded(runId, callId, "op", EmptyArgs, outcome, DateTimeOffset.UtcNow);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ClearAsync(string rid, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class SequencedGwProvider(params CompletionResponse[] responses) : ICompletionProvider
    {
        private int _index;
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_index < responses.Length ? responses[_index++] : responses[^1]);
    }
}
