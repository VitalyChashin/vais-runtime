// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Kubernetes custom resource representing a declarative graph of agents. Maps 1:1 to a
/// graph registered on the v0.19 HTTP control plane; the operator reconciles <c>Spec</c>
/// changes by calling the runtime's <c>IAgentGraphLifecycleManager</c> verbs and writes
/// observed state onto <see cref="AgentGraphStatus"/> via the K8s status subresource.
/// </summary>
/// <remarks>
/// <para>
/// <b>GVK</b>: <c>vais.io/v1alpha1</c>, Kind <c>AgentGraph</c>. <b>Scope</b>: namespaced.
/// <b>Short names</b>: <c>vgraph</c>, <c>vgraphs</c>.
/// <c>kubectl apply -f</c>, <c>kubectl get vgraph</c>, <c>kubectl describe vgraph/&lt;name&gt;</c>
/// all work as expected.
/// </para>
/// <para>
/// Consumers author YAML against <see cref="AgentGraphSpec"/> (the canonical declarative
/// shape mirroring <see cref="AgentGraphManifest"/>).
/// </para>
/// </remarks>
[KubernetesEntity(Group = "vais.io", ApiVersion = "v1alpha1", Kind = "AgentGraph", PluralName = "agentgraphs")]
[KubernetesEntityShortNames("vgraph", "vgraphs")]
public sealed class AgentGraphEntity : CustomKubernetesEntity<AgentGraphSpec, AgentGraphStatus>
{
    /// <summary>Group under which the CRD is registered — <c>vais.io</c>.</summary>
    public const string EntityGroup = "vais.io";

    /// <summary>API version registered for this CRD — <c>v1alpha1</c>. Storage version while the operator is pre-1.0.</summary>
    public const string EntityApiVersion = "v1alpha1";

    /// <summary>Kind registered on the CRD — <c>AgentGraph</c>.</summary>
    public const string EntityKind = "AgentGraph";

    /// <summary>Plural name under which instances are listed — <c>agentgraphs</c>.</summary>
    public const string EntityPluralName = "agentgraphs";

    /// <summary>
    /// Finalizer that pins the CR's deletion until the operator has called
    /// <c>EvictGraphAsync</c> on the runtime. Skipped when
    /// <see cref="AgentGraphSpec.PreserveOnDelete"/> is <c>true</c>.
    /// </summary>
    public const string EvictFinalizer = "vais.io/agentgraph-evict";

    /// <summary>Annotation key the operator reads to bind a CR to a tenant.</summary>
    public const string TenantIdAnnotation = "vais.io/tenant-id";
}
