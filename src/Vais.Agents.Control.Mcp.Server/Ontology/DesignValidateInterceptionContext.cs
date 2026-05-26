// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Mcp.Server.Ontology;

/// <summary>
/// Substrate context for the <c>vais.validate</c> interception chain. Carries the
/// candidate manifest text being validated; the chain produces a
/// <see cref="ValidationOutcome"/>.
/// </summary>
internal sealed class DesignValidateInterceptionContext : InterceptionContext
{
    /// <summary>The candidate manifest JSON text being validated. Never null or whitespace at this point.</summary>
    public required string ManifestJson { get; init; }
}

/// <summary>
/// Outcome the validate chain produces. Mirrors the inline
/// <c>ManifestValidator.ValidateAsync</c> tuple shape so the handler can serialize byte-identically.
/// </summary>
/// <param name="Ok">True iff <paramref name="Errors"/> is empty.</param>
/// <param name="Errors">Hard validation failures (schema breaks, missing envelope keys, dangling refs).</param>
/// <param name="Suggestions">Actionable follow-up hints (e.g. "apply the missing resource first").</param>
internal sealed record ValidationOutcome(
    bool Ok,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Suggestions)
{
    /// <summary>An empty pass-through outcome (no errors, no suggestions). Used as the chain's terminal default.</summary>
    public static ValidationOutcome AllOk { get; } = new(true, [], []);
}
