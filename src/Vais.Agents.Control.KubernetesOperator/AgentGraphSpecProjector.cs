// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Projects an <see cref="AgentGraphSpec"/> onto a shipped <see cref="AgentGraphManifest"/>
/// envelope that the v0.19 HTTP control plane accepts. The projection is field-by-field;
/// collection fields are copied via <see cref="List{T}"/> materialisation.
/// </summary>
internal static class AgentGraphSpecProjector
{
    /// <summary>
    /// Build an <see cref="AgentGraphManifest"/> from a CR <paramref name="spec"/>.
    /// </summary>
    public static AgentGraphManifest ToManifest(AgentGraphSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return new AgentGraphManifest(
            Id: spec.GraphId,
            Version: spec.Version,
            Entry: spec.Entry,
            Nodes: spec.Nodes.ToList(),
            Edges: spec.Edges.ToList(),
            Description: spec.Description,
            Labels: spec.Labels is null ? null : new Dictionary<string, string>(spec.Labels),
            Annotations: spec.Annotations is null ? null : new Dictionary<string, string>(spec.Annotations))
        {
            StateSchema = spec.StateSchema,
            MaxSteps = spec.MaxSteps,
        };
    }
}
