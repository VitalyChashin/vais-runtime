// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// PR 3 of the durable-execution pillar: <c>StatefulAiAgent</c> stamps
/// <see cref="AgentContext.RunId"/> on every run so the tool-call dispatcher's
/// journal paths light up automatically. Caller-supplied RunIds win; an optional
/// <see cref="StatefulAgentOptions.RunIdFactory"/> overrides the default
/// <c>Guid.NewGuid().ToString("N")</c>.
/// </summary>
public sealed class RunIdStampingTests
{
    [Fact]
    public async Task AskAsync_Stamps_A_RunId_On_The_Context_Seen_By_Dispatcher()
    {
        AgentContext? observed = null;
        var registry = new FakeRegistry(new FakeTool("echo", _ => "ok"));
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("going to call a tool", ToolCalls: new[]
            {
                new ToolCallRequest("echo", EmptyArgs, "c1"),
            }),
            new CompletionResponse("final"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());
        var dispatcher = new ObservingDispatcher(new DefaultToolCallDispatcher(registry), ctx => observed = ctx);

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolCallDispatcher = dispatcher,
        });

        await agent.AskAsync("hi");

        observed.Should().NotBeNull();
        observed!.RunId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Two_Separate_Asks_Produce_Distinct_RunIds()
    {
        var observed = new List<string?>();
        var registry = new FakeRegistry(new FakeTool("echo", _ => "ok"));

        // Two independent runs, each doing one tool call, then a final answer.
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("first call", ToolCalls: new[] { new ToolCallRequest("echo", EmptyArgs, "c1") }),
            new CompletionResponse("first final"),
            new CompletionResponse("second call", ToolCalls: new[] { new ToolCallRequest("echo", EmptyArgs, "c2") }),
            new CompletionResponse("second final"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());
        var dispatcher = new ObservingDispatcher(new DefaultToolCallDispatcher(registry), ctx => observed.Add(ctx.RunId));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolCallDispatcher = dispatcher,
        });

        await agent.AskAsync("one");
        await agent.AskAsync("two");

        observed.Should().HaveCount(2);
        observed[0].Should().NotBeNullOrWhiteSpace();
        observed[1].Should().NotBeNullOrWhiteSpace();
        observed[0].Should().NotBe(observed[1]);
    }

    [Fact]
    public async Task Caller_Supplied_RunId_Wins_Over_Factory()
    {
        AgentContext? observed = null;
        var registry = new FakeRegistry(new FakeTool("echo", _ => "ok"));
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("calling", ToolCalls: new[] { new ToolCallRequest("echo", EmptyArgs, "c1") }),
            new CompletionResponse("final"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());
        var dispatcher = new ObservingDispatcher(new DefaultToolCallDispatcher(registry), ctx => observed = ctx);

        var accessor = new AsyncLocalAgentContextAccessor();
        var factoryCalls = 0;
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolCallDispatcher = dispatcher,
            ContextAccessor = accessor,
            RunIdFactory = () => { factoryCalls++; return "FACTORY-ID"; },
        });

        using var scope = accessor.Push(AgentContext.Empty with { RunId = "caller-run-99" });
        await agent.AskAsync("hello");

        observed!.RunId.Should().Be("caller-run-99");
        factoryCalls.Should().Be(0, "caller-supplied RunId must short-circuit the factory");
    }

    [Fact]
    public async Task Custom_RunIdFactory_Is_Used_When_Caller_Did_Not_Set_RunId()
    {
        AgentContext? observed = null;
        var registry = new FakeRegistry(new FakeTool("echo", _ => "ok"));
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("calling", ToolCalls: new[] { new ToolCallRequest("echo", EmptyArgs, "c1") }),
            new CompletionResponse("final"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());
        var dispatcher = new ObservingDispatcher(new DefaultToolCallDispatcher(registry), ctx => observed = ctx);

        var nextId = 0;
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolCallDispatcher = dispatcher,
            RunIdFactory = () => $"run-{++nextId:D4}",
        });

        await agent.AskAsync("hello");

        observed!.RunId.Should().Be("run-0001");
    }

    [Fact]
    public async Task StreamAsync_Also_Stamps_A_RunId()
    {
        AgentContext? observed = null;
        var registry = new FakeRegistry(new FakeTool("echo", _ => "ok"));
        var dispatcher = new ObservingDispatcher(new DefaultToolCallDispatcher(registry), ctx => observed = ctx);

        // Script one streaming turn that ends with a tool call, then a clean final turn.
        var provider = new ScriptedMultiTurnStreamingProvider(
            new[]
            {
                new CompletionUpdate(string.Empty, ToolCalls: new[] { new ToolCallRequest("echo", EmptyArgs, "c1") }),
            },
            new[]
            {
                new CompletionUpdate("final answer"),
            });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolCallDispatcher = dispatcher,
        });

        await foreach (var _ in agent.StreamAsync("hi")) { /* drain */ }

        observed!.RunId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Same_RunId_Is_Threaded_To_Every_Tool_Dispatch_Within_One_Run()
    {
        var observedRunIds = new List<string?>();
        var registry = new FakeRegistry(new FakeTool("echo", _ => "ok"));
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("two calls", ToolCalls: new[]
            {
                new ToolCallRequest("echo", EmptyArgs, "c1"),
                new ToolCallRequest("echo", EmptyArgs, "c2"),
            }),
            new CompletionResponse("final"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());
        var dispatcher = new ObservingDispatcher(new DefaultToolCallDispatcher(registry), ctx => observedRunIds.Add(ctx.RunId));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolCallDispatcher = dispatcher,
        });

        await agent.AskAsync("do it");

        observedRunIds.Should().HaveCount(2);
        observedRunIds[0].Should().NotBeNullOrWhiteSpace();
        observedRunIds[0].Should().Be(observedRunIds[1]);
    }

    [Fact]
    public async Task Default_RunIdFactory_Produces_A_32_Hex_String()
    {
        AgentContext? observed = null;
        var registry = new FakeRegistry(new FakeTool("echo", _ => "ok"));
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("calling", ToolCalls: new[] { new ToolCallRequest("echo", EmptyArgs, "c1") }),
            new CompletionResponse("final"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());
        var dispatcher = new ObservingDispatcher(new DefaultToolCallDispatcher(registry), ctx => observed = ctx);

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolCallDispatcher = dispatcher,
        });
        await agent.AskAsync("hi");

        observed!.RunId.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    // ---- helpers ----

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private sealed class ObservingDispatcher(IToolCallDispatcher inner, Action<AgentContext> onDispatch) : IToolCallDispatcher
    {
        public ValueTask<ToolCallOutcome> DispatchAsync(ToolCallRequest request, AgentContext context, CancellationToken cancellationToken = default)
        {
            onDispatch(context);
            return inner.DispatchAsync(request, context, cancellationToken);
        }
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
