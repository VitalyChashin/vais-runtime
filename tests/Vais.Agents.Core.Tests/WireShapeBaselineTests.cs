// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// SC-9 closure: documented wire-shape baselines for the section pipeline. Each test names a
/// canonical user-level scenario (persona-only, persona+RAG, composer+contributors, etc.) and
/// asserts the <see cref="CompletionRequest"/> that reaches the completion provider matches the
/// expected v0.4 shape. The bar is structural equality at the <c>ICompletionProvider</c>
/// boundary — JSON-level equality would test the serializer, not the pipeline.
///
/// These tests complement (not duplicate) the per-component tests in
/// <see cref="CompletionRequestFlattenerTests"/>, <see cref="DefaultSectionResolverTests"/>,
/// and <see cref="SectionPipelineIntegrationTests"/>; they are written at the highest level —
/// what a user wires into <see cref="StatefulAgentOptions"/> produces what the model sees.
/// </summary>
public sealed class WireShapeBaselineTests
{
    private static (StatefulAiAgent agent, Func<CompletionRequest?> captured) Build(StatefulAgentOptions options)
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
    public async Task Baseline_1_Persona_Only()
    {
        // The simplest agent: inline SystemPrompt, no providers, no tools.
        var (agent, captured) = Build(new StatefulAgentOptions { SystemPrompt = "You are a helpful assistant." });

        await agent.AskAsync("hi");

        var request = captured()!;
        request.SystemPrompt.Should().Be("You are a helpful assistant.");
        request.History.Should().ContainSingle().Which.Text.Should().Be("hi");
        request.Tools.Should().BeNull();
        request.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_2_Persona_Plus_Legacy_Rag_Provider()
    {
        // v0.4 RAG pattern: a provider returns a ContextContribution(SystemPromptAddendum: ...).
        // The section pipeline routes this through the legacy back-projection in SC-2.
        // Wire shape: SystemPrompt = persona + "\n\n" + retrieved.
        var provider = new FixedLegacyProvider(new ContextContribution(SystemPromptAddendum: "Source 1: …"));
        var (agent, captured) = Build(new StatefulAgentOptions
        {
            SystemPrompt = "You are a research assistant.",
            ContextProviders = new IContextProvider[] { provider },
        });

        await agent.AskAsync("q");

        captured()!.SystemPrompt.Should().Be("You are a research assistant.\n\nSource 1: …");
    }

    [Fact]
    public async Task Baseline_3_Composer_With_Two_Contributors_Plus_Section_Emitting_Rag()
    {
        // Composer emits per-contributor SystemSegment sections (Order=Priority), a section-aware
        // RAG provider emits a separate retrieval.docs section. The resolver orders all four
        // SystemSegments by (Order, registration index): persona(0) → policy(10) → retrieval
        // (Order=null, registered last). Flattener joins them with "\n\n".
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new FixedContributor(priority: 0, text: "You are a customer-support assistant.", sectionId: "system.persona"),
            new FixedContributor(priority: 10, text: "Always cite tenant policy when relevant.", sectionId: "system.policy"),
        });
        var rag = new FixedSectionProvider(new Section(
            "retrieval.docs", SectionKind.SystemSegment, new TextPayload("Policy excerpt: …"),
            ProducerId: "rag"));

        var (agent, captured) = Build(new StatefulAgentOptions
        {
            SystemPromptComposer = composer,
            ContextProviders = new IContextProvider[] { rag },
        });

        await agent.AskAsync("hi");

