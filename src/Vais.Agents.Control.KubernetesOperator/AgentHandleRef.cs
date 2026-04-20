// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Wire-shaped projection of <see cref="Vais.Agents.AgentHandle"/> stored in
/// <see cref="AgentStatus.AgentHandle"/>. K8s status subresources store JSON
/// value bags; mirroring the three-tuple gives consumers <c>kubectl get
/// vagent ... -o jsonpath='{.status.agentHandle.agentId}'</c> access.
/// </summary>
/// <param name="AgentId">Stable agent identifier as registered in the runtime.</param>
/// <param name="Version">Immutable version tag — updated each time the controller calls <c>UpdateAsync</c> on a spec change.</param>
/// <param name="InstanceId">Optional runtime-assigned instance identifier when the runtime keys at a finer granularity than agent+version. Null for registries that key on the two-tuple.</param>
public sealed record AgentHandleRef(string AgentId, string Version, string? InstanceId = null);
