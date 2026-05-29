// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// SR-2 — <c>spec.codeMode</c> survives the serialize→parse round-trip (the recurring
/// "manifest field silently dropped on parse" gap), is absent by default, and rejects
/// unsupported runtime/generator values when enabled.
/// </summary>
public sealed class CodeModeManifestTests
{
    private static readonly JsonAgentManifestLoader Loader = new();

    [Fact]
    public async Task CodeMode_SurvivesCodecRoundTrip()
    {
        var original = new AgentManifest(
            Id: "order-analyst",
            Version: "1.0",
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [new ProtocolBinding("Http")],
            Tools: [new ToolRef("crm"), new ToolRef("warehouse")])
        {
            CodeMode = new CodeModeSpec
            {
                Enabled = true,
                Toolset = ["crm", "warehouse"],
                Limits = new CodeModeLimits { TimeoutMs = 3000, MaxToolCalls = 8 },
            },
        };

        var wire = EnvelopeCodec.Serialize(original, "Agent");
        var parsed = (await Loader.LoadFromStringAsync(wire)).Single();

        parsed.CodeMode.Should().NotBeNull();
        parsed.CodeMode!.Enabled.Should().BeTrue();
        parsed.CodeMode.Runtime.Should().Be("jint");
        parsed.CodeMode.Generator.Should().Be("raw");
        parsed.CodeMode.Toolset.Should().Equal("crm", "warehouse");
        parsed.CodeMode.Limits.Should().Be(new CodeModeLimits { TimeoutMs = 3000, MaxToolCalls = 8 });
    }

    [Fact]
    public async Task CodeMode_AbsentByDefault()
    {
        var json = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "plain", "version": "1.0" },
          "spec": { "model": { "provider": "openai", "id": "gpt-4o-mini" } }
        }
        """;

        var parsed = (await Loader.LoadFromStringAsync(json)).Single();

        parsed.CodeMode.Should().BeNull();
    }

    [Fact]
    public async Task CodeMode_EnabledWithUnsupportedRuntime_IsRejected()
    {
        var json = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "bad-runtime", "version": "1.0" },
          "spec": { "codeMode": { "enabled": true, "runtime": "deno" } }
        }
        """;

        var act = async () => await Loader.LoadFromStringAsync(json);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Message.Should().Contain("codeMode.runtime");
    }
}
