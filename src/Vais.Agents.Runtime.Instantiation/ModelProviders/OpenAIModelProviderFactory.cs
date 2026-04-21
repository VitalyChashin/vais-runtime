// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.AI;
using OpenAI;
using Vais.Agents.Ai.MicrosoftAgentFramework;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Instantiation.ModelProviders;

/// <summary>
/// <see cref="IModelProviderFactory"/> for OpenAI — `ModelSpec.Provider == "openai"`.
/// Builds an <see cref="IChatClient"/> via the OpenAI SDK's MEAI bridge and wraps
/// it as a <see cref="MafCompletionProvider"/>. Consumers who prefer Semantic
/// Kernel should register their own <see cref="IModelProviderFactory"/> and
/// inject an <c>SkCompletionProvider</c> instead.
/// </summary>
public sealed class OpenAIModelProviderFactory : IModelProviderFactory
{
    /// <summary>Provider name (case-insensitive match against <c>ModelSpec.Provider</c>).</summary>
    public const string ProviderName = "openai";

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
                $"OpenAI ModelSpec requires 'ApiKeyRef' (a secret:// URI resolving to an API key). Model id: '{spec.Id}'.");
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
                $"OpenAI secret '{spec.ApiKeyRef}' could not be resolved: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"OpenAI secret '{spec.ApiKeyRef}' resolved to an empty value.");
        }

        var openAiClient = new OpenAIClient(apiKey);
        IChatClient chatClient = openAiClient.GetChatClient(spec.Id).AsIChatClient();
        return new MafCompletionProvider(chatClient, modelId: spec.Id);
    }
}
