// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// Translator guard-rails for v0.17 PR 1. Covers every branch on the
/// translator's decision tree: manifest presence, Model presence, SystemPromptSpec
/// shapes (inline / templateRef / fileRef / ambiguous), ToolRef source prefixes
/// (static / mcp / a2a / unknown), guardrail factory lookups, cache semantics,
/// and InvalidateAsync isolation.
/// </summary>
public class AgentManifestTranslatorTests
{
    private const string AgentId = "weather";
    private const string Version = "1.0";

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_Happy_Path_Populates_Every_Option_Slot()
    {
        var manifest = BuildManifest(
            systemPrompt: new SystemPromptSpec(Inline: "You are helpful."),
            tools: new[] { new ToolRef("weather", "static:weather") },
            guardrails: new GuardrailsSpec(
                Input: new[] { new GuardrailRef("length-cap") },
                Output: new[] { new GuardrailRef("no-pii") }),
            budget: new RunBudget(MaxTurns: 5, MaxDuration: TimeSpan.FromSeconds(30)));

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithStaticTool("weather", new NoopTool("weather"))
            .WithGuardrailFactory("length-cap", GuardrailLayer.Input, new NoopInputGuardrail())
            .WithGuardrailFactory("no-pii", GuardrailLayer.Output, new NoopOutputGuardrail());

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.AgentName.Should().Be(AgentId);
        options.SystemPrompt.Should().Be("You are helpful.");
        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Should().HaveCount(1);
        options.ToolRegistry.Tools[0].Name.Should().Be("weather");
        options.InputGuardrails.Should().HaveCount(1);
        options.OutputGuardrails.Should().HaveCount(1);
        options.ToolGuardrails.Should().BeEmpty();
        options.Budget.Should().Be(new RunBudget(MaxTurns: 5, MaxDuration: TimeSpan.FromSeconds(30)));
    }

    // ── Manifest presence / handler routing ────────────────────────────

