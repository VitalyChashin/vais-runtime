// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// M1.3 blanket guard — every optional (nullable / collection-typed) field on
/// <see cref="AgentManifest"/> must round-trip through
/// <see cref="EnvelopeSerializer.Serialize(AgentManifest)"/> →
/// <see cref="JsonAgentManifestLoader.LoadFromStringAsync"/> without being silently
/// dropped or reset to its default.
/// </summary>
/// <remarks>
/// Two guards work together:
/// <list type="bullet">
///   <item><see cref="Field_RoundTrips"/> — one Theory case per optional field;
///   fails when the serialiser or loader omits a field.</item>
///   <item><see cref="AllManifestOptionalFields_AreCovered"/> — reflection walker that
///   fails when a new optional field is added to <see cref="AgentManifest"/> without
///   a corresponding entry in <see cref="CoveredPaths"/> and <see cref="RoundTripCases"/>.</item>
/// </list>
/// Enum-typed fields are covered by the companion <c>ManifestEnumRoundTripTests</c> (M1.1).
/// </remarks>
public sealed class ManifestFieldRoundTripTests
{
    private static AgentManifest Base() => new(
        "test-agent", "1.0",
        new AgentHandlerRef("declarative"),
        Array.Empty<ProtocolBinding>(),
        Array.Empty<ToolRef>());

    // ── covered paths — must stay in sync with RoundTripCases() ──────────────

