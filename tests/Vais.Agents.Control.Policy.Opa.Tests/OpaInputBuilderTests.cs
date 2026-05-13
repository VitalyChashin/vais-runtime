// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Policy.Opa.Tests;

public sealed class OpaInputBuilderTests
{
    [Fact]
    public void Build_WithManifestAndPrincipal_CarriesAllTopLevelKeys()
    {
        var manifest = SampleManifest();
        var principal = new AgentPrincipal(Id: "u1", TenantId: "tenant-42", Scopes: new[] { "agent:invoke", "agent:query" });

        var input = OpaInputBuilder.Build(PolicyOperation.Invoke, manifest, principal);

        input["schemaVersion"]!.GetValue<string>().Should().Be("1");
        input["operation"]!.GetValue<string>().Should().Be("Invoke");
        input["principal"].Should().NotBeNull();
        input["principal"]!["id"]!.GetValue<string>().Should().Be("u1");
        input["principal"]!["tenantId"]!.GetValue<string>().Should().Be("tenant-42");
        input["principal"]!["scopes"]!.AsArray().Should().HaveCount(2);
        input["agent"].Should().NotBeNull();
        input["agent"]!["id"]!.GetValue<string>().Should().Be("chat");
    }

    [Fact]
    public void Build_NullPrincipal_EmitsNullOnTheWire()
    {
        var input = OpaInputBuilder.Build(PolicyOperation.Create, SampleManifest(), principal: null);
        var json = input.ToJsonString(OpaInputBuilder.SerializerOptions);

        var parsed = JsonDocument.Parse(json).RootElement;
        parsed.GetProperty("principal").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Build_NullManifest_EmitsNullOnTheWire()
    {
        var input = OpaInputBuilder.Build(PolicyOperation.Query, manifest: null, principal: null);
        var json = input.ToJsonString(OpaInputBuilder.SerializerOptions);

        var parsed = JsonDocument.Parse(json).RootElement;
        parsed.GetProperty("agent").ValueKind.Should().Be(JsonValueKind.Null);
        parsed.GetProperty("operation").GetString().Should().Be("Query");
    }

    [Fact]
    public void Build_FullManifest_FieldsPreservedThroughRoundTrip()
    {
        var manifest = SampleManifest();

        var input = OpaInputBuilder.Build(PolicyOperation.Create, manifest, principal: null);
        var json = input.ToJsonString(OpaInputBuilder.SerializerOptions);
        var roundTrip = JsonNode.Parse(json)!.AsObject();

        var agent = roundTrip["agent"]!.AsObject();
        agent["id"]!.GetValue<string>().Should().Be(manifest.Id);
        agent["version"]!.GetValue<string>().Should().Be(manifest.Version);
        agent["handler"]!["typeName"]!.GetValue<string>().Should().Be(manifest.Handler.TypeName);
        agent["tools"]!.AsArray().Should().HaveCount(manifest.Tools.Count);
    }

    private static AgentManifest SampleManifest() => new(
        Id: "chat",
        Version: "v1",
        Handler: new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
        Protocols: new[] { new ProtocolBinding("Http") },
        Tools: new[] { new ToolRef("weather") });
}
