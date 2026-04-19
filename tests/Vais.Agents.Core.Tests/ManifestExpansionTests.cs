// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.6 PR 1a: <see cref="AgentManifest"/> gains a dozen optional init-only
/// properties covering the library + reasoning + control-plane layers from the
/// manifest-schema doc. Everything additive; v0.4 ctor usage unchanged.
/// </summary>
public sealed class ManifestExpansionTests
{
    [Fact]
    public void v0_4_Ctor_Usage_Still_Compiles_And_All_New_Fields_Default_To_Null()
    {
        var manifest = new AgentManifest(
            "x", "1",
            new AgentHandlerRef("T"),
            Array.Empty<ProtocolBinding>(),
            Array.Empty<ToolRef>());

        manifest.Model.Should().BeNull();
        manifest.SystemPrompt.Should().BeNull();
        manifest.McpServers.Should().BeNull();
        manifest.Guardrails.Should().BeNull();
        manifest.Handoffs.Should().BeNull();
        manifest.Budget.Should().BeNull();
        manifest.ContextProviders.Should().BeNull();
        manifest.OutputSchema.Should().BeNull();
        manifest.Reasoning.Should().BeNull();
        manifest.Observability.Should().BeNull();
        manifest.Annotations.Should().BeNull();
        manifest.AgentMode.Should().Be(AgentMode.ToolCalling);
    }

    [Fact]
    public void With_Expression_Round_Trips_All_New_Init_Fields()
    {
        var manifest = new AgentManifest("x", "1", new AgentHandlerRef("T"), Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>())
        {
            Model = new ModelSpec("openai", "gpt-4.1", ApiKeyRef: "secret://env/OPENAI_API_KEY", Temperature: 0.2),
            SystemPrompt = new SystemPromptSpec(Inline: "You are helpful."),
            McpServers = new[] { new McpServerRef("fs", "stdio", Command: "mcp-fs") },
            Guardrails = new GuardrailsSpec(
                Input: new[] { new GuardrailRef("pii") },
                Output: new[] { new GuardrailRef("schema") }),
            Handoffs = new[] { new HandoffRef("billing", When: "invoices") },
            Budget = new RunBudget(MaxTurns: 5, MaxDuration: TimeSpan.FromSeconds(30)),
            ContextProviders = new[] { new ContextProviderRef("rag") },
            OutputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            AgentMode = AgentMode.SchemaGuidedToolCalling,
            Reasoning = new ReasoningSpec(ReasoningPattern.Cascade, Schema: JsonDocument.Parse("""{"type":"object"}""").RootElement),
            Observability = new ObservabilitySpec(LangfuseProject: "prod"),
            Annotations = new Dictionary<string, string> { ["owner"] = "support" },
        };

        manifest.Model!.Provider.Should().Be("openai");
        manifest.SystemPrompt!.Inline.Should().Be("You are helpful.");
        manifest.McpServers!.Should().ContainSingle();
        manifest.Guardrails!.Input!.Should().ContainSingle().Which.Name.Should().Be("pii");
        manifest.Handoffs!.Should().ContainSingle().Which.ToAgent.Should().Be("billing");
        manifest.Budget!.MaxTurns.Should().Be(5);
        manifest.ContextProviders!.Should().ContainSingle();
        manifest.OutputSchema!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        manifest.AgentMode.Should().Be(AgentMode.SchemaGuidedToolCalling);
        manifest.Reasoning!.Pattern.Should().Be(ReasoningPattern.Cascade);
        manifest.Observability!.LangfuseProject.Should().Be("prod");
        manifest.Annotations!["owner"].Should().Be("support");
    }

    [Fact]
    public void AgentMode_Has_Three_Values()
    {
        Enum.GetValues<AgentMode>().Should().BeEquivalentTo(new[]
        {
            AgentMode.ToolCalling,
            AgentMode.SchemaGuided,
            AgentMode.SchemaGuidedToolCalling,
        });
    }

    [Fact]
    public void ReasoningPattern_Has_Three_Values()
    {
        Enum.GetValues<ReasoningPattern>().Should().BeEquivalentTo(new[]
        {
            ReasoningPattern.Cascade,
            ReasoningPattern.Routing,
            ReasoningPattern.Cycle,
        });
    }

    [Fact]
    public void IdentityRef_Extended_Fields_Default_Null()
    {
        var id = new IdentityRef(InboundAuth: "oidc");
        id.Credentials.Should().BeNull();
        id.InboundClaims.Should().BeNull();
    }

    [Fact]
    public void IdentityRef_Credentials_List_Round_Trips()
    {
        var id = new IdentityRef(InboundAuth: "oidc")
        {
            Credentials = new[]
            {
                new OutboundCredentialRef("openai-api", "secret://env/OPENAI_API_KEY", "bearer"),
                new OutboundCredentialRef("stripe", "secret://keyvault/prod/stripe", "oauth2ClientCredentials"),
            },
            InboundClaims = new Dictionary<string, string> { ["scope"] = "agent:invoke" },
        };

        id.Credentials.Should().HaveCount(2);
        id.InboundClaims!["scope"].Should().Be("agent:invoke");
    }

    [Fact]
    public void MemoryRef_Extended_Fields_Default_Null()
    {
        var m = new MemoryRef("redis");
        m.Scope.Should().BeNull();
        m.HistoryReducer.Should().BeNull();

        var full = m with { Scope = "session", HistoryReducer = "last-n" };
        full.Scope.Should().Be("session");
        full.HistoryReducer.Should().Be("last-n");
    }

    [Fact]
    public void AutoscalingSpec_Extended_Fields_Default_Null()
    {
        var a = new AutoscalingSpec(MinReplicas: 1, MaxReplicas: 5, Target: "cpu");
        a.TargetValue.Should().BeNull();
        a.IdleTtl.Should().BeNull();

        var full = a with { TargetValue = 0.7, IdleTtl = TimeSpan.FromMinutes(5) };
        full.TargetValue.Should().Be(0.7);
        full.IdleTtl.Should().Be(TimeSpan.FromMinutes(5));
    }
}
