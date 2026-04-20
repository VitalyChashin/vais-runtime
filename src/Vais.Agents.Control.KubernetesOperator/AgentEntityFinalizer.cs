// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using Microsoft.Extensions.Logging;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Finalizer that drives runtime eviction when an
/// <see cref="AgentEntity"/> is deleted. KubeOps auto-adds the
/// finalizer identifier (<see cref="AgentEntity.DeactivateFinalizer"/>)
/// on reconcile and auto-removes it after this
/// <see cref="FinalizeAsync"/> returns successfully.
/// </summary>
/// <remarks>
/// Skips the runtime call when
/// <see cref="AgentSpec.PreserveOnDelete"/> is <c>true</c> — operator
/// releases the CR but leaves the agent registered on the runtime for
/// rebuild from a different source.
/// </remarks>
internal sealed class AgentEntityFinalizer(
    IAgentControlPlaneClient controlPlaneClient,
    IAgentEntityKubernetesClient kubernetesClient,
    TimeProvider timeProvider,
    ILogger<AgentEntityFinalizer> logger)
    : IEntityFinalizer<AgentEntity>
{
    private readonly IAgentControlPlaneClient _controlPlaneClient = controlPlaneClient ?? throw new ArgumentNullException(nameof(controlPlaneClient));
    private readonly IAgentEntityKubernetesClient _kubernetesClient = kubernetesClient ?? throw new ArgumentNullException(nameof(kubernetesClient));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<AgentEntityFinalizer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<ReconciliationResult<AgentEntity>> FinalizeAsync(AgentEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = _timeProvider.GetUtcNow();
        entity.Status ??= new AgentStatus();
        entity.Status.Phase = AgentPhase.Terminating;
        entity.Status.LastReconciledAt = now;
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        if (entity.Spec.PreserveOnDelete)
        {
            _logger.LogInformation(
                "AgentEntity {Namespace}/{Name}: preserveOnDelete=true — releasing finalizer without runtime eviction.",
                entity.Metadata.NamespaceProperty,
                entity.Metadata.Name);
            return ReconciliationResult<AgentEntity>.Success(entity);
        }

        if (entity.Status.AgentHandle is null)
        {
            _logger.LogInformation(
                "AgentEntity {Namespace}/{Name}: no agent handle on status — releasing finalizer without runtime call.",
                entity.Metadata.NamespaceProperty,
                entity.Metadata.Name);
            return ReconciliationResult<AgentEntity>.Success(entity);
        }

        _logger.LogInformation(
            "AgentEntity {Namespace}/{Name}: calling EvictAsync on control plane.",
            entity.Metadata.NamespaceProperty,
            entity.Metadata.Name);

        try
        {
            await _controlPlaneClient
                .EvictAsync(entity.Status.AgentHandle.AgentId, entity.Status.AgentHandle.Version, idempotencyKey: null, cancellationToken)
                .ConfigureAwait(false);
            return ReconciliationResult<AgentEntity>.Success(entity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentEntity {Namespace}/{Name}: EvictAsync failed; retrying.", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
            return ReconciliationResult<AgentEntity>.Failure(entity, "EvictAsyncFailed", ex, TimeSpan.FromSeconds(10));
        }
    }
}