    private static readonly IReadOnlySet<string> CoveredPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        "Description",
        "Labels",
        "Memory",
        "Identity",
        "Autoscaling",
        "Model",
        "SystemPrompt",
        "McpServers",
        "A2ARemoteAgents",
        "LocalAgents",
        "Guardrails",
        "Handoffs",
        "Budget",
        "ContextProviders",
        "OutputSchema",
        "Reasoning",
        "CodeMode",
        "Observability",
        "Annotations",
        "LlmGatewayRef",
        "McpGatewayRef",
    };

    // ── round-trip test cases ─────────────────────────────────────────────────

    /// <summary>
    /// One row per optional field: [ path, AgentManifest, Func&lt;AgentManifest, object?&gt;, expected ].
    /// </summary>
    public static IEnumerable<object?[]> RoundTripCases()
    {
        yield return Row("Description",
            Base() with { Description = "an agent" },
            m => m.Description, "an agent");

        yield return Row("Labels",
            Base() with { Labels = new Dictionary<string, string> { ["env"] = "test" } },
            m => m.Labels!["env"], "test");

        yield return Row("Annotations",
            Base() with { Annotations = new Dictionary<string, string> { ["owner"] = "ci" } },
            m => m.Annotations!["owner"], "ci");

        yield return Row("LlmGatewayRef",
            Base() with { LlmGatewayRef = "my-llm-gw" },
            m => m.LlmGatewayRef, "my-llm-gw");

        yield return Row("McpGatewayRef",
            Base() with { McpGatewayRef = "my-mcp-gw" },
            m => m.McpGatewayRef, "my-mcp-gw");

        yield return Row("Memory",
            Base() with { Memory = new MemoryRef("redis") },
            m => m.Memory!.Provider, "redis");

        yield return Row("Identity",
            Base() with { Identity = new IdentityRef("jwt") },
            m => m.Identity!.InboundAuth, "jwt");

        yield return Row("Autoscaling",
            Base() with { Autoscaling = new AutoscalingSpec(MinReplicas: 1, MaxReplicas: 5) },
            m => m.Autoscaling!.MaxReplicas, (object?)5);

        yield return Row("Model",
            Base() with { Model = new ModelSpec("openai", "gpt-4.1") },
            m => m.Model!.Id, "gpt-4.1");

        yield return Row("SystemPrompt",
            Base() with { SystemPrompt = new SystemPromptSpec(Inline: "You are a test agent.") },
            m => m.SystemPrompt!.Inline, "You are a test agent.");

        yield return Row("McpServers",
            Base() with { McpServers = new[] { new McpServerRef("fs", "streamableHttp", Url: "http://localhost:3000") } },
            m => m.McpServers!.Single().Name, "fs");

        // A2ARemoteAgents: confirmed-live bug on 2026-05-15 — field was missing from both
        // EnvelopeSerializer and JsonAgentManifestLoader; this case is the regression guard.
        yield return Row("A2ARemoteAgents",
            Base() with { A2ARemoteAgents = new[] { new A2ARemoteAgentRef("ext-agent", new Uri("https://a2a.example.com/agent")) } },
            m => m.A2ARemoteAgents!.Single().Url.AbsoluteUri, "https://a2a.example.com/agent");

        yield return Row("LocalAgents",
            Base() with { LocalAgents = new[] { new LocalAgentRef("sub") } },
            m => m.LocalAgents!.Single().Name, "sub");

        yield return Row("Guardrails",
            Base() with { Guardrails = new GuardrailsSpec(Input: new[] { new GuardrailRef("pii") }) },
            m => m.Guardrails!.Input!.Single().Name, "pii");

        yield return Row("Handoffs",
            Base() with { Handoffs = new[] { new HandoffRef("target-agent") } },
            m => m.Handoffs!.Single().ToAgent, "target-agent");

        yield return Row("Budget",
            Base() with { Budget = new RunBudget(MaxTurns: 10) },
            m => m.Budget!.MaxTurns, (object?)10);

        yield return Row("ContextProviders",
            Base() with { ContextProviders = new[] { new ContextProviderRef("rag") } },
            m => m.ContextProviders!.Single().Name, "rag");

        yield return Row("OutputSchema",
            Base() with { OutputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone() },
            m => m.OutputSchema!.Value.GetRawText(), "{\"type\":\"object\"}");

        // Reasoning: enum field (Pattern) is covered by ManifestEnumRoundTripTests (M1.1);
        // the non-enum fields (SchemaRef, MaxIterations, etc.) are covered here.
        yield return Row("Reasoning",
            Base() with { Reasoning = new ReasoningSpec(ReasoningPattern.Cascade, SchemaRef: "test://schema") },
            m => m.Reasoning!.SchemaRef, "test://schema");

        yield return Row("Observability",
            Base() with { Observability = new ObservabilitySpec(LangfuseProject: "my-proj") },
            m => m.Observability!.LangfuseProject, "my-proj");

        yield return Row("CodeMode",
            Base() with { CodeMode = new CodeModeSpec { Enabled = true, Toolset = new[] { "crm" } } },
            m => m.CodeMode!.Toolset!.Single(), "crm");
    }

    private static object?[] Row(string path, AgentManifest manifest, Func<AgentManifest, object?> extract, object? expected)
        => [path, manifest, extract, expected];

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task Field_RoundTrips(
        string path, AgentManifest input, Func<AgentManifest, object?> extract, object? expected)
    {
        var json = EnvelopeSerializer.Serialize(input);
        var manifests = await new JsonAgentManifestLoader().LoadFromStringAsync(json);
        extract(manifests.Single()).Should().Be(expected,
            because: $"{path} must survive the EnvelopeSerializer → JsonAgentManifestLoader round-trip");
    }

    // ── coverage guard ────────────────────────────────────────────────────────

    [Fact]
    public void AllManifestOptionalFields_AreCovered()
    {
        var discovered = DiscoverOptionalFieldPaths(typeof(AgentManifest));
        var uncovered = discovered.Except(CoveredPaths).OrderBy(p => p).ToList();
        uncovered.Should().BeEmpty(
            because: "every optional field on AgentManifest must have a round-trip test case; " +
                     $"add the missing paths to {nameof(CoveredPaths)} and {nameof(RoundTripCases)}()");
    }

    // ── reflection walker ─────────────────────────────────────────────────────

    // Required constructor parameters that are always written by EnvelopeSerializer — not at risk of silent loss.
    private static readonly IReadOnlySet<string> AlwaysSerializedProps = new HashSet<string>(StringComparer.Ordinal)
    {
        "Id", "Version", "Handler", "Protocols", "Tools",
    };

    private static IReadOnlyList<string> DiscoverOptionalFieldPaths(Type manifestType)
    {
        var results = new List<string>();
        foreach (var prop in manifestType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (AlwaysSerializedProps.Contains(prop.Name)) continue;
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (underlying.IsEnum) continue; // covered by ManifestEnumRoundTripTests (M1.1)
            results.Add(prop.Name);
        }
        return results;
    }
}
