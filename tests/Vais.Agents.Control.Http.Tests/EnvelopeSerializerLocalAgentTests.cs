// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Regression: <see cref="LocalAgentRef.Mode"/> must round-trip through
/// <see cref="EnvelopeSerializer"/> → <see cref="JsonAgentManifestLoader"/> as a
/// string. A previous bug serialised the enum as a number, which the manifest
/// loader's string-only branch silently dropped, defaulting back to
/// <see cref="LocalAgentInvocationMode.Blocking"/>.
/// </summary>
public sealed class EnvelopeSerializerLocalAgentTests
{
    private static AgentManifest CoordinatorWith(LocalAgentInvocationMode mode) =>
        new(
            Id: "coordinator",
            Version: "1.0",
            Handler: new AgentHandlerRef("declarative"),
            Protocols: new[] { new ProtocolBinding("Http") },
            Tools: new[] { new ToolRef("ask_specialist", "agent:specialist") })
        {
            Model = new ModelSpec("openai", "gpt-4o-mini", ApiKeyRef: "secret://env/OPENAI_API_KEY"),
            SystemPrompt = new SystemPromptSpec(Inline: "delegate everything"),
            LocalAgents = new[] { new LocalAgentRef("specialist", AgentId: "specialist-agent", Mode: mode) },
        };

    [Theory]
    [InlineData(LocalAgentInvocationMode.Blocking)]
    [InlineData(LocalAgentInvocationMode.Background)]
    public async Task Serialize_PreservesLocalAgentMode_AcrossRoundTrip(LocalAgentInvocationMode mode)
    {
        var json = EnvelopeSerializer.Serialize(CoordinatorWith(mode));
        var manifests = await new JsonAgentManifestLoader().LoadFromStringAsync(json);

        manifests.Single().LocalAgents!.Single().Mode.Should().Be(mode);
    }
}
