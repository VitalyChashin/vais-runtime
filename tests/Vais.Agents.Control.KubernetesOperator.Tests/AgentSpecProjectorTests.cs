// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class AgentSpecProjectorTests
{
    [Fact]
    public void ToManifest_CopiesRequiredFields()
    {
        var spec = new AgentSpec
        {
            AgentId = "chat",
            Version = "v2",
            Handler = new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
            Protocols = new List<ProtocolBinding> { new("Http"), new("A2A") },
            Tools = new List<ToolRef> { new("weather"), new("search") },
        };

        var manifest = AgentSpecProjector.ToManifest(spec);

        manifest.Id.Should().Be("chat");
        manifest.Version.Should().Be("v2");
        manifest.Handler.TypeName.Should().Be("Vais.Agents.Samples.ChatAgent");
        manifest.Protocols.Should().HaveCount(2);
        manifest.Tools.Should().HaveCount(2);
    }

    [Fact]
    public void ToManifest_CopiesOptionalInitProperties()
    {
        var spec = new AgentSpec
        {
            AgentId = "chat",
            Version = "v1",
            Handler = new AgentHandlerRef("H"),
            Protocols = new List<ProtocolBinding> { new("Http") },
            Tools = new List<ToolRef>(),
            Model = new ModelSpec("openai", "gpt-4"),
            SystemPrompt = new SystemPromptSpec { Inline = "You are helpful." },
            Description = "Test.",
            Labels = new Dictionary<string, string> { ["team"] = "agents" },
            Annotations = new Dictionary<string, string> { ["x"] = "y" },
            AgentMode = AgentMode.ToolCalling,
        };

        var manifest = AgentSpecProjector.ToManifest(spec);

        manifest.Model!.Provider.Should().Be("openai");
        manifest.SystemPrompt!.Inline.Should().Be("You are helpful.");
        manifest.Description.Should().Be("Test.");
        manifest.Labels!["team"].Should().Be("agents");
        manifest.Annotations!["x"].Should().Be("y");
    }

    [Fact]
    public void ToManifest_DoesNotInjectSecretRefs_V013Limitation()
    {
        // v0.13 design: operator-side secret resolution is validation-only.
        // SecretRefs on the CR does NOT flow into the projected manifest —
        // runtime resolves via env: / file: URIs inside manifest fields directly.
        var spec = new AgentSpec
        {
            AgentId = "chat",
            Version = "v1",
            Handler = new AgentHandlerRef("H"),
            Protocols = new List<ProtocolBinding> { new("Http") },
            Tools = new List<ToolRef>(),
            SecretRefs = new Dictionary<string, SecretKeyReference>
            {
                ["OPENAI_API_KEY"] = new("openai-creds", "apiKey"),
            },
        };

        var manifest = AgentSpecProjector.ToManifest(spec);

        // No AgentManifest field carries the logical-name → value map.
        // Annotations stay untouched (caller did not set them).
        manifest.Annotations.Should().BeNull();
    }

    [Fact]
    public void ToManifest_NullOptionalFields_StayNull()
    {
        var spec = new AgentSpec
        {
            AgentId = "chat",
            Version = "v1",
            Handler = new AgentHandlerRef("H"),
            Protocols = new List<ProtocolBinding> { new("Http") },
            Tools = new List<ToolRef>(),
        };

        var manifest = AgentSpecProjector.ToManifest(spec);

        manifest.Description.Should().BeNull();
        manifest.Labels.Should().BeNull();
        manifest.Model.Should().BeNull();
        manifest.McpServers.Should().BeNull();
    }
}
