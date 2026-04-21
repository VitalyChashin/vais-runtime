// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// The <c>.status</c> subresource of an <see cref="AgentGraphEntity"/> custom resource.
/// Controller-owned — patched via the K8s status-subresource endpoint on every reconcile
/// that observes a material change.
/// </summary>
/// <remarks>
/// The <see cref="Phase"/> + <see cref="Conditions"/> pair mirrors K8s built-in patterns.
/// <see cref="ObservedGeneration"/> follows the <c>Deployment.status.observedGeneration</c>
/// idiom — equal to <c>metadata.generation</c> when status is current; smaller when the
/// controller is behind.
/// </remarks>
public sealed class AgentGraphStatus
{
    /// <summary>
    /// Two-tuple handle returned by the runtime's <c>CreateAsync</c> / <c>UpdateAsync</c>.
    /// Null until the controller's first successful upsert. Replaced with a new handle on
    /// each <c>UpdateAsync</c> (new version).
    /// </summary>
    public AgentGraphHandleRef? GraphHandle { get; set; }

    /// <summary>
    /// SHA-256 of canonical-JSON(<see cref="AgentGraphSpec"/>) at the last successful upsert.
    /// Reconcile compares against this to detect drift; equal → no runtime call.
    /// </summary>
    public string? ManifestRevision { get; set; }

    /// <summary>Operator-local lifecycle phase. Defaults to <see cref="AgentGraphPhase.Pending"/> until the first reconcile runs.</summary>
    public AgentGraphPhase Phase { get; set; } = AgentGraphPhase.Pending;

    /// <summary>UTC timestamp of the last reconcile pass that observed this CR. Refreshed even on no-op reconciles.</summary>
    public DateTimeOffset? LastReconciledAt { get; set; }

    /// <summary>
    /// Short exception-type / message from the last failed reconcile. Null when the last pass succeeded.
    /// Paired with condition <c>Ready: False</c> carrying the same machine-readable reason.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Standard K8s condition entries. The operator emits three types — <c>Ready</c>, <c>Synced</c>,
    /// <c>ManifestValid</c>. Consumers filter by <c>type</c>.
    /// </summary>
    public IList<AgentCondition>? Conditions { get; set; }

    /// <summary>
    /// The <c>metadata.generation</c> the controller saw when it last wrote status.
    /// Consumers detect stale reconciles by comparing this against the live <c>metadata.generation</c>.
    /// </summary>
    public long ObservedGeneration { get; set; }
}