    [Fact]
    public async Task TranslateAsync_Registry_Miss_Throws_AgentNotFound()
    {
        var fixture = new TranslatorFixture().WithProvider("openai");   // no manifest registered

        var act = async () => await fixture.Translator.TranslateAsync("ghost");

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.AgentNotFound);
    }

    [Fact]
    public async Task TranslateAsync_Null_Model_Throws_HandlerNotLoaded()
    {
        var manifest = BuildManifest(model: null, systemPrompt: null);
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.HandlerNotLoaded);
    }

    [Fact]
    public async Task TranslateAsync_Unknown_Provider_Throws_ModelProviderUnsupported()
    {
        var manifest = BuildManifest(
            model: new ModelSpec(Provider: "bedrock", Id: "anthropic.claude-v2"),
            systemPrompt: new SystemPromptSpec(Inline: "hi"));

        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.ModelProviderUnsupported);
    }

    // ── Cache / invalidation ───────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_Second_Call_Returns_Cached_Instance()
    {
        var manifest = BuildManifest(systemPrompt: new SystemPromptSpec(Inline: "cached"));
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var first = await fixture.Translator.TranslateAsync(AgentId);
        var second = await fixture.Translator.TranslateAsync(AgentId);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task InvalidateAsync_Drops_Only_Target_Id()
    {
        var manifestA = BuildManifest(id: "a", systemPrompt: new SystemPromptSpec(Inline: "a"));
        var manifestB = BuildManifest(id: "b", systemPrompt: new SystemPromptSpec(Inline: "b"));

        var fixture = new TranslatorFixture()
            .WithManifest(manifestA)
            .WithManifest(manifestB)
            .WithProvider("openai");

        var a1 = await fixture.Translator.TranslateAsync("a");
        var b1 = await fixture.Translator.TranslateAsync("b");

        var dropped = await fixture.Translator.InvalidateAsync("a");
        dropped.Should().BeTrue();

        var a2 = await fixture.Translator.TranslateAsync("a");
        var b2 = await fixture.Translator.TranslateAsync("b");

        a2.Should().NotBeSameAs(a1, because: "cache was invalidated for 'a'");
        b2.Should().BeSameAs(b1, because: "cache entry for 'b' was never touched");
    }

    [Fact]
    public async Task InvalidateAsync_Unknown_Id_Returns_False()
    {
        var fixture = new TranslatorFixture().WithProvider("openai");

        var dropped = await fixture.Translator.InvalidateAsync("ghost");

        dropped.Should().BeFalse();
    }

    // ── SystemPromptSpec shapes ────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_SystemPrompt_Inline_With_Variables_Substitutes()
    {
        var manifest = BuildManifest(systemPrompt: new SystemPromptSpec(
            Inline: "You help {{role}} users at {{company}}.",
            Variables: new Dictionary<string, string>
            {
                ["role"] = "premium",
                ["company"] = "Contoso",
            }));

        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.SystemPrompt.Should().Be("You help premium users at Contoso.");
    }

    [Fact]
    public async Task TranslateAsync_SystemPrompt_TemplateRef_Resolves_Via_Registry()
    {
        var manifest = BuildManifest(systemPrompt: new SystemPromptSpec(TemplateRef: "triage-intro"));
        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithPromptTemplate("triage-intro", "Triage the following request.");

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.SystemPrompt.Should().Be("Triage the following request.");
    }

    [Fact]
    public async Task TranslateAsync_SystemPrompt_TemplateRef_Not_Registered_Throws()
    {
        var manifest = BuildManifest(systemPrompt: new SystemPromptSpec(TemplateRef: "ghost"));
        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithPromptTemplate("other", "...");

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.PromptTemplateNotRegistered);
    }

    [Fact]
    public async Task TranslateAsync_SystemPrompt_FileRef_Resolves_Via_Loader()
    {
        var manifest = BuildManifest(systemPrompt: new SystemPromptSpec(FileRef: "triage.prompt"));
        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithPromptFile("triage.prompt", "Loaded from disk.");

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.SystemPrompt.Should().Be("Loaded from disk.");
    }

    [Fact]
    public async Task TranslateAsync_SystemPrompt_Ambiguous_Throws()
    {
        var manifest = BuildManifest(systemPrompt: new SystemPromptSpec(
            Inline: "hi",
            TemplateRef: "also-this"));
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.PromptSpecAmbiguous);
    }

    // ── ToolRef source prefixes ────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_Tool_Static_Resolves_Via_Registry()
    {
        var manifest = BuildManifest(
            tools: new[] { new ToolRef("weather", "static:weather") },
            systemPrompt: new SystemPromptSpec(Inline: "hi"));

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithProvider("openai")
            .WithStaticTool("weather", new NoopTool("weather"));

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry!.Tools.Should().ContainSingle().Which.Name.Should().Be("weather");
    }

    [Fact]
    public async Task TranslateAsync_Tool_Static_Unknown_Throws()
    {
        var manifest = BuildManifest(
            tools: new[] { new ToolRef("ghost", "static:ghost") },
            systemPrompt: new SystemPromptSpec(Inline: "hi"));

        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");
        // No WithStaticTool — resolver absent entirely.

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.ToolNotRegistered);
    }

    [Fact]
    public async Task TranslateAsync_Tool_Mcp_Declared_Does_Not_Throw()
    {
        // PR 1 scope: mcp:* validates declaration only; lazy instantiation lands in PR 3.
        // The tool registry on the returned options is null because no tools get materialised.
        var manifest = BuildManifest(
            tools: new[] { new ToolRef("weather", "mcp:weather-server") },
            mcpServers: new[] { new McpServerRef("weather-server", Transport: "http", Url: "http://example") },
            systemPrompt: new SystemPromptSpec(Inline: "hi"));

        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().BeNull(because: "PR 1 validates but does not materialise mcp:* tools");
    }

    [Fact]
    public async Task TranslateAsync_Tool_Mcp_Undeclared_Throws()
    {
        var manifest = BuildManifest(
            tools: new[] { new ToolRef("ghost", "mcp:ghost") },
            systemPrompt: new SystemPromptSpec(Inline: "hi"));
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.McpServerNotDeclared);
    }

    [Fact]
    public async Task TranslateAsync_Tool_A2A_Throws_With_PR3_Message()
    {
        var manifest = BuildManifest(
            tools: new[] { new ToolRef("tech-agent", "a2a:tech-agent") },
            systemPrompt: new SystemPromptSpec(Inline: "hi"));
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.A2AAgentNotDeclared);
    }

    [Fact]
    public async Task TranslateAsync_Tool_Unknown_Prefix_Throws()
    {
        var manifest = BuildManifest(
            tools: new[] { new ToolRef("weather", "grpc:weather") },
            systemPrompt: new SystemPromptSpec(Inline: "hi"));
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.ToolSourceUnknown);
    }

    // ── Guardrail lookup ───────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_Guardrail_Unknown_Throws()
    {
        var manifest = BuildManifest(
            guardrails: new GuardrailsSpec(Input: new[] { new GuardrailRef("ghost-guard") }),
            systemPrompt: new SystemPromptSpec(Inline: "hi"));
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");
        // No guardrail factory registered.

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.GuardrailNotRegistered);
    }

    // ── Budget + fallbacks ─────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_Budget_Threaded_Through()
    {
        var budget = new RunBudget(MaxTurns: 3, MaxPromptTokens: 8000);
        var manifest = BuildManifest(budget: budget, systemPrompt: new SystemPromptSpec(Inline: "hi"));
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.Budget.Should().Be(budget);
    }

    [Fact]
    public async Task TranslateAsync_Null_SystemPrompt_With_Model_Set_Leaves_Prompt_Null()
    {
        var manifest = BuildManifest(
            model: new ModelSpec(Provider: "openai", Id: "gpt-4o"),
            systemPrompt: null);
        var fixture = new TranslatorFixture().WithManifest(manifest).WithProvider("openai");

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.SystemPrompt.Should().BeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static AgentManifest BuildManifest(
        string id = AgentId,
        ModelSpec? model = null,
        SystemPromptSpec? systemPrompt = null,
        IReadOnlyList<ToolRef>? tools = null,
        IReadOnlyList<McpServerRef>? mcpServers = null,
        GuardrailsSpec? guardrails = null,
        RunBudget? budget = null)
    {
        return new AgentManifest(
            Id: id,
            Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: tools ?? Array.Empty<ToolRef>())
        {
            Model = model ?? (systemPrompt is not null ? new ModelSpec(Provider: "openai", Id: "gpt-4o") : null),
            SystemPrompt = systemPrompt,
            McpServers = mcpServers,
            Guardrails = guardrails,
            Budget = budget,
        };
    }

    private sealed class NoopTool : ITool
    {
        public NoopTool(string name)
        {
            Name = name;
            ParametersSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        }

        public string Name { get; }
        public string Description => "noop";
        public JsonElement ParametersSchema { get; }
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) => Task.FromResult("noop");
    }

    private sealed class NoopInputGuardrail : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken = default)
            => new(GuardrailOutcome.Pass);
    }

    private sealed class NoopOutputGuardrail : IOutputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionResponse response, AgentContext context, CancellationToken cancellationToken = default)
            => new(GuardrailOutcome.Pass);
    }
}
