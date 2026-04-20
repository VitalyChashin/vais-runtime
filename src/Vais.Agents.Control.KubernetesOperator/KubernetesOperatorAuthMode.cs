// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Authentication scheme the operator uses when calling the v0.6 HTTP
/// control plane. Selected via
/// <see cref="KubernetesOperatorOptions.AuthMode"/>.
/// </summary>
public enum KubernetesOperatorAuthMode
{
    /// <summary>
    /// Operator reads a projected ServiceAccount token from
    /// <see cref="KubernetesOperatorOptions.TokenPath"/> and injects it as
    /// <c>Authorization: Bearer</c>. Default for in-cluster deployments.
    /// Runtime validates via stock OIDC discovery against the K8s API.
    /// </summary>
    ServiceAccount = 0,

    /// <summary>
    /// Operator ships without its own auth handler — consumer wires a
    /// <c>DelegatingHandler</c> of their own (e.g. OAuth2
    /// client-credentials, mTLS). Fallback for out-of-cluster runtime
    /// deployments where the K8s API issuer isn't reachable for JWKS.
    /// </summary>
    ClientCredentials = 1,
}
