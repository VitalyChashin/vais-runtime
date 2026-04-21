// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Opaque reference to a registered graph, analogous to <c>AgentHandle</c> for agents.
/// Carries <see cref="GraphId"/> + <see cref="Version"/> so lifecycle-manager calls are
/// always version-pinned; never holds mutable run state.
/// </summary>
/// <param name="GraphId">Stable identifier matching <see cref="AgentGraphManifest.Id"/>.</param>
/// <param name="Version">Semver version matching <see cref="AgentGraphManifest.Version"/>.</param>
public sealed record AgentGraphHandle(string GraphId, string Version);
