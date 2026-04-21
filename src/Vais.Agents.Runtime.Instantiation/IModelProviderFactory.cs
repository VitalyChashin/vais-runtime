// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Creates an <c>ICompletionProvider</c> from a stored <c>ModelSpec</c>. One
/// factory per provider string (<c>"openai"</c>, <c>"anthropic"</c>,
/// <c>"azure-openai"</c>, …); the translator dispatches by
/// <see cref="Provider"/>.
/// </summary>
/// <remarks>
/// The built-in set (<see cref="Vais.Agents.Runtime.Instantiation"/>'s
/// <c>AddBuiltinModelProviders</c> in PR 2) ships the three launch providers.
/// Consumer registers additional factories (Bedrock, Gemini, Ollama, …) via
/// <c>services.AddSingleton&lt;IModelProviderFactory, BedrockModelProviderFactory&gt;()</c>.
/// Provider-string matching is case-insensitive.
/// </remarks>
public interface IModelProviderFactory
{
    /// <summary>Provider string this factory handles — matched case-insensitive against <c>ModelSpec.Provider</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Build a completion provider for the supplied spec. The factory pulls API
    /// keys / endpoints via <paramref name="secrets"/>; the translator is the
    /// only caller and wires the resolver from DI.
    /// </summary>
    /// <exception cref="ManifestInstantiationException">Thrown when the spec is missing required fields (e.g. api-key-ref) or the secret-resolver returns empty.</exception>
    ValueTask<ICompletionProvider> CreateAsync(
        ModelSpec spec,
        ISecretResolver secrets,
        CancellationToken cancellationToken = default);
}
