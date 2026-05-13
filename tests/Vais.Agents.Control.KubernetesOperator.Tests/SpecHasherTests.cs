// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class SpecHasherTests
{
    [Fact]
    public void Compute_SameSpec_ProducesSameHash()
    {
        var a = BuildRepresentativeSpec();
        var b = BuildRepresentativeSpec();

        var hashA = SpecHasher.Compute(a);
        var hashB = SpecHasher.Compute(b);

        hashA.Should().Be(hashB);
        hashA.Should().StartWith("sha256:");
    }

    [Fact]
    public void Compute_DifferentAgentId_ProducesDifferentHash()
    {
        var a = BuildRepresentativeSpec();
        var b = BuildRepresentativeSpec();
        b.AgentId = "different";

        SpecHasher.Compute(a).Should().NotBe(SpecHasher.Compute(b));
    }

    [Fact]
    public void Compute_DictionaryKeyOrder_DoesNotAffectHash()
    {
        var a = BuildRepresentativeSpec();
        a.Labels = new Dictionary<string, string> { ["alpha"] = "1", ["beta"] = "2" };

        var b = BuildRepresentativeSpec();
        b.Labels = new Dictionary<string, string> { ["beta"] = "2", ["alpha"] = "1" };

        SpecHasher.Compute(a).Should().Be(SpecHasher.Compute(b));
    }

    [Fact]
    public void Compute_NullVsAbsent_ProducesSameHash()
    {
        var a = BuildRepresentativeSpec();
        a.Description = null;

        var b = BuildRepresentativeSpec();
        b.Description = null;

        SpecHasher.Compute(a).Should().Be(SpecHasher.Compute(b));
    }

    [Fact]
    public void Compute_PreserveOnDeleteFlip_ProducesDifferentHash()
    {
        var a = BuildRepresentativeSpec();
        a.PreserveOnDelete = false;

        var b = BuildRepresentativeSpec();
        b.PreserveOnDelete = true;

        SpecHasher.Compute(a).Should().NotBe(SpecHasher.Compute(b));
    }

    private static AgentSpec BuildRepresentativeSpec() => new()
    {
        AgentId = "chat-assistant",
        Version = "v1",
        Handler = new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
        Protocols = new List<ProtocolBinding> { new("Http") },
        Tools = new List<ToolRef> { new("weather") },
        Description = "A helpful chat assistant.",
        AgentMode = AgentMode.ToolCalling,
    };
}
