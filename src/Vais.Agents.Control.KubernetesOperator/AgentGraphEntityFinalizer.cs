// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using Microsoft.Extensions.Logging;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Finalizer that drives runtime eviction when an <see cref="AgentGraphEntity"/> is
/// deleted. KubeOps auto-adds the finalizer identifier
/// (<see cref="AgentGraphEntity.EvictFinalizer"/>) on reconcile and auto-removes it
/// after this <see cref="FinalizeAsync"/> returns successfully.
/// </summary>
/// <remarks>
/// Skips the runtime call when <see cref="AgentGraphSpec.PreserveOnDelete"/> is <c>true</c>
/// — operator releases the CR but leaves the graph registered on the runtime for rebuild
/// from a different source.
/// </remarks>
internal sealed class AgentGraphEntityFinalizer(
    IAgentControlPlaneClient controlPlaneClient,
    IAgentGraphEntityKubernetesClient kubernetesClient,
    TimeProvider timeProvider,
    ILogger<AgentGraphEntityFinalizer> logger)
    : IEntityFinalizer<AgentGraphEntity>
{
    private readonly IAgentControlPlaneClient _controlPlaneClient = controlPlaneClient ?? throw new ArgumentNullException(nameof(controlPlaneClient));
    private readonly IAgentGraphEntityKubernetesClient _kubernetesClient = kubernetesClient ?? throw new ArgumentNullException(nameof(kubernetesClient));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<AgentGraphEntityFinalizer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<ReconciliationResult<AgentGraphEntity>> FinalizeAsync(AgentGraphEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = _timeProvider.GetUtcNow();
        entity.Status ??= new AgentGraphStatus();
        entity.Status.Phase = AgentGraphPhase.Terminating;
        entity.Status.LastReconciledAt = now;
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        if (entity.Spec.PreserveOnDelete)
        {
            _logger.LogInformation(
                "AgentGraphEntity {Namespace}/{Name}: preserveOnDelete=true — releasing finalizer without runtime eviction.",
                entity.Metadata.NamespaceProperty,
                entity.Metadata.Name);
            return ReconciliationResult<AgentGraphEntity>.Success(entity);
        }

        if (entity.Status.GraphHandle is null)
        {
            _logger.LogInformation(
                "AgentGraphEntity {Namespace}/{Name}: no graph handle on status — releasing finalizer without runtime call.",
                entity.Metadata.NamespaceProperty,
                entity.Metadata.Name);
            return ReconciliationResult<AgentGraphEntity>.Success(entity);
        }

        _logger.LogInformation(
            "AgentGraphEntity {Namespace}/{Name}: calling EvictGraphAsync on control plane.",
            entity.Metadata.NamespaceProperty,
            entity.Metadata.Name);

        try
        {
            await _controlPlaneClient
                .EvictGraphAsync(entity.Status.GraphHandle.GraphId, entity.Status.GraphHandle.Version, idempotencyKey: null, cancellationToken)
                .ConfigureAwait(false);
            return ReconciliationResult<AgentGraphEntity>.Success(entity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentGraphEntity {Namespace}/{Name}: EvictGraphAsync failed; retrying.", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
            return ReconciliationResult<AgentGraphEntity>.Failure(entity, "EvictGraphAsyncFailed", ex, TimeSpan.FromSeconds(10));
        }
    }
}
