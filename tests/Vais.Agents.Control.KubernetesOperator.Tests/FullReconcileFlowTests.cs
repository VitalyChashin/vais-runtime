// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

/// <summary>
/// v0.13 PR 3 integration-ish test — exercises the full create → update →
/// delete reconcile flow against fake control-plane + K8s clients.
/// Verifies the controller + finalizer compose correctly end-to-end with
/// the idempotency-key + status-write sequence the pillar plan specifies.
/// </summary>
public sealed class FullReconcileFlowTests
{
    [Fact]
    public async Task Create_Update_Delete_Sequence_ExercisesEveryHandle()
    {
        var kube = new AgentEntityControllerTests.FakeKubernetesClient();
        var controlPlane = new AgentEntityControllerTests.FakeAgentControlPlaneClient
        {
            CreateResponse = new AgentHandle("chat", "v1"),
            UpdateResponse = new AgentHandle("chat", "v2"),
        };
        var secretResolver = new AgentEntityControllerTests.FakeSecretResolver();
        var clock = new TestClock(new DateTimeOffset(2026, 4, 20, 15, 0, 0, TimeSpan.Zero));
        var options = new StubOptionsMonitor(new KubernetesOperatorOptions
        {
            ControlPlaneBaseUrl = new Uri("https://runtime.local"),
            ReconcileBackoffInitial = TimeSpan.FromSeconds(5),
        });

        var controller = new AgentEntityController(
            controlPlane,
            kube,
            secretResolver,
            options,
            clock,
            NullLogger<AgentEntityController>.Instance);

        var finalizer = new AgentEntityFinalizer(
            controlPlane,
            kube,
            clock,
            NullLogger<AgentEntityFinalizer>.Instance);

        var entity = new AgentEntity
        {
            ApiVersion = "vais.io/v1alpha1",
            Kind = "Agent",
            Metadata = new V1ObjectMeta
            {
                Name = "chat-assistant",
                NamespaceProperty = "default",
                Uid = "uid-full",
                Generation = 1,
            },
            Spec = new AgentSpec
            {
                AgentId = "chat",
                Version = "v1",
                Handler = new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
                Protocols = new List<ProtocolBinding> { new("Http") },
                Tools = new List<ToolRef> { new("weather") },
            },
        };

        // (1) Initial create → status populated with handle.
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);
        controlPlane.CreatedManifests.Should().HaveCount(1);
        controlPlane.CreatedManifests[0].idempotencyKey.Should().Be("uid-full:1:create");
        entity.Status!.AgentHandle!.Version.Should().Be("v1");
        entity.Status.Phase.Should().Be(AgentPhase.Active);

        // (2) Reconcile with same spec — no runtime call.
        var callsBefore = controlPlane.CreatedManifests.Count + controlPlane.UpdatedHandles.Count;
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);
        (controlPlane.CreatedManifests.Count + controlPlane.UpdatedHandles.Count).Should().Be(callsBefore);

        // (3) Bump spec + generation → UpdateAsync called.
        entity.Spec.Version = "v2";
        entity.Metadata.Generation = 2;
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);
        controlPlane.UpdatedHandles.Should().HaveCount(1);
        controlPlane.UpdatedHandles[0].idempotencyKey.Should().Be("uid-full:2:update");
        entity.Status.AgentHandle.Version.Should().Be("v2");

        // (4) Deletion — finalizer fires EvictAsync.
        entity.Metadata.DeletionTimestamp = clock.GetUtcNow().UtcDateTime;
        _ = await controller.ReconcileAsync(entity, CancellationToken.None); // deferred to finalizer
        var finalizerResult = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        finalizerResult.RequeueAfter.Should().BeNull();
        controlPlane.EvictedHandles.Should().ContainSingle();
        controlPlane.EvictedHandles[0].agentId.Should().Be("chat");
        controlPlane.EvictedHandles[0].version.Should().Be("v2");
        entity.Status.Phase.Should().Be(AgentPhase.Terminating);
    }

    private sealed class StubOptionsMonitor(KubernetesOperatorOptions value) : IOptionsMonitor<KubernetesOperatorOptions>
    {
        public KubernetesOperatorOptions CurrentValue => value;
        public KubernetesOperatorOptions Get(string? name) => value;
        public IDisposable OnChange(Action<KubernetesOperatorOptions, string?> listener) => Noop.Instance;

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
