// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// ND-1 / ND-2 guard — the base ontology at <c>contracts/ontology/base-ontology.json</c> is
/// generated from the manifest records and must stay in sync
/// (run with <c>VAIS_UPDATE_ONTOLOGY=1</c> to regenerate). Structural assertions pin the
/// cross-ref edges and required-field constraints so they can't silently drift from the records.
/// </summary>
public sealed class BaseOntologyTests
{
    public static IEnumerable<object[]> Kinds() =>
    [
        ["Agent",             typeof(AgentManifest)],
        ["AgentGraph",        typeof(AgentGraphManifest)],
        ["McpServer",         typeof(McpServerManifest)],
        ["LlmGatewayConfig",  typeof(LlmGatewayConfigManifest)],
        ["McpGatewayConfig",  typeof(McpGatewayConfigManifest)],
        ["ContainerPlugin",   typeof(ContainerPluginManifest)],
        ["EvalSuite",         typeof(EvalSuiteManifest)],
    ];

    [Fact]
    public void Ontology_IsCheckedIn_AndUpToDate()
    {
        var descriptions = XmlDocSummaries.ForAbstractions();
        var rawVersion = typeof(AgentManifest).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        // Strip the SourceLink "+<commit-hash>" suffix so the version is stable across commits.
        var plusIdx = rawVersion.IndexOf('+');
        var version = plusIdx >= 0 ? rawVersion[..plusIdx] : rawVersion;
        var kinds = KindList();
        var generated = ManifestJsonSchemaGenerator.GenerateBaseOntology(kinds, descriptions, version);
        var path = OntologyPath();

        if (Environment.GetEnvironmentVariable("VAIS_UPDATE_ONTOLOGY") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the base ontology must be checked in at contracts/ontology/ (run with VAIS_UPDATE_ONTOLOGY=1 to regenerate)");
        Normalize(File.ReadAllText(path)).Should().Be(Normalize(generated),
            because: "the checked-in ontology must match the generator (run with VAIS_UPDATE_ONTOLOGY=1 to regenerate)");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Ontology_ContainsKind_WithFieldsAndCrossRefs(string kind, Type recordType)
    {
        _ = recordType; // satisfied by kind alone
        var root = ParseOntology();
        root.GetProperty("kinds").TryGetProperty(kind, out var entry)
            .Should().BeTrue($"kind '{kind}' must be present");
        entry.TryGetProperty("fields", out JsonElement fieldsEl)
            .Should().BeTrue($"kind '{kind}' must have a 'fields' object");
        _ = fieldsEl;
        entry.TryGetProperty("crossRefs", out JsonElement crossRefsEl)
            .Should().BeTrue($"kind '{kind}' must have a 'crossRefs' array");
        _ = crossRefsEl;
        entry.TryGetProperty("constraints", out var constraints)
            .Should().BeTrue($"kind '{kind}' must have 'constraints'");
        constraints.TryGetProperty("required", out JsonElement requiredEl)
            .Should().BeTrue($"kind '{kind}' constraints must include 'required'");
        _ = requiredEl;
    }

    [Fact]
    public void Ontology_Agent_HasLlmAndMcpGatewayCrossRefs()
    {
        var crossRefs = GetCrossRefFields("Agent");
        crossRefs.Should().Contain("llmGatewayRef", "Agent.llmGatewayRef → LlmGatewayConfig");
        crossRefs.Should().Contain("mcpGatewayRef", "Agent.mcpGatewayRef → McpGatewayConfig");
    }

    [Fact]
    public void Ontology_McpServer_HasMcpGatewayAndSourcesCrossRefs()
    {
        var crossRefs = GetCrossRefFields("McpServer");
        crossRefs.Should().Contain("mcpGatewayRef", "McpServer.mcpGatewayRef → McpGatewayConfig");
        crossRefs.Should().Contain("sources[].ref", "McpServer.sources[].ref → McpServer (virtual)");
    }

    [Fact]
    public void Ontology_AgentGraph_HasNodeAgentCrossRef()
    {
        var crossRefs = GetCrossRefFields("AgentGraph");
        crossRefs.Should().Contain("nodes[].ref.id", "AgentGraph.nodes[].ref.id → Agent");
    }

    [Fact]
    public void Ontology_AgentGraph_UsesWireStateField()
    {
        // The generator hooks AgentGraph like the schema generator: stateSchema → state (wire form)
        var fields = GetFields("AgentGraph");
        fields.TryGetProperty("state", out _).Should().BeTrue("AgentGraph wire form nests state under spec.state.schema");
        fields.TryGetProperty("stateSchema", out _).Should().BeFalse("flat record field must not leak into the ontology");
    }

    [Fact]
    public void Ontology_CarriesOntologyVersion()
    {
        var root = ParseOntology();
        root.TryGetProperty("ontologyVersion", out var ver).Should().BeTrue();
        ver.GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static IReadOnlyList<(string kind, Type recordType)> KindList() =>
        Kinds().Select(k => ((string)k[0], (Type)k[1])).ToList();

    private static List<string> GetCrossRefFields(string kind)
    {
        var root = ParseOntology();
        return root.GetProperty("kinds").GetProperty(kind).GetProperty("crossRefs")
            .EnumerateArray()
            .Select(r => r.GetProperty("field").GetString()!)
            .ToList();
    }

    private static JsonElement GetFields(string kind)
    {
        var root = ParseOntology();
        var fields = root.GetProperty("kinds").GetProperty(kind).GetProperty("fields").Clone();
        return fields;
    }

    private static JsonElement ParseOntology()
    {
        var generated = ManifestJsonSchemaGenerator.GenerateBaseOntology(
            KindList(), XmlDocSummaries.ForAbstractions());
        using var doc = JsonDocument.Parse(generated);
        return doc.RootElement.Clone();
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");
    private static string OntologyPath() => Path.Combine(RepoContracts.Dir(), "ontology", "base-ontology.json");
}
