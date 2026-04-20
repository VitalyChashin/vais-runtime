// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

/// <summary>
/// v0.13 PR 1 smoke test. Verifies that <see cref="AgentEntity"/> /
/// <see cref="AgentSpec"/> / <see cref="AgentStatus"/> survive a JSON
/// round-trip cleanly — the canonical operation on a K8s CR's wire
/// envelope. Guards against accidental non-JSON-round-trippable shapes
/// (circular refs, no-default-ctor structs, polymorphic fields without
/// a resolver).
/// </summary>
public sealed class AgentEntityJsonRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    [Fact]
    public void AgentSpec_WithRepresentativeFields_RoundTripsThroughJson()
    {
        var spec = new AgentSpec
        {
            AgentId = "chat-assistant",
            Version = "v1",
            Handler = new AgentHandlerRef("Vais.Agents.Samples.ChatAgent", "Vais.Agents.Samples"),
            Protocols = new List<ProtocolBinding>
            {
                new("Http", "/agents/chat"),
                new("A2A"),
            },
            Tools = new List<ToolRef>
            {
                new("weather"),
                new("search", Source: "mcp:google"),
            },
            Description = "A helpful chat assistant.",
            Labels = new Dictionary<string, string> { ["team"] = "agents-platform" },
            AgentMode = AgentMode.ToolCalling,
            SecretRefs = new Dictionary<string, SecretKeyReference>
            {
                ["OPENAI_API_KEY"] = new("openai-creds", "apiKey"),
            },
            PreserveOnDelete = false,
        };

        var json = JsonSerializer.Serialize(spec, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentSpec>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.AgentId.Should().Be(spec.AgentId);
        roundTripped.Version.Should().Be(spec.Version);
        roundTripped.Handler.TypeName.Should().Be(spec.Handler.TypeName);
        roundTripped.Protocols.Should().HaveCount(2);
        roundTripped.Tools.Should().HaveCount(2);
        roundTripped.Tools[1].Source.Should().Be("mcp:google");
        roundTripped.Description.Should().Be(spec.Description);
        roundTripped.Labels!["team"].Should().Be("agents-platform");
        roundTripped.AgentMode.Should().Be(AgentMode.ToolCalling);
        roundTripped.SecretRefs!["OPENAI_API_KEY"].Should().Be(new SecretKeyReference("openai-creds", "apiKey"));
        roundTripped.PreserveOnDelete.Should().BeFalse();
    }

    [Fact]
    public void AgentStatus_WithRepresentativeFields_RoundTripsThroughJson()
    {
        var status = new AgentStatus
        {
            AgentHandle = new AgentHandleRef("chat-assistant", "v1"),
            ManifestRevision = "sha256:abcdef0123456789",
            Phase = AgentPhase.Active,
            LastReconciledAt = DateTimeOffset.Parse("2026-04-20T13:00:00Z"),
            LastError = null,
            Conditions = new List<AgentCondition>
            {
                new("Ready", "True", "ReconcileSucceeded", "Last reconcile finished without error.", DateTimeOffset.Parse("2026-04-20T13:00:00Z"), ObservedGeneration: 3),
                new("Synced", "True", "RuntimeMatchesSpec", "Runtime state matches spec.", DateTimeOffset.Parse("2026-04-20T13:00:00Z"), ObservedGeneration: 3),
                new("ManifestValid", "True", "ValidationPassed", "Manifest accepted by the runtime.", DateTimeOffset.Parse("2026-04-20T13:00:00Z"), ObservedGeneration: 3),
            },
            ObservedGeneration = 3,
        };

        var json = JsonSerializer.Serialize(status, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentStatus>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.AgentHandle.Should().Be(status.AgentHandle);
        roundTripped.ManifestRevision.Should().Be(status.ManifestRevision);
        roundTripped.Phase.Should().Be(AgentPhase.Active);
        roundTripped.LastReconciledAt.Should().Be(status.LastReconciledAt);
        roundTripped.Conditions.Should().HaveCount(3);
        roundTripped.Conditions![0].Type.Should().Be("Ready");
        roundTripped.Conditions[0].Status.Should().Be("True");
        roundTripped.ObservedGeneration.Should().Be(3);
    }

    [Fact]
    public void AgentEntity_Constants_MatchExpectedCrdMetadata()
    {
        AgentEntity.EntityGroup.Should().Be("vais.io");
        AgentEntity.EntityApiVersion.Should().Be("v1alpha1");
        AgentEntity.EntityKind.Should().Be("Agent");
        AgentEntity.EntityPluralName.Should().Be("agents");
        AgentEntity.DeactivateFinalizer.Should().Be("vais.io/agent-deactivate");
        AgentEntity.TenantIdAnnotation.Should().Be("vais.io/tenant-id");
    }
}
