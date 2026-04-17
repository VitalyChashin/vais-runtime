// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais2.Agents;

/// <summary>
/// Stack-neutral tool contract: a named, typed function the model can call.
/// Implementations describe themselves with a JSON Schema for parameters and
/// execute when the model selects them.
/// </summary>
/// <remarks>
/// The shape is deliberately thin — no provider types on the interface. Adapter
/// packages (<c>Vais2.Agents.Ai.SemanticKernel</c>, <c>Vais2.Agents.Ai.MicrosoftAgentFramework</c>)
/// translate to their stack's tool primitive.
/// </remarks>
public interface ITool
{
    /// <summary>Tool name. Must be unique within a single agent turn and match the regex <c>[A-Za-z0-9_-]+</c>.</summary>
    string Name { get; }

    /// <summary>Human-readable description. The model uses this to decide when to call.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema document describing the tool's parameters.
    /// The root should be an <c>object</c> schema with <c>properties</c>; the adapter
    /// forwards it to the underlying SDK unmodified.
    /// </summary>
    JsonElement ParametersSchema { get; }

    /// <summary>
    /// Invoke the tool with JSON-encoded arguments. The returned string is passed
    /// back to the model as the tool's result. Implementations are expected to
    /// encode structured results as JSON.
    /// </summary>
    /// <param name="arguments">
    /// JSON object whose keys match <see cref="ParametersSchema"/>'s <c>properties</c>.
    /// May be an empty object if the schema declares no required parameters.
    /// </param>
    /// <param name="cancellationToken">Cancellation token honoured by the caller.</param>
    Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}
