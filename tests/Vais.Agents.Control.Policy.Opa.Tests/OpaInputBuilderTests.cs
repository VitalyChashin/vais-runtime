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

    [Fact]
    public void Build_ExtensionOverload_SetsExtensionKeyAndNullAgent()
    {
        var ext = SampleExtension();
        var principal = new AgentPrincipal(Id: "u1", TenantId: "t1", Scopes: null);

        var input = OpaInputBuilder.Build(PolicyOperation.ExtensionCreate, ext, principal);

        input["schemaVersion"]!.GetValue<string>().Should().Be("1");
        input["operation"]!.GetValue<string>().Should().Be("ExtensionCreate");
        input["extension"].Should().NotBeNull();
        input["extension"]!["id"]!.GetValue<string>().Should().Be("my-logger");
        input["extension"]!["version"]!.GetValue<string>().Should().Be("1.0.0");
        input["extension"]!["host"]!.GetValue<string>().Should().Be("csharp");
        input.ContainsKey("agent").Should().BeFalse();
    }

    [Fact]
    public void Build_ExtensionOverload_HandlersSerialised()
    {
        var ext = SampleExtension();
        var input = OpaInputBuilder.Build(PolicyOperation.ExtensionUpdate, ext, principal: null);

        var handlers = input["extension"]!["handlers"]!.AsArray();
        handlers.Should().HaveCount(2);
        handlers[0]!["id"]!.GetValue<string>().Should().Be("log-input");
        handlers[0]!["seam"]!.GetValue<string>().Should().Be("agentInput");
        handlers[1]!["seam"]!.GetValue<string>().Should().Be("agentOutput");
    }

    [Fact]
    public void Build_ExtensionOverload_ScopeSerialised()
    {
        var ext = SampleExtension();
        var input = OpaInputBuilder.Build(PolicyOperation.ExtensionCreate, ext, principal: null);

        var scope = input["extension"]!["scope"]!.AsObject();
        scope["workspaces"]!.AsArray().Should().ContainSingle(n => n!.GetValue<string>() == "ws-a");
        scope["agentIds"]!.AsArray().Should().ContainSingle(n => n!.GetValue<string>() == "agent-1");
    }

    [Fact]
    public void Build_ExtensionOverload_NullExtension_EmitsNullOnTheWire()
    {
        var input = OpaInputBuilder.Build(PolicyOperation.ExtensionQuery, extension: null, principal: null);
        var json = input.ToJsonString(OpaInputBuilder.SerializerOptions);
        var parsed = JsonDocument.Parse(json).RootElement;

        parsed.GetProperty("extension").ValueKind.Should().Be(JsonValueKind.Null);
        parsed.GetProperty("operation").GetString().Should().Be("ExtensionQuery");
    }

    [Fact]
    public void Build_ExtensionOverload_LabelsSerialised()
    {
        var ext = SampleExtension();
        var input = OpaInputBuilder.Build(PolicyOperation.ExtensionEvict, ext, principal: null);

        var labels = input["extension"]!["labels"]!.AsObject();
        labels["team"]!.GetValue<string>().Should().Be("platform");
    }

    private static AgentManifest SampleManifest() => new(
        Id: "chat",
        Version: "v1",
        Handler: new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
        Protocols: new[] { new ProtocolBinding("Http") },
        Tools: new[] { new ToolRef("weather") });

    private static ExtensionManifest SampleExtension() => new(
        Id: "my-logger",
        Version: "1.0.0",
        Spec: new ExtensionSpec
        {
            Host = "csharp",
            Handlers = new List<ExtensionHandler>
            {
                new ExtensionHandler { Id = "log-input",  Seam = "agentInput",  Priority = 100, FailureMode = "fail" },
                new ExtensionHandler { Id = "log-output", Seam = "agentOutput", Priority = 100, FailureMode = "fail" },
            },
            Scope = new ExtensionScope(
                Workspaces: new List<string> { "ws-a" },
                AgentIds: new List<string> { "agent-1" },
                Selector: null),
        },
        Labels: new Dictionary<string, string> { ["team"] = "platform" },
        Description: null);
}
