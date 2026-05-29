// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Projects an <see cref="AgentSpec"/> onto a shipped
/// <see cref="AgentManifest"/> envelope that the v0.6 HTTP control plane
/// accepts. The projection is field-by-field; collection fields are
/// copied via <see cref="List{T}"/> materialisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>v0.13 limitation</b>: <see cref="AgentSpec.SecretRefs"/> is NOT
/// projected into the manifest envelope. The CR's secret references are
/// resolved by <see cref="KubernetesSecretResolver"/> as a
/// <em>validation step</em> (catches missing secrets early and surfaces
/// on <see cref="AgentStatus.Conditions"/>) but the resolved values are
/// not wired into the manifest fields — the runtime's existing
/// <c>ISecretResolver</c> composite picks up <c>env:</c> / <c>file:</c>
/// URIs set in the manifest directly by the CR author.
/// </para>
/// <para>
/// A future pillar lands an inline-value wire format
/// (<c>secret://inline/&lt;name&gt;</c> scheme + runtime-side map
/// resolver) that closes the loop so operator-resolved K8s Secret values
/// flow end-to-end. Out of scope for v0.13.
/// </para>
/// </remarks>
internal static class AgentSpecProjector
{
    /// <summary>
    /// Build an <see cref="AgentManifest"/> from a CR <paramref name="spec"/>.
    /// </summary>
    public static AgentManifest ToManifest(AgentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return new AgentManifest(
            Id: spec.AgentId,
            Version: spec.Version,
            Handler: spec.Handler,
            Protocols: spec.Protocols.ToList(),
            Tools: spec.Tools.ToList(),
            Memory: spec.Memory,
            Identity: spec.Identity,
            Autoscaling: spec.Autoscaling,
            Description: spec.Description,
            Labels: spec.Labels is null ? null : new Dictionary<string, string>(spec.Labels))
        {
            Model = spec.Model,
            SystemPrompt = spec.SystemPrompt,
            McpServers = spec.McpServers?.ToList(),
            Guardrails = spec.Guardrails,
            Handoffs = spec.Handoffs?.ToList(),
            Budget = spec.Budget,
            ContextProviders = spec.ContextProviders?.ToList(),
            OutputSchema = spec.OutputSchema,
            AgentMode = spec.AgentMode,
            Reasoning = spec.Reasoning,
            CodeMode = spec.CodeMode,
            Observability = spec.Observability,
            Annotations = spec.Annotations is null ? null : new Dictionary<string, string>(spec.Annotations),
        };
    }
}
