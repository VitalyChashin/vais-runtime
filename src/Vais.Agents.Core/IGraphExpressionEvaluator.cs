// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Evaluates an expression string against graph state. Implement this interface to provide
/// a custom expression language; the built-in implementation in
/// <c>Vais.Agents.Core.PowerFx</c> uses Microsoft PowerFx.
/// </summary>
public interface IGraphExpressionEvaluator
{
    /// <summary>
    /// Evaluate <paramref name="expression"/> against <paramref name="state"/> and return
    /// the boolean result. Implementations are responsible for stripping any leading
    /// <c>=</c> prefix (MAF declarative YAML convention).
    /// </summary>
    ValueTask<bool> EvaluatePredicateAsync(
        string expression,
        IReadOnlyDictionary<string, JsonElement> state,
        CancellationToken cancellationToken = default);
}