        captured()!.SystemPrompt.Should().Be(
            "You are a customer-support assistant.\n\n" +
            "Always cite tenant policy when relevant.\n\n" +
            "Policy excerpt: …");
    }

    [Fact]
    public async Task Baseline_4_Tools_Suppress_ResponseFormat()
    {
        // v0.4 mutual-exclusion rule: when a tool registry has tools, the candidate's ResponseFormat
        // is suppressed (providers may not support both simultaneously). SC-6 preserves this in
        // BuildPerTurnRequestAsync — only one of `tools.base` or `format.base` is emitted per turn.
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        var registry = new SimpleToolRegistry(new[] { new FakeTool("calc", "Math.") });
        var (agent, captured) = Build(new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ResponseFormat = new ResponseFormatSpec(schema),
        });

        await agent.AskAsync("hi");

        var request = captured()!;
        request.Tools.Should().NotBeNull().And.HaveCount(1);
        request.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_5_Response_Format_Without_Tools()
    {
        // No tool registry → ResponseFormat flows through unchanged.
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        var (agent, captured) = Build(new StatefulAgentOptions
        {
            ResponseFormat = new ResponseFormatSpec(schema),
        });

        await agent.AskAsync("hi");

        captured()!.ResponseFormat.Should().NotBeNull();
        captured()!.Tools.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_6_Kitchen_Sink_Everything_Wired()
    {
        // Composer (two contributors) + section-aware retrieval + legacy InjectedHistory + tools.
        // The resolver orders: SystemSegment by Order; turn-kinds interleaved by Order;
        // ToolDeclaration after turns. The legacy InjectedHistory appears AFTER base history
        // because BuildLegacySections sets Order=null on legacy turns, and the resolver's
        // null-falls-back-to-registration-index rule places legacy sections after base history
        // (which has explicit Order=0,1,...).
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new FixedContributor(priority: 0, text: "Persona.", sectionId: "system.persona"),
            new FixedContributor(priority: 10, text: "Policy.", sectionId: "system.policy"),
        });
        var rag = new FixedSectionProvider(new Section(
            "retrieval.docs", SectionKind.SystemSegment, new TextPayload("Retrieved."),
            ProducerId: "rag"));
        var legacyHistoryProvider = new FixedLegacyProvider(new ContextContribution(
            InjectedHistory: new[] { new ChatTurn(AgentChatRole.Assistant, "few-shot example") }));

        var registry = new SimpleToolRegistry(new[] { new FakeTool("calc", "Math.") });

        var (agent, captured) = Build(new StatefulAgentOptions
        {
            SystemPromptComposer = composer,
            ContextProviders = new IContextProvider[] { rag, legacyHistoryProvider },
            ToolRegistry = registry,
        });

        await agent.AskAsync("real question");

        var request = captured()!;
        request.SystemPrompt.Should().Be("Persona.\n\nPolicy.\n\nRetrieved.");
        request.History.Select(t => t.Text).Should().Equal("real question", "few-shot example");
        request.Tools.Should().NotBeNull().And.ContainSingle(t => t.Name == "calc");
        request.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_7_Legacy_Pure_Three_Slot_Contribution_Preserves_v04_Wire_Shape()
    {
        // Regression guard: a provider returning a legacy three-slot contribution must produce
        // identical wire output to what v0.4 would have produced. Specifically:
        //  - SystemPromptAddendum appends after the inline SystemPrompt with "\n\n"
        //  - InjectedHistory appends AFTER session history
        //  - AdditionalTools appends to the candidate's Tools list (legacy semantics)
        var injectedTurn = new ChatTurn(AgentChatRole.Assistant, "summary of prior session");
        var legacyTool = new FakeTool("legacy-calc", "Legacy math.");

        var provider = new FixedLegacyProvider(new ContextContribution(
            SystemPromptAddendum: "Today is 2026-05-16.",
            InjectedHistory: new[] { injectedTurn },
            AdditionalTools: new ITool[] { legacyTool }));

        var (agent, captured) = Build(new StatefulAgentOptions
        {
            SystemPrompt = "You are helpful.",
            ContextProviders = new IContextProvider[] { provider },
        });

        await agent.AskAsync("hi");

        var request = captured()!;
        request.SystemPrompt.Should().Be("You are helpful.\n\nToday is 2026-05-16.");
        request.History.Select(t => t.Text).Should().Equal("hi", "summary of prior session");
        request.Tools.Should().NotBeNull().And.ContainSingle(t => t.Name == "legacy-calc");
    }

    // ─────────────────── Helpers ───────────────────

    private sealed class FixedContributor(int priority, string text, string? sectionId = null) : ISystemPromptContributor
    {
        // Default SectionId via ISystemPromptContributor falls back to `system.fixed-contributor`
        // — fine for one instance, collides on multiple. These tests pass an explicit id per
        // instance (`system.persona`, `system.policy`, …) which is the documented v0.4 pattern
        // for multiple contributors of the same type.
        public int Priority { get; } = priority;
        public string SectionId { get; } = sectionId ?? ISystemPromptContributor.DefaultSectionId(typeof(FixedContributor));

        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(text);
    }

    private sealed class FixedLegacyProvider(ContextContribution contribution) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(ContextInvocationContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(contribution);
    }

    private sealed class FixedSectionProvider(Section section) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(ContextInvocationContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ContextContribution(new[] { section }));
    }

    private sealed class FakeTool(string name, string description) : ITool
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }

    private sealed class SimpleToolRegistry(IReadOnlyList<ITool> tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }
}
