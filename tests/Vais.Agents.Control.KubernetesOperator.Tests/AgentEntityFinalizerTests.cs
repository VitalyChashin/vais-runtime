// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class AgentEntityFinalizerTests
{
    private static readonly DateTimeOffset ClockStart = new(2026, 4, 20, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreserveOnDeleteTrue_SkipsRuntimeEviction()
    {
        var entity = NewEntityWithHandle(preserveOnDelete: true);
        var (finalizer, kube, controlPlane) = Build();

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().BeNull();
        controlPlane.EvictedHandles.Should().BeEmpty();
        entity.Status!.Phase.Should().Be(AgentPhase.Terminating);
    }

    [Fact]
    public async Task PreserveOnDeleteFalse_CallsEvictAsync()
    {
        var entity = NewEntityWithHandle(preserveOnDelete: false);
        var (finalizer, kube, controlPlane) = Build();

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().BeNull();
        controlPlane.EvictedHandles.Should().ContainSingle();
        controlPlane.EvictedHandles[0].agentId.Should().Be("chat");
        controlPlane.EvictedHandles[0].version.Should().Be("v1");
        entity.Status!.Phase.Should().Be(AgentPhase.Terminating);
    }

    [Fact]
    public async Task EvictAsyncThrows_ReturnsFailureWithBackoff()
    {
        var entity = NewEntityWithHandle(preserveOnDelete: false);
        var (finalizer, _, controlPlane) = Build();
        controlPlane.ThrowOnEvict = new InvalidOperationException("runtime down");

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().NotBeNull();
        result.RequeueAfter!.Value.Should().BePositive();
    }

    [Fact]
    public async Task NoAgentHandle_SkipsEvictAsync()
    {
        var entity = NewEntityWithHandle(preserveOnDelete: false);
        entity.Status!.AgentHandle = null;
        var (finalizer, _, controlPlane) = Build();

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().BeNull();
        controlPlane.EvictedHandles.Should().BeEmpty();
    }

    private static AgentEntity NewEntityWithHandle(bool preserveOnDelete)
    {
        return new AgentEntity
        {
            ApiVersion = "vais.io/v1alpha1",
            Kind = "Agent",
            Metadata = new V1ObjectMeta
            {
                Name = "chat-assistant",
                NamespaceProperty = "default",
                Uid = "uid-1",
                Generation = 3,
            },
            Spec = new AgentSpec
            {
                AgentId = "chat",
                Version = "v1",
                Handler = new AgentHandlerRef("H"),
                Protocols = new List<ProtocolBinding> { new("Http") },
                Tools = new List<ToolRef>(),
                PreserveOnDelete = preserveOnDelete,
            },
            Status = new AgentStatus
            {
                AgentHandle = new AgentHandleRef("chat", "v1"),
                Phase = AgentPhase.Active,
            },
        };
    }

    private static (AgentEntityFinalizer finalizer, AgentEntityControllerTests.FakeKubernetesClient kube, AgentEntityControllerTests.FakeAgentControlPlaneClient controlPlane) Build()
    {
        var kube = new AgentEntityControllerTests.FakeKubernetesClient();
        var controlPlane = new AgentEntityControllerTests.FakeAgentControlPlaneClient();
        var clock = new TestClock(ClockStart);
        var finalizer = new AgentEntityFinalizer(controlPlane, kube, clock, NullLogger<AgentEntityFinalizer>.Instance);
        return (finalizer, kube, controlPlane);
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }
}
