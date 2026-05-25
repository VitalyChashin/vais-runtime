// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Json.Schema;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// MS-3-B correctness gate — the generated JSON Schema must actually accept the codec's
/// output. For each kind: load the canonical example into a record, re-serialize it through
/// <see cref="EnvelopeCodec"/> (the real wire output), and validate that JSON against the
/// generated schema with a JSON Schema validator. This catches any divergence between the
/// schema generator and the codec (e.g. a missed field tripping <c>additionalProperties:false</c>,
/// a wrong type, or a mis-mirrored shape hook).
/// </summary>
public sealed class ManifestSchemaValidationTests
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
    public async Task CodecOutput_ValidatesAgainstGeneratedSchema(string kind, Type recordType)
    {
        var exampleYaml = File.ReadAllText(ExamplePath(kind));
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(exampleYaml);
        var codecJson = Encode(resources.Single());

        var schema = JsonSchema.FromText(ManifestJsonSchemaGenerator.GenerateEnvelopeSchema(recordType, kind));
        using var instanceDoc = JsonDocument.Parse(codecJson);
        var result = schema.Evaluate(instanceDoc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        result.IsValid.Should().BeTrue(
            because: $"codec output for {kind} must validate against the generated schema. Violations:\n{Violations(result)}");
    }

    private static string Encode(ManifestResource resource) => resource switch
    {
        ManifestResource.AgentCase a => EnvelopeCodec.Serialize(a.Manifest, "Agent"),
        ManifestResource.AgentGraphCase g => EnvelopeCodec.Serialize(g.Graph, "AgentGraph"),
        ManifestResource.McpServerCase s => EnvelopeCodec.Serialize(s.Server, "McpServer"),
        ManifestResource.LlmGatewayConfigCase l => EnvelopeCodec.Serialize(l.Config, "LlmGatewayConfig"),
        ManifestResource.McpGatewayConfigCase m => EnvelopeCodec.Serialize(m.Config, "McpGatewayConfig"),
        ManifestResource.ContainerPluginCase p => EnvelopeCodec.Serialize(p.Manifest, "ContainerPlugin"),
        ManifestResource.EvalSuiteCase e => EnvelopeCodec.Serialize(e.Suite, "EvalSuite"),
        _ => throw new NotSupportedException(resource.GetType().Name),
    };

    private static string Violations(EvaluationResults result)
    {
        var lines = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"  {d.InstanceLocation}: {e.Key} = {e.Value}"));
        return string.Join("\n", lines);
    }

    private static string ExamplePath(string kind) =>
        Path.Combine(ContractsDir(), "examples", $"{kind}.example.yaml");

    private static string ContractsDir() => RepoContracts.Dir();
}
