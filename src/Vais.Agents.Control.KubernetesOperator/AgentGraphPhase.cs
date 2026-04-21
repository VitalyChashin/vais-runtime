// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Lifecycle phase surfaced on <see cref="AgentGraphStatus.Phase"/>. Mirrors the six
/// operator-local states the reconciler transitions through. Wire casing is PascalCase —
/// matches <c>Pod.status.phase = Running</c> idiom.
/// </summary>
/// <remarks>
/// Distinct from the runtime's graph execution state — this enum is an operator concern
/// and includes states the runtime never sees (<see cref="Pending"/>, <see cref="Creating"/>,
/// <see cref="Updating"/>, <see cref="Error"/>, <see cref="Terminating"/>).
/// </remarks>
public enum AgentGraphPhase
{
    /// <summary>Custom resource seen by the controller but not yet reconciled. Initial state after finalizer is added.</summary>
    Pending = 0,

    /// <summary>Controller is mid-<c>CreateAsync</c> call against the control plane. Transient.</summary>
    Creating = 1,

    /// <summary>Graph is registered in the runtime and the controller's last reconcile succeeded.</summary>
    Active = 2,

    /// <summary>Controller is mid-<c>UpdateAsync</c> call after a spec change. Transient.</summary>
    Updating = 3,

    /// <summary>Last reconcile attempt failed. Controller is backing off; <see cref="AgentGraphStatus.LastError"/> carries detail.</summary>
    Error = 4,

    /// <summary>Custom resource has a <c>metadata.deletionTimestamp</c> set; controller is running finalizer logic before K8s garbage-collects the resource.</summary>
    Terminating = 5,
}
