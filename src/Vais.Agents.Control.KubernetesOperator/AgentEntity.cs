// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Kubernetes custom resource representing a declarative agent. Maps 1:1
/// to an agent registered on the v0.6 HTTP control plane; the v0.13
/// operator reconciles <c>Spec</c> changes by calling the runtime's
/// <c>IAgentLifecycleManager</c> verbs and writes observed state onto
/// <see cref="AgentStatus"/> via the K8s status subresource.
/// </summary>
/// <remarks>
/// <para>
/// <b>GVK</b>: <c>vais.io/v1alpha1</c>, Kind <c>Agent</c>. <b>Scope</b>:
/// namespaced. <b>Short names</b>: <c>vagent</c>, <c>vagents</c>.
/// <c>kubectl apply -f</c>, <c>kubectl get vagent</c>, <c>kubectl
/// describe vagent/&lt;name&gt;</c> all work as expected.
/// </para>
/// <para>
/// Consumers author YAML against <see cref="AgentSpec"/> (the canonical
/// declarative shape mirroring <see cref="AgentManifest"/>). The
/// operator resolves any <see cref="AgentSpec.SecretRefs"/> via the K8s
/// API before materialising the agent on the runtime.
/// </para>
/// <para>
/// <b>Tenant binding</b> follows annotation convention: the operator
/// reads <c>vais.io/tenant-id</c> from <c>metadata.annotations</c>
/// (explicit, decouples K8s namespace topology from tenant topology).
/// An optional <c>ServiceAccountPrincipalMapper</c> maps the calling
/// ServiceAccount's namespace into <c>TenantId</c> for deployments that
/// prefer implicit binding.
/// </para>
/// </remarks>
[KubernetesEntity(Group = "vais.io", ApiVersion = "v1alpha1", Kind = "Agent", PluralName = "agents")]
[KubernetesEntityShortNames("vagent", "vagents")]
public sealed class AgentEntity : CustomKubernetesEntity<AgentSpec, AgentStatus>
{
    /// <summary>Group under which the CRD is registered — <c>vais.io</c>.</summary>
    public const string EntityGroup = "vais.io";

    /// <summary>API version registered for this CRD — <c>v1alpha1</c>. Storage version while the operator is pre-1.0.</summary>
    public const string EntityApiVersion = "v1alpha1";

    /// <summary>Kind registered on the CRD — <c>Agent</c>.</summary>
    public const string EntityKind = "Agent";

    /// <summary>Plural name under which instances are listed — <c>agents</c>.</summary>
    public const string EntityPluralName = "agents";

    /// <summary>
    /// Finalizer that pins the CR's deletion until the operator has
    /// called <see cref="Vais.Agents.IAgentLifecycleManager.EvictAsync"/>
    /// on the runtime. Skipped when <see cref="AgentSpec.PreserveOnDelete"/>
    /// is <c>true</c>.
    /// </summary>
    public const string DeactivateFinalizer = "vais.io/agent-deactivate";

    /// <summary>Annotation key the operator reads to bind a CR to a tenant.</summary>
    public const string TenantIdAnnotation = "vais.io/tenant-id";
}
