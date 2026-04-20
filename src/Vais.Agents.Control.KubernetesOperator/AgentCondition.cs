// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Single condition entry on <see cref="AgentStatus.Conditions"/>. Mirrors
/// the K8s convention established for <c>Pod.status.conditions</c>,
/// <c>Deployment.status.conditions</c>, and the standard K8s condition
/// shape documented at
/// <see href="https://kubernetes.io/docs/concepts/overview/working-with-objects/kubernetes-api/#standard-api-conventions"/>.
/// </summary>
/// <remarks>
/// The operator emits three condition types: <c>Ready</c> (reconcile
/// succeeded), <c>Synced</c> (runtime state matches desired), and
/// <c>ManifestValid</c> (last upsert passed runtime validation). Consumers
/// inspect <c>.status.conditions[?(@.type=="Ready")].status</c> in jsonpath
/// queries — hence string typing on <see cref="Type"/> and <see cref="Status"/>
/// rather than enums, matching the K8s wire convention.
/// </remarks>
/// <param name="Type">Condition type — <c>Ready</c>, <c>Synced</c>, or <c>ManifestValid</c>. String-typed to match K8s convention; consumer code compares via string equality.</param>
/// <param name="Status">Condition status — <c>True</c>, <c>False</c>, or <c>Unknown</c>. String-typed per K8s wire convention.</param>
/// <param name="Reason">Machine-readable, CamelCase reason code. Stable across restarts; consumers can pattern-match.</param>
/// <param name="Message">Human-readable detail. Free-form; may change between minor versions.</param>
/// <param name="LastTransitionTime">Timestamp the condition last transitioned between statuses.</param>
/// <param name="ObservedGeneration">The <c>metadata.generation</c> the controller saw when it wrote this condition. Consumers can detect stale reconciles by comparing against <c>metadata.generation</c>.</param>
public sealed record AgentCondition(
    string Type,
    string Status,
    string Reason,
    string Message,
    DateTimeOffset LastTransitionTime,
    long ObservedGeneration);
