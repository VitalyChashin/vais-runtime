// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// MS-3-B guard — the per-kind JSON Schemas under <c>contracts/schemas/</c> are generated
/// from the records by <see cref="ManifestJsonSchemaGenerator"/> and must stay in sync
/// (run with <c>VAIS_UPDATE_SCHEMAS=1</c> to regenerate). Structural assertions pin the
/// codec-shaping rules the generator must mirror (AgentGraph state wrapping,
/// <c>[JsonPropertyName]</c> wire names) so they can't silently drift from the codec.
/// </summary>
public sealed class ManifestJsonSchemaTests
{
    public static IEnumerable<object[]> Kinds() =>
    [
        ["Agent", typeof(AgentManifest)],
        ["AgentGraph", typeof(AgentGraphManifest)],
        ["McpServer", typeof(McpServerManifest)],
        ["LlmGatewayConfig", typeof(LlmGatewayConfigManifest)],
        ["McpGatewayConfig", typeof(McpGatewayConfigManifest)],
        ["ContainerPlugin", typeof(ContainerPluginManifest)],
        ["EvalSuite", typeof(EvalSuiteManifest)],
    ];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Schema_IsCheckedIn_AndUpToDate(string kind, Type recordType)
    {
        var generated = ManifestJsonSchemaGenerator.GenerateEnvelopeSchema(recordType, kind);
        var path = SchemaPath(kind);

        if (Environment.GetEnvironmentVariable("VAIS_UPDATE_SCHEMAS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the {kind} schema must be checked in at contracts/schemas/ (run with VAIS_UPDATE_SCHEMAS=1 to regenerate)");
        Normalize(File.ReadAllText(path)).Should().Be(Normalize(generated),
            because: $"the checked-in {kind} schema must match the generator (run with VAIS_UPDATE_SCHEMAS=1 to regenerate)");
    }

    [Fact]
    public void AgentGraphSchema_NestsStateSchemaUnderState()
    {
        var spec = SpecProperties("AgentGraph", typeof(AgentGraphManifest));
        spec.TryGetProperty("state", out var state).Should().BeTrue("AgentGraph wire form nests the schema under spec.state.schema");
        state.GetProperty("properties").TryGetProperty("schema", out _).Should().BeTrue();
        spec.TryGetProperty("stateSchema", out _).Should().BeFalse("the flat record field must not leak into the wire schema");
    }

    [Fact]
    public void AgentSchema_PinsA2aRemoteAgentsWireName()
    {
        var spec = SpecProperties("Agent", typeof(AgentManifest));
        spec.TryGetProperty("a2aRemoteAgents", out _).Should().BeTrue("[JsonPropertyName] wire name must be honored");
    }

    [Fact]
    public void EvalSuiteSchema_UnwrapsNestedSpec()
    {
        var spec = SpecProperties("EvalSuite", typeof(EvalSuiteManifest));
        spec.TryGetProperty("cases", out _).Should().BeTrue("the nested Spec must be unwrapped, not emitted as spec.spec");
        spec.TryGetProperty("spec", out _).Should().BeFalse();
    }

    private static JsonElement SpecProperties(string kind, Type recordType)
    {
        var json = ManifestJsonSchemaGenerator.GenerateEnvelopeSchema(recordType, kind);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("properties").GetProperty("spec").GetProperty("properties").Clone();
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    private static string SchemaPath(string kind) => Path.Combine(AgenticContractsDir(), "schemas", $"{kind}.schema.json");

    private static string AgenticContractsDir([CallerFilePath] string callerPath = "")
    {
        // callerPath = <repo>/agentic/tests/Vais.Agents.Core.Tests/ManifestJsonSchemaTests.cs
        var testsProjDir = Directory.GetParent(callerPath)!;       // Vais.Agents.Core.Tests
        var agenticDir = testsProjDir.Parent!.Parent!;             // tests -> agentic
        return Path.Combine(agenticDir.FullName, "contracts");
    }
}
