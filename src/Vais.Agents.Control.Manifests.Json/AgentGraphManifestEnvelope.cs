// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Wraps an <see cref="AgentGraphManifest"/> into the v0.6-style envelope shape
/// (<c>apiVersion</c> + <c>kind</c> + <c>metadata</c> + <c>spec</c>) that
/// <see cref="JsonAgentGraphManifestLoader"/> consumes on the wire.
/// </summary>
/// <remarks>
/// Thin wrapper over the shared <see cref="EnvelopeCodec"/> (Phase 3 / MS-1c) — kept as a
/// stable public entry point; the per-kind serialization logic lives in the codec.
/// </remarks>
public static class AgentGraphManifestEnvelope
{
    /// <summary>Serialise <paramref name="manifest"/> to a v0.6 envelope JSON string.</summary>
    public static string Serialize(AgentGraphManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "AgentGraph");
    }
}
