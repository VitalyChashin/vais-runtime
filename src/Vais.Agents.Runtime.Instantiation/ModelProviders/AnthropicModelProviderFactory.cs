// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Vais.Agents.Ai.MicrosoftAgentFramework;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Instantiation.ModelProviders;

/// <summary>
/// <see cref="IModelProviderFactory"/> for Anthropic — `ModelSpec.Provider ==
/// "anthropic"`. Bridges the community `Anthropic.SDK` client to MEAI's
/// <see cref="IChatClient"/> and wraps it as a
/// <see cref="MafCompletionProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Anthropic.SDK is community-maintained and its MEAI surface has shifted
/// between preview releases; the pillar plan's risks section calls this factory
/// preview-grade. Partners hitting breakage on a future SDK bump should drop
/// in a custom <see cref="IModelProviderFactory"/> and replace the DI
/// registration.
/// </para>
/// </remarks>
public sealed class AnthropicModelProviderFactory : IModelProviderFactory
{
    /// <summary>Provider name (case-insensitive match against <c>ModelSpec.Provider</c>).</summary>
    public const string ProviderName = "anthropic";

    /// <inheritdoc />
    public string Provider => ProviderName;

    /// <inheritdoc />
    public async ValueTask<ICompletionProvider> CreateAsync(
        ModelSpec spec,
        ISecretResolver secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(secrets);

        if (string.IsNullOrWhiteSpace(spec.ApiKeyRef))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"Anthropic ModelSpec requires 'ApiKeyRef' (a secret:// URI resolving to an API key). Model id: '{spec.Id}'.");
        }

        string apiKey;
        try
        {
            apiKey = await secrets.ResolveAsync(spec.ApiKeyRef, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"Anthropic secret '{spec.ApiKeyRef}' could not be resolved: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"Anthropic secret '{spec.ApiKeyRef}' resolved to an empty value.");
        }

        var anthropicClient = new AnthropicClient(apiKey);
        IChatClient chatClient = anthropicClient.Messages;
        return new MafCompletionProvider(chatClient, modelId: spec.Id);
    }
}
