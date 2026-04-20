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
/// Reconciles <see cref="AgentEntity"/> custom resources against the v0.6
/// HTTP control plane. Implements the 6-row reconcile decision table
/// documented on the v0.13 pillar plan:
/// <list type="number">
///   <item><description>New CR → <c>CreateAsync</c> → store handle on status.</description></item>
///   <item><description>CR with handle, spec-hash matches → refresh <c>LastReconciledAt</c>.</description></item>
///   <item><description>CR with handle, spec-hash differs → <c>UpdateAsync</c> → store new handle.</description></item>
///   <item><description>CR with <c>deletionTimestamp</c> set → no-op; finalizer handles it.</description></item>
///   <item><description>On any failure → set condition <c>Ready=False</c> + return a failure <see cref="ReconciliationResult{TEntity}"/> with backoff.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Finalizer lifecycle is delegated to <see cref="AgentEntityFinalizer"/>,
/// registered via KubeOps' <c>AddFinalizer</c> pipeline. KubeOps adds /
/// removes the finalizer string on the metadata automatically.
/// </remarks>
[EntityRbac(typeof(AgentEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch)]
[EntityRbac(typeof(Corev1Event), Verbs = RbacVerb.Create | RbacVerb.Patch)]
internal sealed class AgentEntityController(
    IAgentControlPlaneClient controlPlaneClient,
    IAgentEntityKubernetesClient kubernetesClient,
    IKubernetesSecretResolver secretResolver,
    IOptionsMonitor<KubernetesOperatorOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentEntityController> logger)
    : IEntityController<AgentEntity>
{
    private readonly IAgentControlPlaneClient _controlPlaneClient = controlPlaneClient ?? throw new ArgumentNullException(nameof(controlPlaneClient));
    private readonly IAgentEntityKubernetesClient _kubernetesClient = kubernetesClient ?? throw new ArgumentNullException(nameof(kubernetesClient));
    private readonly IKubernetesSecretResolver _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
    private readonly IOptionsMonitor<KubernetesOperatorOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<AgentEntityController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<ReconciliationResult<AgentEntity>> ReconcileAsync(AgentEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.Metadata.DeletionTimestamp is not null)
        {
            _logger.LogDebug(
                "AgentEntity {Namespace}/{Name} has deletionTimestamp — deferring to finalizer.",
                entity.Metadata.NamespaceProperty,
                entity.Metadata.Name);
            return ReconciliationResult<AgentEntity>.Success(entity);
        }

        try
        {
            if (entity.Spec.SecretRefs is { Count: > 0 })
            {
                await _secretResolver.ResolveAsync(
                        entity.Metadata.NamespaceProperty,
                        new Dictionary<string, SecretKeyReference>(entity.Spec.SecretRefs),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var desiredHash = SpecHasher.Compute(entity.Spec);
            var existing = entity.Status?.AgentHandle;

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

            return ReconciliationResult<AgentEntity>.Success(entity);
        }
        catch (SecretResolutionException ex)
        {
            await RecordFailureAsync(entity, "SecretResolutionFailed", ex, validationFailure: true, cancellationToken).ConfigureAwait(false);
            return ReconciliationResult<AgentEntity>.Failure(entity, "SecretResolutionFailed", ex, _options.CurrentValue.ReconcileBackoffInitial);
        }
        catch (AgentManifestValidationException ex)
        {
            await RecordFailureAsync(entity, "ManifestInvalid", ex, validationFailure: true, cancellationToken).ConfigureAwait(false);
            return ReconciliationResult<AgentEntity>.Failure(entity, "ManifestInvalid", ex, _options.CurrentValue.ReconcileBackoffInitial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(entity, "ReconcileFailed", ex, validationFailure: false, cancellationToken).ConfigureAwait(false);
            return ReconciliationResult<AgentEntity>.Failure(entity, "ReconcileFailed", ex, _options.CurrentValue.ReconcileBackoffInitial);
        }
    }

    /// <inheritdoc />
    public Task<ReconciliationResult<AgentEntity>> DeletedAsync(AgentEntity entity, CancellationToken cancellationToken)
        => Task.FromResult(ReconciliationResult<AgentEntity>.Success(entity));

    private async Task CreateAsync(AgentEntity entity, string desiredHash, CancellationToken cancellationToken)
    {
        var idempotencyKey = IdempotencyKeyFactory.Build(entity.Metadata.Uid, entity.Metadata.Generation ?? 0L, IdempotencyKeyFactory.CreateVerb);
        var manifest = AgentSpecProjector.ToManifest(entity.Spec);

        await MarkPhaseAsync(entity, AgentPhase.Creating, cancellationToken).ConfigureAwait(false);

        var handle = await _controlPlaneClient
            .CreateAsync(manifest, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var generation = entity.Metadata.Generation ?? 0L;
        entity.Status ??= new AgentStatus();
        entity.Status.AgentHandle = new AgentHandleRef(handle.AgentId, handle.Version, handle.InstanceId);
        entity.Status.ManifestRevision = desiredHash;
        entity.Status.Phase = AgentPhase.Active;
        entity.Status.LastReconciledAt = now;
        entity.Status.LastError = null;
        entity.Status.ObservedGeneration = generation;
        entity.Status.Conditions = new List<AgentCondition>
        {
            AgentConditions.Ready(AgentConditions.StatusTrue, "ReconcileSucceeded", "Agent created on the control plane.", now, generation),
            AgentConditions.Synced(AgentConditions.StatusTrue, "RuntimeMatchesSpec", "Runtime state matches desired.", now, generation),
            AgentConditions.ManifestValid(AgentConditions.StatusTrue, "ValidationPassed", "Manifest accepted by the runtime.", now, generation),
        };
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(AgentEntity entity, AgentHandleRef existing, string desiredHash, CancellationToken cancellationToken)
    {
        var idempotencyKey = IdempotencyKeyFactory.Build(entity.Metadata.Uid, entity.Metadata.Generation ?? 0L, IdempotencyKeyFactory.UpdateVerb);
        var manifest = AgentSpecProjector.ToManifest(entity.Spec);

        await MarkPhaseAsync(entity, AgentPhase.Updating, cancellationToken).ConfigureAwait(false);

        var newHandle = await _controlPlaneClient
            .UpdateAsync(existing.AgentId, manifest, existing.Version, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var generation = entity.Metadata.Generation ?? 0L;
        entity.Status ??= new AgentStatus();
        entity.Status.AgentHandle = new AgentHandleRef(newHandle.AgentId, newHandle.Version, newHandle.InstanceId);
        entity.Status.ManifestRevision = desiredHash;
        entity.Status.Phase = AgentPhase.Active;
        entity.Status.LastReconciledAt = now;
        entity.Status.LastError = null;
        entity.Status.ObservedGeneration = generation;
        entity.Status.Conditions = new List<AgentCondition>
        {
            AgentConditions.Ready(AgentConditions.StatusTrue, "ReconcileSucceeded", "Agent updated on the control plane.", now, generation),
            AgentConditions.Synced(AgentConditions.StatusTrue, "RuntimeMatchesSpec", "Runtime state matches desired.", now, generation),
            AgentConditions.ManifestValid(AgentConditions.StatusTrue, "ValidationPassed", "Manifest accepted by the runtime.", now, generation),
        };
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task TouchAsync(AgentEntity entity, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        entity.Status ??= new AgentStatus();
        entity.Status.LastReconciledAt = now;
        entity.Status.LastError = null;
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkPhaseAsync(AgentEntity entity, AgentPhase phase, CancellationToken cancellationToken)
    {
        entity.Status ??= new AgentStatus();
        entity.Status.Phase = phase;
        await _kubernetesClient.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(AgentEntity entity, string reason, Exception ex, bool validationFailure, CancellationToken cancellationToken)
    {
        _logger.LogWarning(ex, "AgentEntity {Namespace}/{Name} reconcile failed: {Reason}", entity.Metadata.NamespaceProperty, entity.Metadata.Name, reason);

        var now = _timeProvider.GetUtcNow();
        var generation = entity.Metadata.Generation ?? 0L;
        entity.Status ??= new AgentStatus();
        entity.Status.Phase = AgentPhase.Error;
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
            _logger.LogError(statusEx, "Failed to write error status for AgentEntity {Namespace}/{Name}.", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        }
    }
}
