// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Evaluates whether a container extension targets a hot seam and returns a problem description
/// when it does without operator acknowledgment.
/// </summary>
/// <remarks>
/// Hot seams are those called in the critical LLM path where container round-trips add
/// measurable per-call latency. The set is configurable; it starts empty in Phase A/B
/// (no seam is blocked by default). Phase C adds LlmGatewayMiddleware to the default set.
/// </remarks>
public sealed class HotSeamGuard
{
    private readonly IReadOnlySet<string> _hotSeams;

    /// <summary>Construct with a configurable set of hot seam identifiers.</summary>
    public HotSeamGuard(IReadOnlySet<string>? hotSeams = null)
    {
        _hotSeams = hotSeams ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Default instance — empty hot-seam set (no seams are blocked in Phase B).</summary>
    public static readonly HotSeamGuard Default = new();

    /// <summary>
    /// Returns descriptions of any handlers that target a hot seam in a <c>host: container</c> extension.
    /// Empty list = no hot-seam concern; 200 OK may proceed.
    /// </summary>
    public IReadOnlyList<HotSeamViolation> Evaluate(ExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!string.Equals(manifest.Spec.Host, "container", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<HotSeamViolation>();

        if (_hotSeams.Count == 0)
            return Array.Empty<HotSeamViolation>();

        var violations = new List<HotSeamViolation>();
        foreach (var handler in manifest.Spec.Handlers)
        {
            if (_hotSeams.Contains(handler.Seam))
            {
                violations.Add(new HotSeamViolation(handler.Id, handler.Seam));
            }
        }
        return violations;
    }
}

/// <summary>A single hot-seam violation found by <see cref="HotSeamGuard"/>.</summary>
/// <param name="HandlerId">Handler that targets the hot seam.</param>
/// <param name="Seam">The hot seam name.</param>
public sealed record HotSeamViolation(string HandlerId, string Seam);
