// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Operator configuration knobs. Wired via the options pattern through
/// <c>AddAgentKubernetesOperator(Action&lt;KubernetesOperatorOptions&gt;)</c>
/// and bound to the <c>Vais:KubernetesOperator</c> config section by
/// convention.
/// </summary>
/// <remarks>
/// Public surface is frozen with v0.13; in PR 1 the defaults carry the
/// intent and the fields are consumed for the first time in PR 2 when
/// the reconciler + HTTP handler ship.
/// </remarks>
public sealed class KubernetesOperatorOptions
{
    /// <summary>
    /// Base URL of the v0.6 HTTP control plane the operator reconciles
    /// against. Required — the operator fails fast on startup when null
    /// or empty. Example: <c>https://vais-runtime.vais.svc.cluster.local:443</c>.
    /// </summary>
    public Uri? ControlPlaneBaseUrl { get; set; }

    /// <summary>
    /// JWT audience the operator requests on its projected
    /// ServiceAccount token + the runtime validates against on the
    /// receiving side. Defaults to <c>vais-agents-runtime</c>; must
    /// match the runtime's <c>AddAgentControlPlaneJwtAuth</c> config.
    /// </summary>
    public string ControlPlaneAudience { get; set; } = "vais-agents-runtime";

    /// <summary>
    /// Filesystem path the operator reads its projected
    /// ServiceAccount token from. Defaults to the standard
    /// projected-volume location used by the shipped Helm chart.
    /// Ignored when <see cref="AuthMode"/> is
    /// <see cref="KubernetesOperatorAuthMode.ClientCredentials"/>.
    /// </summary>
    public string TokenPath { get; set; } = "/var/run/secrets/tokens/vais-runtime-token";

    /// <summary>Authentication scheme used on outbound control-plane calls.</summary>
    public KubernetesOperatorAuthMode AuthMode { get; set; } = KubernetesOperatorAuthMode.ServiceAccount;

    /// <summary>
    /// Namespaces the operator watches <see cref="AgentEntity"/> + K8s
    /// <c>Secret</c> resources in. Null / empty = cluster-wide. Narrow
    /// for multi-tenant clusters where the operator should only reconcile
    /// specific namespaces.
    /// </summary>
    public IList<string>? WatchNamespaces { get; set; }

    /// <summary>
    /// In-memory TTL for the projected ServiceAccount token. On cache
    /// hit the operator also re-reads when the token file's
    /// <c>mtime</c> changed (kubelet rotates atomically). Defaults to
    /// 5 minutes.
    /// </summary>
    public TimeSpan TokenCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initial requeue backoff after a failed reconcile. Paired with
    /// <see cref="ReconcileBackoffMax"/> to bound exponential retry.
    /// </summary>
    public TimeSpan ReconcileBackoffInitial { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum requeue backoff after repeated failures.</summary>
    public TimeSpan ReconcileBackoffMax { get; set; } = TimeSpan.FromMinutes(15);
}
