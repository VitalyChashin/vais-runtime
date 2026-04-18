// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Plug-point for rendering a template string with a bag of variables. The default
/// implementation in Core does simple <c>{key}</c> substitution; consumers wanting
/// SK's Handlebars / Liquid engines wire those via their own adapter and register
/// it in DI for composers / contributors to pick up.
/// </summary>
/// <remarks>
/// Not consumed by <c>StatefulAiAgent</c> directly — it's a service exposed for
/// system-prompt contributors and consumer-side prompt assembly code. Register as
/// a DI singleton when wiring; the agent framework does not resolve it.
/// </remarks>
public interface IPromptTemplate
{
    /// <summary>
    /// Render <paramref name="template"/> by substituting variables. Missing variables
    /// are handled per-implementation (the shipped <c>FormatStringPromptTemplate</c>
    /// leaves them as literal <c>{key}</c> text).
    /// </summary>
    ValueTask<string> RenderAsync(
        string template,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken = default);
}
