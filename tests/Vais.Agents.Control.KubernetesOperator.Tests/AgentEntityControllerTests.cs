// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vais.Agents;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class AgentEntityControllerTests
{
    private static readonly DateTimeOffset ClockStart = new(2026, 4, 20, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeletionTimestamp_Set_ReturnsSuccess_WithoutRuntimeCall()
    {
        var entity = NewEntity();
        entity.Metadata.DeletionTimestamp = ClockStart.UtcDateTime;

        var (controller, ctx) = BuildController();

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().BeNull();
        ctx.ControlPlane.CreatedManifests.Should().BeEmpty();
        ctx.ControlPlane.UpdatedHandles.Should().BeEmpty();
        ctx.ControlPlane.EvictedHandles.Should().BeEmpty();
    }

    [Fact]
    public async Task NewCr_CallsCreateAsync_StoresHandle_WithIdempotencyKey()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.CreateResponse = new AgentHandle("chat", "v1");

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        ctx.ControlPlane.CreatedManifests.Should().HaveCount(1);
        ctx.ControlPlane.CreatedManifests[0].manifest.Id.Should().Be("chat");
        ctx.ControlPlane.CreatedManifests[0].idempotencyKey.Should().Be("uid-1:7:create");
        entity.Status!.AgentHandle!.AgentId.Should().Be("chat");
        entity.Status.ManifestRevision.Should().StartWith("sha256:");
        entity.Status.Phase.Should().Be(AgentPhase.Active);
        entity.Status.ObservedGeneration.Should().Be(7);
        entity.Status.Conditions!.Should().HaveCount(3);
        entity.Status.Conditions!.Should().OnlyContain(c => c.Status == AgentConditions.StatusTrue);
    }

    [Fact]
    public async Task HashMatches_DoesNotCallRuntime_RefreshesLastReconciledAt()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();

        // First pass — persists handle + revision.
        ctx.ControlPlane.CreateResponse = new AgentHandle("chat", "v1");
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);
        var originalRevision = entity.Status!.ManifestRevision;
        var callsAfterFirst = ctx.ControlPlane.CreatedManifests.Count + ctx.ControlPlane.UpdatedHandles.Count;

        // Second pass — same spec → touch only.
        ctx.Clock.Advance(TimeSpan.FromMinutes(5));
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        (ctx.ControlPlane.CreatedManifests.Count + ctx.ControlPlane.UpdatedHandles.Count)
            .Should().Be(callsAfterFirst);
        entity.Status!.ManifestRevision.Should().Be(originalRevision);
    }

    [Fact]
    public async Task HashDiffers_CallsUpdateAsync_StoresNewHandle()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.CreateResponse = new AgentHandle("chat", "v1");

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        // Mutate spec and bump generation — simulates `kubectl apply` of a new version.
        entity.Spec.Version = "v2";
        entity.Metadata.Generation = 8;
        ctx.ControlPlane.UpdateResponse = new AgentHandle("chat", "v2");

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        ctx.ControlPlane.UpdatedHandles.Should().HaveCount(1);
        ctx.ControlPlane.UpdatedHandles[0].idempotencyKey.Should().Be("uid-1:8:update");
        ctx.ControlPlane.UpdatedHandles[0].agentId.Should().Be("chat");
        ctx.ControlPlane.UpdatedHandles[0].version.Should().Be("v1");
        entity.Status!.AgentHandle!.Version.Should().Be("v2");
        entity.Status.ObservedGeneration.Should().Be(8);
    }

    [Fact]
    public async Task SecretRefs_ResolvedBeforeCreate_SuccessPath()
    {
        var entity = NewEntity();
        entity.Spec.SecretRefs = new Dictionary<string, SecretKeyReference>
        {
            ["OPENAI_API_KEY"] = new("openai-creds", "apiKey"),
        };
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.CreateResponse = new AgentHandle("chat", "v1");
        ctx.SecretResolver.SeededValues["OPENAI_API_KEY"] = "sk-test";

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        ctx.SecretResolver.ResolveCalls.Should().HaveCount(1);
        ctx.ControlPlane.CreatedManifests.Should().HaveCount(1);
        entity.Status!.Phase.Should().Be(AgentPhase.Active);
    }

    [Fact]
    public async Task SecretRefs_MissingSecret_SetsManifestInvalidCondition_RequestsRequeue()
    {
        var entity = NewEntity();
        entity.Spec.SecretRefs = new Dictionary<string, SecretKeyReference>
        {
            ["OPENAI_API_KEY"] = new("missing", "apiKey"),
        };
        var (controller, ctx) = BuildController();
        ctx.SecretResolver.ThrowOnResolve = new SecretResolutionException(
            "OPENAI_API_KEY",
            entity.Spec.SecretRefs["OPENAI_API_KEY"],
            "secret not found in namespace");

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        ctx.ControlPlane.CreatedManifests.Should().BeEmpty();
        result.RequeueAfter.Should().NotBeNull();
        entity.Status!.Phase.Should().Be(AgentPhase.Error);
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.ManifestValidType && c.Status == AgentConditions.StatusFalse);
    }

    [Fact]
    public async Task CreateAsyncThrows_SetsErrorPhase_ReturnsFailureWithBackoff()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.ThrowOnCreate = new AgentManifestValidationException(new[] { "bad field" });

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().NotBeNull();
        entity.Status!.Phase.Should().Be(AgentPhase.Error);
        entity.Status.LastError.Should().Contain("bad field");
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.ReadyType && c.Status == AgentConditions.StatusFalse);
    }

    [Fact]
    public async Task DeletedAsync_ReturnsSuccessImmediately()
    {
        var entity = NewEntity();
        var (controller, _) = BuildController();
        var result = await controller.DeletedAsync(entity, CancellationToken.None);
        result.Entity.Should().BeSameAs(entity);
        result.RequeueAfter.Should().BeNull();
    }

    private static AgentEntity NewEntity()
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
                Generation = 7,
            },
            Spec = new AgentSpec
            {
                AgentId = "chat",
                Version = "v1",
                Handler = new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
                Protocols = new List<ProtocolBinding> { new("Http") },
                Tools = new List<ToolRef>(),
            },
        };
    }

    private static (AgentEntityController controller, ControllerTestContext ctx) BuildController()
    {
        var ctx = new ControllerTestContext();
        var options = new StubOptionsMonitor(new KubernetesOperatorOptions
        {
            ControlPlaneBaseUrl = new Uri("https://runtime.local"),
            ReconcileBackoffInitial = TimeSpan.FromSeconds(5),
        });
        var controller = new AgentEntityController(
            ctx.ControlPlane,
            ctx.KubeClient,
            ctx.SecretResolver,
            options,
            ctx.Clock,
            NullLogger<AgentEntityController>.Instance);
        return (controller, ctx);
    }

    private sealed class ControllerTestContext
    {
        public FakeAgentControlPlaneClient ControlPlane { get; } = new();
        public FakeKubernetesClient KubeClient { get; } = new();
        public FakeSecretResolver SecretResolver { get; } = new();
        public TestClock Clock { get; } = new(ClockStart);
    }

    internal sealed class FakeAgentControlPlaneClient : IAgentControlPlaneClient
    {
        public List<(AgentManifest manifest, string? idempotencyKey)> CreatedManifests { get; } = new();
        public List<(string agentId, string? version, string? idempotencyKey)> UpdatedHandles { get; } = new();
        public List<(string agentId, string? version)> EvictedHandles { get; } = new();
        public AgentHandle CreateResponse { get; set; } = new("unset", "v0");
        public AgentHandle UpdateResponse { get; set; } = new("unset", "v0");
        public Exception? ThrowOnCreate { get; set; }
        public Exception? ThrowOnUpdate { get; set; }
        public Exception? ThrowOnEvict { get; set; }

        public Task<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
            => CreateAsync(manifest, idempotencyKey: null, cancellationToken);

        public Task<AgentHandle> CreateAsync(AgentManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        {
            if (ThrowOnCreate is not null) throw ThrowOnCreate;
            CreatedManifests.Add((manifest, idempotencyKey));
            return Task.FromResult(CreateResponse);
        }

        public Task<IReadOnlyList<AgentManifest>> ListAsync(string? labelPrefix = null, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentManifest>>(Array.Empty<AgentManifest>());

        public Task<AgentQueryResponse?> QueryAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentQueryResponse?>(null);

        public Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version = null, CancellationToken cancellationToken = default)
            => UpdateAsync(agentId, newManifest, version, idempotencyKey: null, cancellationToken);

        public Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        {
            if (ThrowOnUpdate is not null) throw ThrowOnUpdate;
            UpdatedHandles.Add((agentId, version, idempotencyKey));
            return Task.FromResult(UpdateResponse);
        }

        public Task CancelAsync(string agentId, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EvictAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
            => EvictAsync(agentId, version, idempotencyKey: null, cancellationToken);

        public Task EvictAsync(string agentId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        {
            if (ThrowOnEvict is not null) throw ThrowOnEvict;
            EvictedHandles.Add((agentId, version));
            return Task.CompletedTask;
        }

        public Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentInvocationResult("unused"));

        public Task SignalAsync(string agentId, AgentSignal signal, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        // Graph stubs — not exercised by operator tests; DIM requires explicit impl for non-default overloads.
        public Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(new AgentGraphHandle(manifest.Id, manifest.Version));
        public Task<AgentGraphListResponse> ListGraphsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new AgentGraphListResponse([]));
        public Task<AgentGraphQueryResponse?> QueryGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult<AgentGraphQueryResponse?>(null);
        public Task<AgentGraphHandle> UpdateGraphAsync(string graphId, AgentGraphManifest newManifest, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new AgentGraphHandle(graphId, newManifest.Version));
        public Task EvictGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GraphInvocationResult> InvokeGraphAsync(string graphId, GraphInvocationRequest request, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new GraphInvocationResult(request.RunId ?? "run-1", new Dictionary<string, System.Text.Json.JsonElement>(), IsComplete: true));
        public Task<GraphInvocationResult> ResumeGraphAsync(string graphId, string runId, GraphResumeRequest request, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new GraphInvocationResult(runId, new Dictionary<string, System.Text.Json.JsonElement>(), IsComplete: true));
        public Task CancelGraphRunAsync(string graphId, string runId, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class FakeKubernetesClient : IAgentEntityKubernetesClient
    {
        public List<AgentEntity> StatusUpdates { get; } = new();

        public Task UpdateStatusAsync(AgentEntity entity, CancellationToken cancellationToken)
        {
            StatusUpdates.Add(entity);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeSecretResolver : IKubernetesSecretResolver
    {
        public ConcurrentDictionary<string, string> SeededValues { get; } = new();
        public List<(string ns, IReadOnlyDictionary<string, SecretKeyReference> refs)> ResolveCalls { get; } = new();
        public SecretResolutionException? ThrowOnResolve { get; set; }

        public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
            string namespaceName,
            IReadOnlyDictionary<string, SecretKeyReference> refs,
            CancellationToken cancellationToken)
        {
            if (ThrowOnResolve is not null) throw ThrowOnResolve;
            ResolveCalls.Add((namespaceName, refs));
            var map = new Dictionary<string, string>(SeededValues);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(map);
        }
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
