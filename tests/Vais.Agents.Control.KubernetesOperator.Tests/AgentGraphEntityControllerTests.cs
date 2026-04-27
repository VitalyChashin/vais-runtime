// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vais.Agents;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class AgentGraphEntityControllerTests
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
        ctx.ControlPlane.CreatedGraphs.Should().BeEmpty();
        ctx.ControlPlane.UpdatedGraphs.Should().BeEmpty();
        ctx.ControlPlane.EvictedGraphs.Should().BeEmpty();
    }

    [Fact]
    public async Task NewCr_CallsCreateGraphAsync_StoresHandle_WithIdempotencyKey()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.CreateGraphResponse = new AgentGraphHandle("pipeline", "1.0");

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        ctx.ControlPlane.CreatedGraphs.Should().HaveCount(1);
        ctx.ControlPlane.CreatedGraphs[0].manifest.Id.Should().Be("pipeline");
        ctx.ControlPlane.CreatedGraphs[0].idempotencyKey.Should().Be("uid-1:7:create");
        entity.Status!.GraphHandle!.GraphId.Should().Be("pipeline");
        entity.Status.ManifestRevision.Should().StartWith("sha256:");
        entity.Status.Phase.Should().Be(AgentGraphPhase.Active);
        entity.Status.ObservedGeneration.Should().Be(7);
        entity.Status.Conditions!.Should().HaveCount(3);
        entity.Status.Conditions!.Should().OnlyContain(c => c.Status == AgentConditions.StatusTrue);
    }

    [Fact]
    public async Task HashMatches_DoesNotCallRuntime_RefreshesLastReconciledAt()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();

        ctx.ControlPlane.CreateGraphResponse = new AgentGraphHandle("pipeline", "1.0");
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);
        var originalRevision = entity.Status!.ManifestRevision;
        var callsAfterFirst = ctx.ControlPlane.CreatedGraphs.Count + ctx.ControlPlane.UpdatedGraphs.Count;

        ctx.Clock.Advance(TimeSpan.FromMinutes(5));
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        (ctx.ControlPlane.CreatedGraphs.Count + ctx.ControlPlane.UpdatedGraphs.Count)
            .Should().Be(callsAfterFirst);
        entity.Status!.ManifestRevision.Should().Be(originalRevision);
    }

    [Fact]
    public async Task HashDiffers_CallsUpdateGraphAsync_StoresNewHandle()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.CreateGraphResponse = new AgentGraphHandle("pipeline", "1.0");

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        entity.Spec.Version = "2.0";
        entity.Metadata.Generation = 8;
        ctx.ControlPlane.UpdateGraphResponse = new AgentGraphHandle("pipeline", "2.0");

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        ctx.ControlPlane.UpdatedGraphs.Should().HaveCount(1);
        ctx.ControlPlane.UpdatedGraphs[0].idempotencyKey.Should().Be("uid-1:8:update");
        ctx.ControlPlane.UpdatedGraphs[0].graphId.Should().Be("pipeline");
        ctx.ControlPlane.UpdatedGraphs[0].version.Should().Be("1.0");
        entity.Status!.GraphHandle!.Version.Should().Be("2.0");
        entity.Status.ObservedGeneration.Should().Be(8);
    }

    [Fact]
    public async Task CreateAsyncThrows_ManifestInvalid_SetsErrorPhase_ReturnsFailureWithBackoff()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.ThrowOnCreate = new AgentManifestValidationException(new[] { "bad node" });

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().NotBeNull();
        entity.Status!.Phase.Should().Be(AgentGraphPhase.Error);
        entity.Status.LastError.Should().Contain("bad node");
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.ReadyType && c.Status == AgentConditions.StatusFalse);
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.ManifestValidType && c.Status == AgentConditions.StatusFalse);
    }

    [Fact]
    public async Task CreateAsyncThrows_GenericException_SetsErrorPhase_ManifestValidUnknown()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.ThrowOnCreate = new InvalidOperationException("runtime unavailable");

        var result = await controller.ReconcileAsync(entity, CancellationToken.None);

        result.RequeueAfter.Should().NotBeNull();
        entity.Status!.Phase.Should().Be(AgentGraphPhase.Error);
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.ManifestValidType && c.Status == AgentConditions.StatusUnknown);
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

    [Fact]
    public async Task CreateSucceeds_StatusHasThreeConditions_AllTrue()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.CreateGraphResponse = new AgentGraphHandle("pipeline", "1.0");

        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        entity.Status!.Conditions.Should().HaveCount(3);
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.ReadyType && c.Status == AgentConditions.StatusTrue);
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.SyncedType && c.Status == AgentConditions.StatusTrue);
        entity.Status.Conditions.Should().Contain(c => c.Type == AgentConditions.ManifestValidType && c.Status == AgentConditions.StatusTrue);
    }

    [Fact]
    public async Task UpdateSucceeds_StatusUpdatedWithNewHandle()
    {
        var entity = NewEntity();
        var (controller, ctx) = BuildController();
        ctx.ControlPlane.CreateGraphResponse = new AgentGraphHandle("pipeline", "1.0");
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        entity.Spec.Version = "3.0";
        entity.Metadata.Generation = 9;
        ctx.ControlPlane.UpdateGraphResponse = new AgentGraphHandle("pipeline", "3.0");
        _ = await controller.ReconcileAsync(entity, CancellationToken.None);

        entity.Status!.GraphHandle!.Version.Should().Be("3.0");
        entity.Status.Phase.Should().Be(AgentGraphPhase.Active);
        entity.Status.Conditions.Should().OnlyContain(c => c.Status == AgentConditions.StatusTrue);
    }

    private static AgentGraphEntity NewEntity() => new()
    {
        ApiVersion = "vais.io/v1alpha1",
        Kind = "AgentGraph",
        Metadata = new V1ObjectMeta
        {
            Name = "my-pipeline",
            NamespaceProperty = "default",
            Uid = "uid-1",
            Generation = 7,
        },
        Spec = new AgentGraphSpec
        {
            GraphId = "pipeline",
            Version = "1.0",
            Entry = "start",
            Nodes = new List<GraphNode> { new("start", "End") },
            Edges = new List<GraphEdge>(),
        },
    };

    private static (AgentGraphEntityController controller, GraphControllerTestContext ctx) BuildController()
    {
        var ctx = new GraphControllerTestContext();
        var options = new StubOptionsMonitor(new KubernetesOperatorOptions
        {
            ControlPlaneBaseUrl = new Uri("https://runtime.local"),
            ReconcileBackoffInitial = TimeSpan.FromSeconds(5),
        });
        var controller = new AgentGraphEntityController(
            ctx.ControlPlane,
            ctx.KubeClient,
            options,
            ctx.Clock,
            NullLogger<AgentGraphEntityController>.Instance);
        return (controller, ctx);
    }

    private sealed class GraphControllerTestContext
    {
        public FakeGraphControlPlaneClient ControlPlane { get; } = new();
        public FakeGraphKubernetesClient KubeClient { get; } = new();
        public TestClock Clock { get; } = new(ClockStart);
    }

    internal sealed class FakeGraphControlPlaneClient : IAgentControlPlaneClient
    {
        public List<(AgentGraphManifest manifest, string? idempotencyKey)> CreatedGraphs { get; } = new();
        public List<(string graphId, string? version, string? idempotencyKey)> UpdatedGraphs { get; } = new();
        public List<(string graphId, string? version)> EvictedGraphs { get; } = new();
        public AgentGraphHandle CreateGraphResponse { get; set; } = new("unset", "v0");
        public AgentGraphHandle UpdateGraphResponse { get; set; } = new("unset", "v0");
        public Exception? ThrowOnCreate { get; set; }
        public Exception? ThrowOnUpdate { get; set; }
        public Exception? ThrowOnEvict { get; set; }

        // Agent stubs — not exercised by graph operator tests.
        public Task<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(new AgentHandle("unset", "v0"));
        public Task<IReadOnlyList<AgentManifest>> ListAsync(string? labelPrefix = null, int? limit = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentManifest>>(Array.Empty<AgentManifest>());
        public Task<AgentQueryResponse?> QueryAsync(string agentId, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult<AgentQueryResponse?>(null);
        public Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new AgentHandle("unset", "v0"));
        public Task CancelAsync(string agentId, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EvictAsync(string agentId, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new AgentInvocationResult("unused"));
        public Task SignalAsync(string agentId, AgentSignal signal, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        // Graph verbs.
        public Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest manifest, CancellationToken cancellationToken = default)
            => CreateGraphAsync(manifest, idempotencyKey: null, cancellationToken);

        public Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        {
            if (ThrowOnCreate is not null) throw ThrowOnCreate;
            CreatedGraphs.Add((manifest, idempotencyKey));
            return Task.FromResult(CreateGraphResponse);
        }

        public Task<AgentGraphListResponse> ListGraphsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new AgentGraphListResponse([]));
        public Task<AgentGraphQueryResponse?> QueryGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult<AgentGraphQueryResponse?>(null);

        public Task<AgentGraphHandle> UpdateGraphAsync(string graphId, AgentGraphManifest newManifest, string? version = null, CancellationToken cancellationToken = default)
            => UpdateGraphAsync(graphId, newManifest, version, idempotencyKey: null, cancellationToken);

        public Task<AgentGraphHandle> UpdateGraphAsync(string graphId, AgentGraphManifest newManifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        {
            if (ThrowOnUpdate is not null) throw ThrowOnUpdate;
            UpdatedGraphs.Add((graphId, version, idempotencyKey));
            return Task.FromResult(UpdateGraphResponse);
        }

        public Task EvictGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default)
            => EvictGraphAsync(graphId, version, idempotencyKey: null, cancellationToken);

        public Task EvictGraphAsync(string graphId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        {
            if (ThrowOnEvict is not null) throw ThrowOnEvict;
            EvictedGraphs.Add((graphId, version));
            return Task.CompletedTask;
        }

        public Task<GraphInvocationResult> InvokeGraphAsync(string graphId, GraphInvocationRequest request, string? version = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GraphInvocationResult(request.RunId ?? "run-1", new Dictionary<string, System.Text.Json.JsonElement>(), IsComplete: true));

        public Task<GraphInvocationResult> ResumeGraphAsync(string graphId, string runId, GraphResumeRequest request, string? version = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GraphInvocationResult(runId, new Dictionary<string, System.Text.Json.JsonElement>(), IsComplete: true));

        public Task CancelGraphRunAsync(string graphId, string runId, string? version = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Gateway stubs — not exercised by graph operator tests.
        public Task<LlmGatewayConfigHandle> CreateLlmGatewayConfigAsync(LlmGatewayConfigManifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(new LlmGatewayConfigHandle(manifest.Id, manifest.Version));
        public Task<LlmGatewayConfigHandle> UpdateLlmGatewayConfigAsync(string id, LlmGatewayConfigManifest manifest, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new LlmGatewayConfigHandle(id, manifest.Version));
        public Task<LlmGatewayConfigListResponse> ListLlmGatewayConfigsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new LlmGatewayConfigListResponse([]));
        public Task<LlmGatewayConfigQueryResponse?> QueryLlmGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult<LlmGatewayConfigQueryResponse?>(null);
        public Task EvictLlmGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<McpGatewayConfigHandle> CreateMcpGatewayConfigAsync(McpGatewayConfigManifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(new McpGatewayConfigHandle(manifest.Id, manifest.Version));
        public Task<McpGatewayConfigHandle> UpdateMcpGatewayConfigAsync(string id, McpGatewayConfigManifest manifest, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new McpGatewayConfigHandle(id, manifest.Version));
        public Task<McpGatewayConfigListResponse> ListMcpGatewayConfigsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new McpGatewayConfigListResponse([]));
        public Task<McpGatewayConfigQueryResponse?> QueryMcpGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult<McpGatewayConfigQueryResponse?>(null);
        public Task EvictMcpGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<McpServerHandle> CreateMcpServerAsync(McpServerManifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(new McpServerHandle(manifest.Id, manifest.Version));
        public Task<McpServerHandle> UpdateMcpServerAsync(string id, McpServerManifest manifest, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult(new McpServerHandle(id, manifest.Version));
        public Task<McpServerListResponse> ListMcpServersAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) => Task.FromResult(new McpServerListResponse([]));
        public Task<McpServerQueryResponse?> QueryMcpServerAsync(string id, string? version = null, CancellationToken cancellationToken = default) => Task.FromResult<McpServerQueryResponse?>(null);
        public Task EvictMcpServerAsync(string id, string? version = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class FakeGraphKubernetesClient : IAgentGraphEntityKubernetesClient
    {
        public List<AgentGraphEntity> StatusUpdates { get; } = new();

        public Task UpdateStatusAsync(AgentGraphEntity entity, CancellationToken cancellationToken)
        {
            StatusUpdates.Add(entity);
            return Task.CompletedTask;
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
