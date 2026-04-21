// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using KubeOps.KubernetesClient;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Narrow wrapper over the methods on <see cref="IKubernetesClient"/> that the graph
/// reconcile loop actually touches. Keeps the test surface small — hand-rolled fakes only
/// need to stub one method.
/// </summary>
internal interface IAgentGraphEntityKubernetesClient
{
    /// <summary>Patch the status subresource of <paramref name="entity"/> via the K8s API.</summary>
    Task UpdateStatusAsync(AgentGraphEntity entity, CancellationToken cancellationToken);
}

/// <summary>Default implementation — delegates to <see cref="IKubernetesClient.UpdateStatusAsync"/>.</summary>
internal sealed class AgentGraphEntityKubernetesClient(IKubernetesClient inner) : IAgentGraphEntityKubernetesClient
{
    private readonly IKubernetesClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public Task UpdateStatusAsync(AgentGraphEntity entity, CancellationToken cancellationToken)
        => _inner.UpdateStatusAsync(entity, cancellationToken);
}
