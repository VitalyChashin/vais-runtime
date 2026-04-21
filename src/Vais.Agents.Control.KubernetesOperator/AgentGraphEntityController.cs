// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Reconciles <see cref="AgentGraphEntity"/> custom resources against the v0.19
/// HTTP control plane. Implements the 6-row reconcile decision table:
/// <list type="number">
///   <item><description>New CR → <c>CreateGraphAsync</c> → store handle on status.</description></item>
///   <item><description>CR with handle, spec-hash matches → refresh <c>LastReconciledAt</c>.</description></item>
///   <item><description>CR with handle, spec-hash differs → <c>UpdateGraphAsync</c> → store new handle.</description></item>
///   <item><description>CR with <c>deletionTimestamp</c> set → no-op; finalizer handles it.</description></item>
///   <item><description>On any failure → set condition <c>Ready=False</c> + return a failure result with backoff.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Finalizer lifecycle is delegated to <see cref="AgentGraphEntityFinalizer"/>, registered
/// via KubeOps' <c>AddFinalizer</c> pipeline.
/// </remarks>
[EntityRbac(typeof(AgentGraphEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(Corev1Event), Verbs = RbacVerb.Create | RbacVerb.Patch)]
internal sealed class AgentGraphEntityController(
    IAgentControlPlaneClient controlPlaneClient,
    IAgentGraphEntityKubernetesClient kubernetesClient,
    IOptionsMonitor<KubernetesOperatorOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentGraphEntityController> logger)
    : IEntityController<AgentGraphEntity>
{
    private readonly IAgentControlPlaneClient _controlPlaneClient = controlPlaneClient ?? throw new ArgumentNullException(nameof(controlPlaneClient));
    private readonly IAgentGraphEntityKubernetesClient _kubernetesClient = kubernetesClient ?? throw new ArgumentNullException(nameof(kubernetesClient));
    private readonly IOptionsMonitor<KubernetesOperatorOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<AgentGraphEntityController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<ReconciliationResult<AgentGraphEntity>> ReconcileAsync(AgentGraphEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.Metadata.DeletionTimestamp is not null)
        {
            _logger.LogDebug(
                "AgentGraphEntity {Namespace}/{Name} has deletionTimestamp — deferring to finalizer.",
                entity.Metadata.NamespaceProperty,
                entity.Metadata.Name);
            return ReconciliationResult<AgentGraphEntity>.Success(entity);
        }

        try
        {
            var desiredHash = AgentGraphSpecHasher.Compute(entity.Spec);
            var existing = entity.Status?.GraphHandle;

            if (existing is null)
            {
                await CreateAsync(entity, desiredHash, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.Equals(desiredHash, entity.Status?.ManifestRevision, StringComparison.Ordinal))
            {
                await UpdateAsync(entity, existing, desiredHash, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TouchAsync(entity, cancellationToken).ConfigureAwait(false);
            }

            return ReconciliationResult<AgentGraphEntity>.Success(entity);
        }
        catch (AgentManifestValidationException ex)
        {
            await RecordFailureAsync(entity, "ManifestInvalid", ex, validationFailure: true, cancellationToken).ConfigureAwait(false);
            return ReconciliationResult<AgentGraphEntity>.Failure(entity, "ManifestInvalid", ex, _options.CurrentValue.ReconcileBackoffInitial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(entity, "ReconcileFailed", ex, validationFailure: false, cancellationToken).ConfigureAwait(false);
            return ReconciliationResult<AgentGraphEntity>.Failure(entity, "ReconcileFailed", ex, _options.CurrentValue.ReconcileBackoffInitial);
        }
    }

    /// <inheritdoc />
    public Task<ReconciliationResult<AgentGraphEntity>> DeletedAsync(AgentGraphEntity entity, CancellationToken cancellationToken)
        => Task.FromResult(ReconciliationResult<AgentGraphEntity>.Success(entity));

    private async Task CreateAsync(AgentGraphEntity entity, string desiredHash, CancellationToken cancellationToken)
    {
        var idempotencyKey = IdempotencyKeyFactory.Build(entity.Metadata.Uid, entity.Metadata.Generation ?? 0L, IdempotencyKeyFactory.CreateVerb);
        var manifest = AgentGraphSpecProjector.ToManifest(entity.Spec);

        await MarkPhaseAsync(entity, AgentGraphPhase.Creating, cancellationToken).ConfigureAwait(false);

        var handle = await _controlPlaneClient
            .CreateGraphAsync(manifest, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var generation = entity.Metadata.Generation ?? 0L;
        entity.Status ??= new AgentGraphStatus();
        entity.Status.GraphHandle = new AgentGraphHandleRef(handle.GraphId, handle.Version);
        entity.Status.ManifestRevision = desiredHash;
        entity.Status.Phase = AgentGraphPhase.Active;
        entity.Status.LastReconciledAt = now;
        entity.Status.LastError = null;
        entity.Status.ObservedGeneration = generation;
        entity.Status.Conditions = new List<AgentCondition>
        {
            AgentConditions.Ready(AgentConditions.StatusTrue, "ReconcileSucceeded", "Graph created on the control plane.", now, generation),
            AgentConditions.Synced(AgentConditions.StatusTrue, "RuntimeMatchesSpec", "Runtime state matches desired.", now, generation),
            AgentConditions.ManifestValid(AgentConditions.StatusTrue, "ValidationPassed", "Manifest accepted by the runtime.", now, generation),
        };
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(AgentGraphEntity entity, AgentGraphHandleRef existing, string desiredHash, CancellationToken cancellationToken)
    {
        var idempotencyKey = IdempotencyKeyFactory.Build(entity.Metadata.Uid, entity.Metadata.Generation ?? 0L, IdempotencyKeyFactory.UpdateVerb);
        var manifest = AgentGraphSpecProjector.ToManifest(entity.Spec);

        await MarkPhaseAsync(entity, AgentGraphPhase.Updating, cancellationToken).ConfigureAwait(false);

        var newHandle = await _controlPlaneClient
            .UpdateGraphAsync(existing.GraphId, manifest, existing.Version, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var generation = entity.Metadata.Generation ?? 0L;
        entity.Status ??= new AgentGraphStatus();
        entity.Status.GraphHandle = new AgentGraphHandleRef(newHandle.GraphId, newHandle.Version);
        entity.Status.ManifestRevision = desiredHash;
        entity.Status.Phase = AgentGraphPhase.Active;
        entity.Status.LastReconciledAt = now;
        entity.Status.LastError = null;
        entity.Status.ObservedGeneration = generation;
        entity.Status.Conditions = new List<AgentCondition>
        {
            AgentConditions.Ready(AgentConditions.StatusTrue, "ReconcileSucceeded", "Graph updated on the control plane.", now, generation),
            AgentConditions.Synced(AgentConditions.StatusTrue, "RuntimeMatchesSpec", "Runtime state matches desired.", now, generation),
            AgentConditions.ManifestValid(AgentConditions.StatusTrue, "ValidationPassed", "Manifest accepted by the runtime.", now, generation),
        };
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task TouchAsync(AgentGraphEntity entity, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        entity.Status ??= new AgentGraphStatus();
        entity.Status.LastReconciledAt = now;
        entity.Status.LastError = null;
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkPhaseAsync(AgentGraphEntity entity, AgentGraphPhase phase, CancellationToken cancellationToken)
    {
        entity.Status ??= new AgentGraphStatus();
        entity.Status.Phase = phase;
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(AgentGraphEntity entity, string reason, Exception ex, bool validationFailure, CancellationToken cancellationToken)
    {
        _logger.LogWarning(ex, "AgentGraphEntity {Namespace}/{Name} reconcile failed: {Reason}", entity.Metadata.NamespaceProperty, entity.Metadata.Name, reason);

        var now = _timeProvider.GetUtcNow();
        var generation = entity.Metadata.Generation ?? 0L;
        entity.Status ??= new AgentGraphStatus();
        entity.Status.Phase = AgentGraphPhase.Error;
        entity.Status.LastReconciledAt = now;
        entity.Status.LastError = ex.Message;
        entity.Status.ObservedGeneration = generation;

        var conditions = new List<AgentCondition>
        {
            AgentConditions.Ready(AgentConditions.StatusFalse, reason, ex.Message, now, generation),
            AgentConditions.Synced(AgentConditions.StatusUnknown, reason, "Runtime state unknown — last reconcile failed.", now, generation),
        };
        conditions.Add(validationFailure
            ? AgentConditions.ManifestValid(AgentConditions.StatusFalse, reason, ex.Message, now, generation)
            : AgentConditions.ManifestValid(AgentConditions.StatusUnknown, reason, "Validation state unknown.", now, generation));
        entity.Status.Conditions = conditions;

        try
        {
            await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception statusEx)
        {
            _logger.LogError(statusEx, "Failed to write error status for AgentGraphEntity {Namespace}/{Name}.", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        }
    }
}
