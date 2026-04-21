// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Wire-shaped projection of <see cref="Vais.Agents.AgentGraphHandle"/> stored in
/// <see cref="AgentGraphStatus.GraphHandle"/>. K8s status subresources store JSON
/// value bags; mirroring the two-tuple gives consumers
/// <c>kubectl get vgraph ... -o jsonpath='{.status.graphHandle.graphId}'</c> access.
/// </summary>
/// <param name="GraphId">Stable graph identifier as registered in the runtime.</param>
/// <param name="Version">Immutable version tag — updated each time the controller calls <c>UpdateAsync</c> on a spec change.</param>
public sealed record AgentGraphHandleRef(string GraphId, string Version);
