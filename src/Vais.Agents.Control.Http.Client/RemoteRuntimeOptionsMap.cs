// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Options model holding the per-runtime identity-propagation configuration.
/// Bound from <c>Vais:RemoteRuntimes</c> — the key is the normalised runtime URL.
/// </summary>
public sealed class RemoteRuntimeOptionsMap
{
    /// <summary>Per-runtime options keyed by normalised runtime URL (case-insensitive).</summary>
    public Dictionary<string, RemoteRuntimeOptions> Runtimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
