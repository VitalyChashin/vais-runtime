// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// SC-7 / SC-8: composer and contributor migration tests. The contributor's <c>SectionId</c>
/// default convention, the aggregating composer's section-emitting override, and the legacy
/// composer's default <c>ComposeSectionsAsync</c> wrapper are all covered here. End-to-end
/// flow through <see cref="StatefulAiAgent"/> is exercised by the existing
/// <c>SectionPipelineIntegrationTests</c> and <c>PromptTests</c> suites.
/// </summary>
public sealed class SectionEmittingComposerTests
{
    [Fact]
    public void Contributor_SectionId_Default_Is_Kebab_Cased_With_System_Prefix()
    {
        ISystemPromptContributor persona = new PersonaContributor();
        ISystemPromptContributor tenantPolicy = new TenantPolicyContributor();

        persona.SectionId.Should().Be("system.persona-contributor");
        tenantPolicy.SectionId.Should().Be("system.tenant-policy-contributor");
    }

    [Fact]
    public void Contributor_DefaultSectionId_Helper_Reproduces_The_Convention()
    {
        ISystemPromptContributor.DefaultSectionId(typeof(PersonaContributor)).Should().Be("system.persona-contributor");
        ISystemPromptContributor.DefaultSectionId(typeof(TenantPolicyContributor)).Should().Be("system.tenant-policy-contributor");
    }

    [Fact]
    public void Contributor_Override_Wins_Over_Default_SectionId()
    {
        ISystemPromptContributor custom = new CustomIdContributor("system.persona");

        custom.SectionId.Should().Be("system.persona");
    }

    [Fact]
    public async Task Aggregating_Composer_Emits_One_Section_Per_Contributor()
    {
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new PersonaContributor { Priority = 0 },
            new TenantPolicyContributor { Priority = 10 },
        });

        var sections = await composer.ComposeSectionsAsync(new AgentContext());

        sections.Should().HaveCount(2);
        sections[0].Id.Should().Be("system.persona-contributor");
        sections[0].Order.Should().Be(0);
        sections[0].Kind.Should().Be(SectionKind.SystemSegment);
        sections[0].ProducerId.Should().Be(nameof(PersonaContributor));
        sections[0].Payload.Should().BeOfType<TextPayload>().Which.Value.Should().Be("persona-text");

        sections[1].Id.Should().Be("system.tenant-policy-contributor");
        sections[1].Order.Should().Be(10);
        sections[1].ProducerId.Should().Be(nameof(TenantPolicyContributor));
    }

    [Fact]
    public async Task Aggregating_Composer_Skips_Contributors_With_Null_Or_Empty_Output()
    {
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new PersonaContributor(),
            new EmptyContributor(),
            new NullContributor(),
            new TenantPolicyContributor(),
        });

        var sections = await composer.ComposeSectionsAsync(new AgentContext());

        sections.Should().HaveCount(2);
        sections.Select(s => s.Id).Should().Equal("system.persona-contributor", "system.tenant-policy-contributor");
    }

    [Fact]
    public async Task Aggregating_Composer_Empty_Set_Returns_Empty_Sections()
    {
        var composer = new AggregatingSystemPromptComposer(Array.Empty<ISystemPromptContributor>());

        var sections = await composer.ComposeSectionsAsync(new AgentContext());

        sections.Should().BeEmpty();
    }

    [Fact]
    public async Task Custom_Composer_Without_Override_Falls_Back_To_Single_System_Composed_Section()
    {
        // A composer that only implements ComposeAsync — the default interface impl wraps the
        // string as a single `system.composed` section.
        ISystemPromptComposer composer = new StringOnlyComposer("hello");

        var sections = await composer.ComposeSectionsAsync(new AgentContext());

        sections.Should().ContainSingle();
        sections[0].Id.Should().Be("system.composed");
        sections[0].Kind.Should().Be(SectionKind.SystemSegment);
        sections[0].Payload.Should().BeOfType<TextPayload>().Which.Value.Should().Be("hello");
        sections[0].ProducerId.Should().Be(nameof(StringOnlyComposer));
    }

    [Fact]
    public async Task Custom_Composer_Returning_Null_Falls_Back_To_Empty_Section_List()
    {
        ISystemPromptComposer composer = new StringOnlyComposer(null);

        var sections = await composer.ComposeSectionsAsync(new AgentContext());

        sections.Should().BeEmpty();
    }

    [Fact]
    public async Task Aggregating_Composer_Sections_Flow_Through_StatefulAiAgent_As_Separate_Sections()
    {
        // End-to-end: when the aggregating composer is wired to StatefulAiAgent, the pipeline
        // sees multiple SystemSegment sections (one per contributor) instead of one collapsed
        // string. The flattener concatenates them in priority order with "\n\n", producing the
        // same wire-shape as the v0.4 string-concatenation path.
        var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
        {
            new PersonaContributor { Priority = 0 },
            new TenantPolicyContributor { Priority = 10 },
        });

        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { SystemPromptComposer = composer });

        await agent.AskAsync("hi");

        captured!.SystemPrompt.Should().Be("persona-text\n\ntenant-policy-text");
    }

    // ─────────────────── Test fixtures ───────────────────

    private sealed class PersonaContributor : ISystemPromptContributor
    {
        public int Priority { get; init; } = 0;

        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>("persona-text");
    }

    private sealed class TenantPolicyContributor : ISystemPromptContributor
    {
        public int Priority { get; init; } = 10;

        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>("tenant-policy-text");
    }

    private sealed class EmptyContributor : ISystemPromptContributor
    {
        public int Priority => 5;

        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(string.Empty);
    }

    private sealed class NullContributor : ISystemPromptContributor
    {
        public int Priority => 6;

        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class CustomIdContributor(string sectionId) : ISystemPromptContributor
    {
        public int Priority => 0;
        public string SectionId { get; } = sectionId;

        public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>("text");
    }

    private sealed class StringOnlyComposer(string? text) : ISystemPromptComposer
    {
        public ValueTask<string?> ComposeAsync(AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(text);
    }
}
