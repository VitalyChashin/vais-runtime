// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// SC-6: verifies the StatefulAiAgent section pipeline (composer + providers → resolver →
/// packer → flattener). The "legacy + new providers behave identically to legacy-only" guarantee
/// is enforced cross-cuttingly by the broader regression suite passing unchanged; these tests
/// focus on the new section-emitting paths the pipeline now supports.
/// </summary>
public sealed class SectionPipelineIntegrationTests
{
    private static StatefulAgentOptions Options(
        IReadOnlyList<IContextProvider>? providers = null,
        ISectionWindowPacker? packer = null,
        ISectionResolver? resolver = null,
        SectionBudgetContext? budget = null,
        IContextWindowPacker? legacyPacker = null,
        string? systemPrompt = null)
        => new()
        {
            SystemPrompt = systemPrompt,
            ContextProviders = providers ?? Array.Empty<IContextProvider>(),
            SectionWindowPacker = packer,
            SectionResolver = resolver,
            SectionBudget = budget,
            ContextWindowPacker = legacyPacker,
        };

    private static (StatefulAiAgent agent, Func<CompletionRequest?> captured) Build(
        StatefulAgentOptions options)
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req =>
        {
            captured = req;
            return new CompletionResponse("ok");
        });
        return (new StatefulAiAgent(provider, options), () => captured);
    }

    [Fact]
    public async Task SectionEmitting_Provider_Contributes_SystemSegment()
    {
        var providers = new IContextProvider[]
        {
            new FakeSectionProvider(sections => sections.Add(
                new Section("retrieval.docs", SectionKind.SystemSegment, new TextPayload("retrieved"), ProducerId: "rag"))),
        };
        var (agent, captured) = Build(Options(providers, systemPrompt: "base"));

        await agent.AskAsync("hi");

        captured()!.SystemPrompt.Should().Be("base\n\nretrieved");
    }

    [Fact]
    public async Task SectionEmitting_Provider_Can_Add_Tools_And_ResponseFormat()
    {
        var calc = new FakeTool("calc", "Math.");
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        var providers = new IContextProvider[]
        {
            new FakeSectionProvider(sections =>
            {
                sections.Add(new Section("tools.calc", SectionKind.ToolDeclaration, new ToolsPayload(new ITool[] { calc }), ProducerId: "tools"));
                sections.Add(new Section("format.response", SectionKind.ResponseFormat, new ResponseFormatPayload(new ResponseFormatSpec(schema)), ProducerId: "manifest"));
            }),
        };
        var (agent, captured) = Build(Options(providers));

        await agent.AskAsync("hi");

        captured()!.Tools.Should().NotBeNull().And.ContainSingle(t => t.Name == "calc");
        captured()!.ResponseFormat.Should().NotBeNull();
    }

    [Fact]
    public async Task SectionEmitting_Provider_Can_Inject_History_Before_Last_User_Turn()
    {
        // Section-emitting provider injects a user turn with Order=0 — sorts before
        // base history (which has Order=0,1,...) under stable tiebreak — actually, base
        // history.base.0 has Order=0 and registers first, so the injected User with Order=0
        // sorts after it (stable). To test ordering precedence we use a negative Order.
        var providers = new IContextProvider[]
        {
            new FakeSectionProvider(sections => sections.Add(new Section(
                "history.preamble",
                SectionKind.UserMessage,
                new TurnPayload(new ChatTurn(AgentChatRole.User, "preamble")),
                Order: -1,
                ProducerId: "preamble"))),
        };
        var (agent, captured) = Build(Options(providers));

        await agent.AskAsync("real-question");

        captured()!.History.Select(t => t.Text).Should().Equal("preamble", "real-question");
    }

    [Fact]
    public async Task SectionEmitting_And_Legacy_Providers_Combine_Predictably()
    {
        // Mixed providers: one legacy SystemPromptAddendum + one new section-emitting RAG.
        // Both contribute SystemSegment sections — composer base goes first (registration order),
        // then provider sections in their order.
        var providers = new IContextProvider[]
        {
            new FakeLegacyProvider(new ContextContribution(SystemPromptAddendum: "legacy-rules")),
            new FakeSectionProvider(sections => sections.Add(
                new Section("retrieval.docs", SectionKind.SystemSegment, new TextPayload("retrieved"), ProducerId: "rag"))),
        };
        var (agent, captured) = Build(Options(providers, systemPrompt: "persona"));

        await agent.AskAsync("hi");

        captured()!.SystemPrompt.Should().Be("persona\n\nlegacy-rules\n\nretrieved");
    }

    [Fact]
    public async Task SectionWindowPacker_Override_Can_Drop_Sections()
    {
        var providers = new IContextProvider[]
        {
            new FakeSectionProvider(sections => sections.Add(new Section(
                "retrieval.docs",
                SectionKind.SystemSegment,
                new TextPayload("rag-text"),  // 8 chars, priority 8 → drops first under pressure
                ProducerId: "rag",
                Budget: new SectionBudget(Priority: 8)))),
        };
        // Budget=10 chars; total without trimming is 4 (system.base="base") + 1 ("q" history) + 8 (RAG) = 13.
        // Greedy shed drops the highest-priority section (RAG, priority 8) first; remaining 5 chars
        // fits the 10-char budget, so the packer stops there. Result: system.base + history survive.
        var options = Options(providers, systemPrompt: "base", budget: new SectionBudgetContext(MaxChars: 10));
        var (agent, captured) = Build(options);

        await agent.AskAsync("q");

        captured()!.SystemPrompt.Should().Be("base");
        captured()!.History.Should().ContainSingle().Which.Text.Should().Be("q");
    }

    [Fact]
    public async Task Custom_SectionResolver_Is_Invoked()
    {
        var resolver = new CountingResolver();
        var (agent, _) = Build(Options(resolver: resolver, systemPrompt: "base"));

        await agent.AskAsync("hi");

        resolver.InvocationCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Legacy_ContextWindowPacker_Is_Wrapped_In_LegacyAdapter()
    {
        var legacy = new CountingLegacyPacker();
        var (agent, captured) = Build(Options(legacyPacker: legacy, systemPrompt: "base"));

        await agent.AskAsync("hi");

        legacy.InvocationCount.Should().BeGreaterThan(0);
        captured()!.SystemPrompt.Should().Be("base");
    }

    [Fact]
    public async Task History_Preserved_End_To_End_Through_Section_Pipeline()
    {
        // Multi-turn history flows through base sections, the resolver, the (identity) packer,
        // and the flattener with order preserved.
        var (agent, captured) = Build(Options(systemPrompt: "base"));

        await agent.AskAsync("first");
        await agent.AskAsync("second");

        captured()!.History.Select(t => t.Text).Should().Equal("first", "ok", "second");
    }

    // ─────────────────── Helpers ───────────────────

    private sealed class FakeSectionProvider(Action<List<Section>> emit) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(ContextInvocationContext context, CancellationToken cancellationToken = default)
        {
            var list = new List<Section>();
            emit(list);
            return ValueTask.FromResult(new ContextContribution(list));
        }
    }

    private sealed class FakeLegacyProvider(ContextContribution contribution) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(ContextInvocationContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(contribution);
    }

    private sealed class CountingResolver : ISectionResolver
    {
        public int InvocationCount { get; private set; }

        public ValueTask<IReadOnlyList<Section>> ResolveAsync(IReadOnlyList<Section> contributed, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return DefaultSectionResolver.Instance.ResolveAsync(contributed, cancellationToken);
        }
    }

    private sealed class CountingLegacyPacker : IContextWindowPacker
    {
        public int InvocationCount { get; private set; }

        public ValueTask<CompletionRequest> PackAsync(CompletionRequest candidate, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult(candidate);
        }
    }

    private sealed class FakeTool(string name, string description) : ITool
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
