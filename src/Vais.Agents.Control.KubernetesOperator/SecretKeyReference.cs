// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// K8s-style selector for a single key inside a <c>Secret</c> resource.
/// Mirrors the shape of Kubernetes <c>SecretKeySelector</c> used in
/// <c>Pod.spec.containers[].env[].valueFrom.secretKeyRef</c>.
/// </summary>
/// <remarks>
/// <para>
/// Resolved by the operator's secret resolver before the resulting
/// <see cref="Vais.Agents.AgentManifest"/> is sent to the control plane.
/// Runtime sees the resolved value as a plain string — identical to how
/// environment-resolved secrets flow under the shipped <c>ISecretResolver</c>
/// composite.
/// </para>
/// <para>
/// <b>v0.13 excludes</b> the <c>optional</c> field present on the K8s
/// type — a missing secret is an error that surfaces on the
/// <see cref="AgentStatus"/> conditions and triggers a reconcile backoff.
/// A future pillar may add graceful-skip semantics if demand arises.
/// </para>
/// </remarks>
/// <param name="Name">Name of the <c>Secret</c> resource in the same namespace as the owning <c>Agent</c> CR.</param>
/// <param name="Key">Key inside <c>secret.data</c> whose value should be resolved and substituted.</param>
public sealed record SecretKeyReference(string Name, string Key);
